using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using EnterpriseBIM.Core.Contracts;
using EnterpriseBIM.Core.Enums;
using EnterpriseBIM.Core.Primitives;
using EnterpriseBIM.Core.Serialization;

namespace EnterpriseBIM.Revit.Reconstruction
{
    public sealed class RevitElementReconstructor
    {
        private readonly Document _doc;
        private readonly double _scaleToFeet;
        private readonly double _shortCurveTolerance;
        private readonly Dictionary<string, Level> _levelsByName;
        private readonly Level _fallbackLevel;
        private readonly Dictionary<string, WallType> _wallTypesByName;
        private readonly WallType _fallbackWallType;
        private readonly Dictionary<string, FloorType> _floorTypesByName;
        private readonly FloorType? _fallbackFloorType;

        public RevitElementReconstructor(Document doc, LengthUnitSystem packageUnits)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _scaleToFeet = ExchangePackage.GetUnitToFeetConversionFactor(packageUnits);
            _shortCurveTolerance = _doc.Application.ShortCurveTolerance;

            List<Level> allLevels = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            if (allLevels.Count == 0)
            {
                throw new InvalidOperationException("Target Revit document contains zero Level elements.");
            }

            _fallbackLevel = allLevels[0];
            _levelsByName = allLevels
                .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            List<WallType> allWallTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .Where(wt => wt.Kind == WallKind.Basic)
                .ToList();

            if (allWallTypes.Count == 0)
            {
                throw new InvalidOperationException("Target Revit document contains no Basic WallTypes.");
            }

            _fallbackWallType = allWallTypes[0];
            _wallTypesByName = allWallTypes
                .GroupBy(wt => wt.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            List<FloorType> allFloorTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .Where(ft => !ft.IsFoundationSlab)
                .ToList();

            _fallbackFloorType = allFloorTypes.FirstOrDefault();
            _floorTypesByName = allFloorTypes
                .GroupBy(ft => ft.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<ElementId> ReconstructPayload(BimElementPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);

            List<Element> createdElements = new();

            if (payload.Classification == BimElementClassification.Wall &&
                payload.RepresentationKind == GeometryRepresentationKind.CurveExtrusion &&
                payload.AxisCurve is not null)
            {
                IReadOnlyList<Wall> walls = ReconstructParametricWalls(payload);
                createdElements.AddRange(walls);
            }
            else if (payload.Classification == BimElementClassification.Floor &&
                     payload.RepresentationKind == GeometryRepresentationKind.ClosedPlanarBoundary &&
                     !payload.BoundaryLoops.IsEmpty &&
                     _fallbackFloorType is not null)
            {
                Floor? floor = TryReconstructParametricFloor(payload);
                if (floor is not null)
                {
                    createdElements.Add(floor);
                }
            }

            if (createdElements.Count == 0)
            {
                DirectShape directShape = ReconstructDirectShapeFallback(payload);
                createdElements.Add(directShape);
            }

            foreach (Element element in createdElements)
            {
                ApplyMetadataParameters(element, payload);
            }

            return createdElements.Select(e => e.Id).ToList();
        }

        private IReadOnlyList<Wall> ReconstructParametricWalls(BimElementPayload payload)
        {
            Polyline3D axis = payload.AxisCurve!;
            IReadOnlyList<Line> segments = BuildValidatedRevitLines(axis);
            if (segments.Count == 0)
            {
                throw new InvalidOperationException($"Wall payload '{payload.SourceObjectId}' contains no curve segments exceeding Revit ShortCurveTolerance.");
            }

            Level targetLevel = ResolveLevel(payload.TargetLevelName);
            WallType targetWallType = ResolveWallType(payload.TargetRevitTypeName);

            double heightFeet = Math.Max(payload.UnconnectedHeight * _scaleToFeet, 1.0);
            double offsetFeet = payload.BaseOffset * _scaleToFeet;

            List<Wall> createdWalls = new(segments.Count);
            foreach (Line lineSegment in segments)
            {
                Wall wall = Wall.Create(
                    _doc,
                    lineSegment,
                    targetWallType.Id,
                    targetLevel.Id,
                    heightFeet,
                    offsetFeet,
                    flip: false,
                    structural: payload.IsStructural);

                createdWalls.Add(wall);
            }

            return createdWalls;
        }

        private Floor? TryReconstructParametricFloor(BimElementPayload payload)
        {
            try
            {
                Level targetLevel = ResolveLevel(payload.TargetLevelName);
                FloorType floorType = _floorTypesByName.TryGetValue(payload.TargetRevitTypeName, out FloorType? matched)
                    ? matched
                    : _fallbackFloorType!;

                List<CurveLoop> loops = new(payload.BoundaryLoops.Length);
                foreach (Polyline3D poly in payload.BoundaryLoops)
                {
                    IReadOnlyList<Line> lines = BuildValidatedRevitLines(poly, forceClose: true);
                    if (lines.Count < 3)
                    {
                        continue;
                    }
                    CurveLoop curveLoop = new();
                    foreach (Line line in lines)
                    {
                        curveLoop.Append(line);
                    }
                    if (!curveLoop.IsOpen())
                    {
                        loops.Add(curveLoop);
                    }
                }

                if (loops.Count == 0)
                {
                    return null;
                }

                Floor floor = Floor.Create(_doc, loops, floorType.Id, targetLevel.Id);
                Parameter? offsetParam = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (offsetParam is not null && !offsetParam.IsReadOnly)
                {
                    offsetParam.Set(payload.BaseOffset * _scaleToFeet);
                }

                return floor;
            }
            catch (Exception ex)
            {
                ExchangeStreamSerializer.AppendAuditLog(
                    "EnterpriseBIM.Revit.Reconstructor",
                    "WARN",
                    $"Parametric floor generation failed for '{payload.SourceObjectId}', falling back to DirectShape: {ex.Message}");
                return null;
            }
        }

        private DirectShape ReconstructDirectShapeFallback(BimElementPayload payload)
        {
            BuiltInCategory targetCategory = ResolveBuiltInCategory(payload.Classification);
            ElementId categoryId = new(targetCategory);

            if (!DirectShape.IsValidCategoryId(categoryId, _doc))
            {
                categoryId = new ElementId(BuiltInCategory.OST_GenericModel);
            }

            IList<GeometryObject> geometryObjects = BuildDirectShapeGeometryObjects(payload);
            if (geometryObjects.Count == 0)
            {
                throw new InvalidOperationException($"Failed to construct valid TessellatedShape or Curve geometry for DirectShape '{payload.SourceObjectId}'.");
            }

            DirectShape ds = DirectShape.CreateElement(_doc, categoryId);
            ds.ApplicationId = "EnterpriseBIM.Pipeline.v1";
            ds.ApplicationDataId = payload.SourceObjectId.ToString("D");
            ds.Name = string.IsNullOrWhiteSpace(payload.ElementName)
                ? $"EnterpriseBIM_{payload.SourceObjectId:N}"
                : payload.ElementName;

            ds.SetShape(geometryObjects);
            return ds;
        }

        private IList<GeometryObject> BuildDirectShapeGeometryObjects(BimElementPayload payload)
        {
            List<GeometryObject> results = new();

            if (!payload.Meshes.IsEmpty)
            {
                foreach (Mesh3D meshDto in payload.Meshes)
                {
                    IList<GeometryObject> builtShapes = BuildTessellatedShapeFromMesh(meshDto);
                    foreach (GeometryObject geomObj in builtShapes)
                    {
                        results.Add(geomObj);
                    }
                }
            }
            else if (payload.AxisCurve is not null)
            {
                IReadOnlyList<Line> lines = BuildValidatedRevitLines(payload.AxisCurve);
                foreach (Line line in lines)
                {
                    results.Add(line);
                }
            }

            return results;
        }

        private IList<GeometryObject> BuildTessellatedShapeFromMesh(Mesh3D meshDto)
        {
            TessellatedShapeBuilder builder = new();
            builder.OpenConnectedFaceSet(isSolid: meshDto.IsClosedManifold);

            ElementId materialId = ElementId.InvalidElementId;
            ImmutableArray<Point3D> vertices = meshDto.Vertices;
            int validFaceCount = 0;

            foreach (MeshFaceIndex face in meshDto.Faces)
            {
                if (face.A < 0 || face.A >= vertices.Length ||
                    face.B < 0 || face.B >= vertices.Length ||
                    face.C < 0 || face.C >= vertices.Length)
                {
                    continue;
                }

                XYZ ptA = ToRevitXyz(vertices[face.A]);
                XYZ ptB = ToRevitXyz(vertices[face.B]);
                XYZ ptC = ToRevitXyz(vertices[face.C]);

                if (IsValidTriangle(ptA, ptB, ptC))
                {
                    TessellatedFace triangleOne = new(new List<XYZ>(3) { ptA, ptB, ptC }, materialId);
                    if (builder.DoesFaceHaveEnoughLoopsAndVertices(triangleOne))
                    {
                        builder.AddFace(triangleOne);
                        validFaceCount++;
                    }
                }

                if (face.IsQuad && face.D!.Value >= 0 && face.D.Value < vertices.Length)
                {
                    XYZ ptD = ToRevitXyz(vertices[face.D.Value]);
                    if (IsValidTriangle(ptA, ptC, ptD))
                    {
                        TessellatedFace triangleTwo = new(new List<XYZ>(3) { ptA, ptC, ptD }, materialId);
                        if (builder.DoesFaceHaveEnoughLoopsAndVertices(triangleTwo))
                        {
                            builder.AddFace(triangleTwo);
                            validFaceCount++;
                        }
                    }
                }
            }

            builder.CloseConnectedFaceSet();

            if (validFaceCount == 0)
            {
                return Array.Empty<GeometryObject>();
            }

            builder.Target = meshDto.IsClosedManifold
                ? TessellatedShapeBuilderTarget.AnyGeometry
                : TessellatedShapeBuilderTarget.Mesh;
            builder.Fallback = TessellatedShapeBuilderFallback.Mesh;

            builder.Build();
            TessellatedShapeBuilderResult result = builder.GetBuildResult();

            if (result.Outcome == TessellatedShapeBuilderOutcome.Nothing)
            {
                return Array.Empty<GeometryObject>();
            }

            return result.GetGeometricalObjects();
        }

        private bool IsValidTriangle(XYZ a, XYZ b, XYZ c)
        {
            double ab = a.DistanceTo(b);
            double bc = b.DistanceTo(c);
            double ca = c.DistanceTo(a);

            if (ab <= _shortCurveTolerance || bc <= _shortCurveTolerance || ca <= _shortCurveTolerance)
            {
                return false;
            }

            XYZ cross = (b - a).CrossProduct(c - a);
            return cross.GetLength() > (_shortCurveTolerance * _shortCurveTolerance);
        }

        private IReadOnlyList<Line> BuildValidatedRevitLines(Polyline3D polyline, bool forceClose = false)
        {
            List<XYZ> filteredPoints = new(polyline.Vertices.Length);
            foreach (Point3D pt in polyline.Vertices)
            {
                XYZ current = ToRevitXyz(pt);
                if (filteredPoints.Count == 0 || filteredPoints[^1].DistanceTo(current) > _shortCurveTolerance)
                {
                    filteredPoints.Add(current);
                }
            }

            bool closeLoop = polyline.IsClosed || forceClose;
            if (closeLoop && filteredPoints.Count > 2 && filteredPoints[^1].DistanceTo(filteredPoints[0]) <= _shortCurveTolerance)
            {
                filteredPoints.RemoveAt(filteredPoints.Count - 1);
            }

            List<Line> lines = new();
            for (int i = 0; i < filteredPoints.Count - 1; i++)
            {
                lines.Add(Line.CreateBound(filteredPoints[i], filteredPoints[i + 1]));
            }

            if (closeLoop && filteredPoints.Count >= 3)
            {
                lines.Add(Line.CreateBound(filteredPoints[^1], filteredPoints[0]));
            }

            return lines;
        }

        private void ApplyMetadataParameters(Element element, BimElementPayload payload)
        {
            Parameter? commentsParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (commentsParam is not null && !commentsParam.IsReadOnly)
            {
                string commentPayload = $"SourceId={payload.SourceObjectId:D};Layer={payload.Layer.FullPath}";
                commentsParam.Set(commentPayload);
            }

            Parameter? markParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (markParam is not null && !markParam.IsReadOnly && !string.IsNullOrWhiteSpace(payload.ElementName))
            {
                markParam.Set(payload.ElementName);
            }

            foreach (KeyValuePair<string, string> kvp in payload.UserAttributes)
            {
                Parameter? customParam = element.LookupParameter(kvp.Key);
                if (customParam is null || customParam.IsReadOnly)
                {
                    continue;
                }

                switch (customParam.StorageType)
                {
                    case StorageType.String:
                        customParam.Set(kvp.Value);
                        break;
                    case StorageType.Integer:
                        if (int.TryParse(kvp.Value, out int intVal))
                        {
                            customParam.Set(intVal);
                        }
                        break;
                    case StorageType.Double:
                        if (double.TryParse(kvp.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dblVal))
                        {
                            customParam.Set(dblVal);
                        }
                        break;
                }
            }
        }

        private XYZ ToRevitXyz(Point3D pt) =>
            new(pt.X * _scaleToFeet, pt.Y * _scaleToFeet, pt.Z * _scaleToFeet);

        private Level ResolveLevel(string requestedLevelName) =>
            !string.IsNullOrWhiteSpace(requestedLevelName) && _levelsByName.TryGetValue(requestedLevelName, out Level? level)
                ? level
                : _fallbackLevel;

        private WallType ResolveWallType(string requestedTypeName) =>
            !string.IsNullOrWhiteSpace(requestedTypeName) && _wallTypesByName.TryGetValue(requestedTypeName, out WallType? wallType)
                ? wallType
                : _fallbackWallType;

        private static BuiltInCategory ResolveBuiltInCategory(BimElementClassification classification) => classification switch
        {
            BimElementClassification.Wall => BuiltInCategory.OST_Walls,
            BimElementClassification.Floor => BuiltInCategory.OST_Floors,
            BimElementClassification.StructuralColumn => BuiltInCategory.OST_StructuralColumns,
            BimElementClassification.StructuralFraming => BuiltInCategory.OST_StructuralFraming,
            BimElementClassification.DirectShapeSpecialtyEquipment => BuiltInCategory.OST_SpecialityEquipment,
            BimElementClassification.DirectShapeCurtainSystems => BuiltInCategory.OST_Curtain_Systems,
            _ => BuiltInCategory.OST_GenericModel
        };
    }
}
