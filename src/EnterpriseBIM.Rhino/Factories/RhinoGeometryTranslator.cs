using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using EnterpriseBIM.Core.Contracts;
using EnterpriseBIM.Core.Enums;
using EnterpriseBIM.Core.Primitives;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace EnterpriseBIM.Rhino.Factories
{
    public static class RhinoGeometryTranslator
    {
        private const string AttrClassification = "BIM_CLASSIFICATION";
        private const string AttrRevitType = "BIM_REVIT_TYPE";
        private const string AttrRevitLevel = "BIM_REVIT_LEVEL";
        private const string AttrHeight = "BIM_HEIGHT";
        private const string AttrBaseOffset = "BIM_BASE_OFFSET";
        private const string AttrStructural = "BIM_STRUCTURAL";

        public static LengthUnitSystem MapRhinoUnitSystem(UnitSystem rhinoUnits) => rhinoUnits switch
        {
            UnitSystem.Millimeters => LengthUnitSystem.Millimeters,
            UnitSystem.Centimeters => LengthUnitSystem.Centimeters,
            UnitSystem.Meters => LengthUnitSystem.Meters,
            UnitSystem.Inches => LengthUnitSystem.DecimalInches,
            UnitSystem.Feet => LengthUnitSystem.DecimalFeet,
            _ => LengthUnitSystem.Millimeters
        };

        public static double GetRhinoToSupportedUnitScale(UnitSystem docUnits, out LengthUnitSystem mappedUnits)
        {
            switch (docUnits)
            {
                case UnitSystem.Millimeters:
                case UnitSystem.Centimeters:
                case UnitSystem.Meters:
                case UnitSystem.Inches:
                case UnitSystem.Feet:
                    mappedUnits = MapRhinoUnitSystem(docUnits);
                    return 1.0;
                default:
                    mappedUnits = LengthUnitSystem.Millimeters;
                    return RhinoMath.UnitScale(docUnits, UnitSystem.Millimeters);
            }
        }

        public static BimElementPayload? TranslateRhinoObject(
            RhinoObject rhinoObj,
            RhinoDoc doc,
            double unitScaleFactor)
        {
            ArgumentNullException.ThrowIfNull(rhinoObj);
            ArgumentNullException.ThrowIfNull(doc);

            GeometryBase? geometry = rhinoObj.Geometry;
            if (geometry is null || !geometry.IsValid)
            {
                return null;
            }

            ImmutableDictionary<string, string> userDict = ExtractUserTextDictionary(rhinoObj);
            LayerMetadata layerMetadata = ExtractLayerHierarchy(rhinoObj, doc);
            TransformMatrix4x4 orientationMatrix = ComputeObjectOrientationMatrix(geometry, unitScaleFactor);

            BimElementClassification classification = ResolveClassification(geometry, userDict, layerMetadata);
            string targetTypeName = GetAttributeOrDefault(userDict, AttrRevitType, "Generic - 200mm");
            string targetLevelName = GetAttributeOrDefault(userDict, AttrRevitLevel, "Level 1");
            double defaultHeight = 3000.0 * RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem) * unitScaleFactor;
            double unconnectedHeight = ParseDoubleOrDefault(userDict, AttrHeight, defaultHeight);
            double baseOffset = ParseDoubleOrDefault(userDict, AttrBaseOffset, 0.0);
            bool isStructural = ParseBoolOrDefault(userDict, AttrStructural, classification is BimElementClassification.StructuralColumn or BimElementClassification.StructuralFraming);

            double chordTolerance = Math.Max(doc.ModelAbsoluteTolerance * unitScaleFactor, 1e-3);

            if (geometry is Curve curve)
            {
                Polyline3D? polyline = ConvertCurveToPolyline(curve, chordTolerance, unitScaleFactor);
                if (polyline is null)
                {
                    return null;
                }

                GeometryRepresentationKind repKind = curve.IsClosed && curve.IsPlanar(doc.ModelAbsoluteTolerance)
                    ? GeometryRepresentationKind.ClosedPlanarBoundary
                    : GeometryRepresentationKind.CurveExtrusion;

                return new BimElementPayload
                {
                    SourceObjectId = rhinoObj.Id,
                    ElementName = string.IsNullOrWhiteSpace(rhinoObj.Name) ? $"RhinoCurve_{rhinoObj.Id:N}" : rhinoObj.Name,
                    Classification = classification == BimElementClassification.Undefined
                        ? (repKind == GeometryRepresentationKind.ClosedPlanarBoundary ? BimElementClassification.Floor : BimElementClassification.Wall)
                        : classification,
                    RepresentationKind = repKind,
                    TargetRevitTypeName = targetTypeName,
                    TargetLevelName = targetLevelName,
                    UnconnectedHeight = unconnectedHeight,
                    BaseOffset = baseOffset,
                    IsStructural = isStructural,
                    Layer = layerMetadata,
                    WorldTransform = orientationMatrix,
                    AxisCurve = repKind == GeometryRepresentationKind.CurveExtrusion ? polyline : null,
                    BoundaryLoops = repKind == GeometryRepresentationKind.ClosedPlanarBoundary
                        ? ImmutableArray.Create(polyline)
                        : ImmutableArray<Polyline3D>.Empty,
                    Meshes = ImmutableArray<Mesh3D>.Empty,
                    UserAttributes = userDict
                };
            }

            if (geometry is Extrusion extrusion)
            {
                if (classification == BimElementClassification.Wall && extrusion.ProfileCount > 0)
                {
                    Curve? profileCurve = extrusion.Profile3d(0, 0.0);
                    if (profileCurve is not null && !profileCurve.IsClosed)
                    {
                        Polyline3D? axis = ConvertCurveToPolyline(profileCurve, chordTolerance, unitScaleFactor);
                        if (axis is not null)
                        {
                            double extrusionHeight = extrusion.PathStart.DistanceTo(extrusion.PathEnd) * unitScaleFactor;
                            return new BimElementPayload
                            {
                                SourceObjectId = rhinoObj.Id,
                                ElementName = string.IsNullOrWhiteSpace(rhinoObj.Name) ? $"RhinoExtrusion_{rhinoObj.Id:N}" : rhinoObj.Name,
                                Classification = BimElementClassification.Wall,
                                RepresentationKind = GeometryRepresentationKind.CurveExtrusion,
                                TargetRevitTypeName = targetTypeName,
                                TargetLevelName = targetLevelName,
                                UnconnectedHeight = extrusionHeight > 1e-3 ? extrusionHeight : unconnectedHeight,
                                BaseOffset = baseOffset,
                                IsStructural = isStructural,
                                Layer = layerMetadata,
                                WorldTransform = orientationMatrix,
                                AxisCurve = axis,
                                BoundaryLoops = ImmutableArray<Polyline3D>.Empty,
                                Meshes = ImmutableArray<Mesh3D>.Empty,
                                UserAttributes = userDict
                            };
                        }
                    }
                }

                using Brep? extrusionBrep = extrusion.ToBrep(splitKinkyFaces: true);
                if (extrusionBrep is null)
                {
                    return null;
                }

                return TranslateBrepPayload(
                    rhinoObj,
                    extrusionBrep,
                    classification,
                    targetTypeName,
                    targetLevelName,
                    unconnectedHeight,
                    baseOffset,
                    isStructural,
                    layerMetadata,
                    orientationMatrix,
                    userDict,
                    doc.ModelAbsoluteTolerance,
                    unitScaleFactor);
            }

            if (geometry is Brep brep)
            {
                return TranslateBrepPayload(
                    rhinoObj,
                    brep,
                    classification,
                    targetTypeName,
                    targetLevelName,
                    unconnectedHeight,
                    baseOffset,
                    isStructural,
                    layerMetadata,
                    orientationMatrix,
                    userDict,
                    doc.ModelAbsoluteTolerance,
                    unitScaleFactor);
            }

            return null;
        }

        private static BimElementPayload? TranslateBrepPayload(
            RhinoObject rhinoObj,
            Brep brep,
            BimElementClassification classification,
            string targetTypeName,
            string targetLevelName,
            double unconnectedHeight,
            double baseOffset,
            bool isStructural,
            LayerMetadata layerMetadata,
            TransformMatrix4x4 orientationMatrix,
            ImmutableDictionary<string, string> userDict,
            double modelTolerance,
            double unitScaleFactor)
        {
            ImmutableArray<Mesh3D> meshes = ConvertBrepToMeshes(brep, modelTolerance, unitScaleFactor);
            if (meshes.IsEmpty)
            {
                return null;
            }

            BimElementClassification resolvedClass = classification == BimElementClassification.Undefined
                ? BimElementClassification.DirectShapeGeneric
                : classification;

            return new BimElementPayload
            {
                SourceObjectId = rhinoObj.Id,
                ElementName = string.IsNullOrWhiteSpace(rhinoObj.Name) ? $"RhinoBrep_{rhinoObj.Id:N}" : rhinoObj.Name,
                Classification = resolvedClass,
                RepresentationKind = GeometryRepresentationKind.TriangulatedManifoldMesh,
                TargetRevitTypeName = targetTypeName,
                TargetLevelName = targetLevelName,
                UnconnectedHeight = unconnectedHeight,
                BaseOffset = baseOffset,
                IsStructural = isStructural,
                Layer = layerMetadata,
                WorldTransform = orientationMatrix,
                AxisCurve = null,
                BoundaryLoops = ImmutableArray<Polyline3D>.Empty,
                Meshes = meshes,
                UserAttributes = userDict
            };
        }

        private static Polyline3D? ConvertCurveToPolyline(Curve curve, double tolerance, double unitScaleFactor)
        {
            if (curve.TryGetPolyline(out Polyline nativePolyline) && nativePolyline.Count >= 2)
            {
                ImmutableArray<Point3D>.Builder builder = ImmutableArray.CreateBuilder<Point3D>(nativePolyline.Count);
                foreach (Point3d pt in nativePolyline)
                {
                    builder.Add(new Point3D(pt.X * unitScaleFactor, pt.Y * unitScaleFactor, pt.Z * unitScaleFactor));
                }
                return new Polyline3D(builder.ToImmutable(), curve.IsClosed);
            }

            using PolylineCurve? polyCurve = curve.ToPolyline(
                tolerance,
                angleTolerance: RhinoMath.ToRadians(2.0),
                minimumLength: tolerance,
                maximumLength: 0.0);

            if (polyCurve is not null && polyCurve.TryGetPolyline(out Polyline tessellated) && tessellated.Count >= 2)
            {
                ImmutableArray<Point3D>.Builder builder = ImmutableArray.CreateBuilder<Point3D>(tessellated.Count);
                foreach (Point3d pt in tessellated)
                {
                    builder.Add(new Point3D(pt.X * unitScaleFactor, pt.Y * unitScaleFactor, pt.Z * unitScaleFactor));
                }
                return new Polyline3D(builder.ToImmutable(), curve.IsClosed);
            }

            using NurbsCurve? nurbs = curve.ToNurbsCurve();
            if (nurbs is not null && nurbs.Points.Count >= 2)
            {
                ImmutableArray<Point3D>.Builder builder = ImmutableArray.CreateBuilder<Point3D>(nurbs.Points.Count);
                for (int i = 0; i < nurbs.Points.Count; i++)
                {
                    Point3d pt = nurbs.Points[i].Location;
                    builder.Add(new Point3D(pt.X * unitScaleFactor, pt.Y * unitScaleFactor, pt.Z * unitScaleFactor));
                }
                return new Polyline3D(builder.ToImmutable(), curve.IsClosed);
            }

            return null;
        }

        private static ImmutableArray<Mesh3D> ConvertBrepToMeshes(Brep brep, double modelTolerance, double unitScaleFactor)
        {
            MeshingParameters mp = MeshingParameters.QualityRenderMesh;
            mp.Tolerance = modelTolerance;
            mp.MinimumEdgeLength = modelTolerance * 2.0;
            mp.ClosedObjectPostProcess = true;
            mp.JaggedSeams = false;
            mp.SimplePlanes = true;

            Mesh[]? rhinoMeshes = Mesh.CreateFromBrep(brep, mp);
            if (rhinoMeshes is null || rhinoMeshes.Length == 0)
            {
                return ImmutableArray<Mesh3D>.Empty;
            }

            using Mesh unifiedMesh = new();
            foreach (Mesh m in rhinoMeshes)
            {
                if (m is not null && m.IsValid)
                {
                    unifiedMesh.Append(m);
                }
                m?.Dispose();
            }

            unifiedMesh.Weld(Math.PI);
            unifiedMesh.Faces.ConvertNonPlanarQuadsToTriangles(modelTolerance, RhinoMath.ToRadians(1.0), 0);
            unifiedMesh.Faces.ConvertQuadsToTriangles();
            unifiedMesh.Normals.ComputeNormals();
            unifiedMesh.Compact();

            if (unifiedMesh.Vertices.Count < 3 || unifiedMesh.Faces.Count < 1)
            {
                return ImmutableArray<Mesh3D>.Empty;
            }

            ImmutableArray<Point3D>.Builder verticesBuilder = ImmutableArray.CreateBuilder<Point3D>(unifiedMesh.Vertices.Count);
            for (int i = 0; i < unifiedMesh.Vertices.Count; i++)
            {
                Point3d v = unifiedMesh.Vertices.Point3dAt(i);
                verticesBuilder.Add(new Point3D(v.X * unitScaleFactor, v.Y * unitScaleFactor, v.Z * unitScaleFactor));
            }

            ImmutableArray<Vector3D>.Builder normalsBuilder = ImmutableArray.CreateBuilder<Vector3D>(unifiedMesh.Normals.Count);
            for (int i = 0; i < unifiedMesh.Normals.Count; i++)
            {
                Vector3f n = unifiedMesh.Normals[i];
                normalsBuilder.Add(new Vector3D(n.X, n.Y, n.Z).Normalize());
            }

            ImmutableArray<MeshFaceIndex>.Builder facesBuilder = ImmutableArray.CreateBuilder<MeshFaceIndex>(unifiedMesh.Faces.Count);
            for (int i = 0; i < unifiedMesh.Faces.Count; i++)
            {
                MeshFace f = unifiedMesh.Faces[i];
                if (f.A == f.B || f.B == f.C || f.C == f.A)
                {
                    continue;
                }

                if (f.IsQuad && f.C != f.D && f.D != f.A)
                {
                    facesBuilder.Add(new MeshFaceIndex(f.A, f.B, f.C, f.D));
                }
                else
                {
                    facesBuilder.Add(new MeshFaceIndex(f.A, f.B, f.C, null));
                }
            }

            if (facesBuilder.Count == 0)
            {
                return ImmutableArray<Mesh3D>.Empty;
            }

            bool isManifold = unifiedMesh.IsClosed && unifiedMesh.IsManifold(
                topologicalTest: true,
                out bool isOriented,
                out _) && isOriented;

            Mesh3D meshDto = new(
                verticesBuilder.ToImmutable(),
                normalsBuilder.ToImmutable(),
                facesBuilder.ToImmutable(),
                isManifold);

            return ImmutableArray.Create(meshDto);
        }

        private static TransformMatrix4x4 ComputeObjectOrientationMatrix(GeometryBase geometry, double unitScaleFactor)
        {
            BoundingBox bbox = geometry.GetBoundingBox(accurate: true);
            if (!bbox.IsValid)
            {
                return TransformMatrix4x4.Identity;
            }

            Point3d center = bbox.Center;
            Plane worldPlane = Plane.WorldXY;
            Plane objectFrame = new(center, Vector3d.XAxis, Vector3d.YAxis);

            if (geometry is Curve curve && curve.FrameAt(curve.Domain.Min, out Plane curveFrame) && curveFrame.IsValid)
            {
                objectFrame = curveFrame;
            }
            else if (geometry is Extrusion extrusion)
            {
                Transform extXform = extrusion.GetProfileTransformation(0.0);
                if (extXform.IsValid)
                {
                    return ConvertRhinoTransform(extXform, unitScaleFactor);
                }
            }

            Transform planeToPlane = Transform.PlaneToPlane(worldPlane, objectFrame);
            return ConvertRhinoTransform(planeToPlane, unitScaleFactor);
        }

        private static TransformMatrix4x4 ConvertRhinoTransform(Transform xform, double unitScaleFactor)
        {
            ImmutableArray<double> values = ImmutableArray.Create<double>(
                xform.M00, xform.M01, xform.M02, xform.M03 * unitScaleFactor,
                xform.M10, xform.M11, xform.M12, xform.M13 * unitScaleFactor,
                xform.M20, xform.M21, xform.M22, xform.M23 * unitScaleFactor,
                xform.M30, xform.M31, xform.M32, xform.M33);
            return new TransformMatrix4x4(values);
        }

        private static LayerMetadata ExtractLayerHierarchy(RhinoObject rhinoObj, RhinoDoc doc)
        {
            int layerIndex = rhinoObj.Attributes.LayerIndex;
            if (layerIndex >= 0 && layerIndex < doc.Layers.Count)
            {
                Layer layer = doc.Layers[layerIndex];
                if (layer is not null)
                {
                    return new LayerMetadata(
                        layer.Id,
                        layer.Name ?? "Default",
                        layer.FullPath ?? layer.Name ?? "Default",
                        layer.Color.ToArgb());
                }
            }
            return new LayerMetadata(Guid.Empty, "Default", "Default", -16777216);
        }

        private static ImmutableDictionary<string, string> ExtractUserTextDictionary(RhinoObject rhinoObj)
        {
            ImmutableDictionary<string, string>.Builder builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

            NameValueCollection attrStrings = rhinoObj.Attributes.GetUserStrings();
            if (attrStrings is not null)
            {
                foreach (string? key in attrStrings.AllKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        builder[key] = attrStrings[key] ?? string.Empty;
                    }
                }
            }

            NameValueCollection? geomStrings = rhinoObj.Geometry?.GetUserStrings();
            if (geomStrings is not null)
            {
                foreach (string? key in geomStrings.AllKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key) && !builder.ContainsKey(key))
                    {
                        builder[key] = geomStrings[key] ?? string.Empty;
                    }
                }
            }

            return builder.ToImmutable();
        }

        private static BimElementClassification ResolveClassification(
            GeometryBase geometry,
            ImmutableDictionary<string, string> userDict,
            LayerMetadata layerMetadata)
        {
            if (userDict.TryGetValue(AttrClassification, out string? rawClass) &&
                Enum.TryParse(rawClass, ignoreCase: true, out BimElementClassification parsedClass))
            {
                return parsedClass;
            }

            string pathUpper = layerMetadata.FullPath.ToUpperInvariant();
            if (pathUpper.Contains("WALL"))
            {
                return geometry is Curve ? BimElementClassification.Wall : BimElementClassification.DirectShapeGeneric;
            }
            if (pathUpper.Contains("FLOOR") || pathUpper.Contains("SLAB"))
            {
                return geometry is Curve { IsClosed: true } ? BimElementClassification.Floor : BimElementClassification.DirectShapeGeneric;
            }
            if (pathUpper.Contains("COLUMN"))
            {
                return BimElementClassification.StructuralColumn;
            }
            if (pathUpper.Contains("CURTAIN"))
            {
                return BimElementClassification.DirectShapeCurtainSystems;
            }
            if (pathUpper.Contains("EQUIPMENT"))
            {
                return BimElementClassification.DirectShapeSpecialtyEquipment;
            }

            return geometry is Curve
                ? BimElementClassification.Wall
                : BimElementClassification.DirectShapeGeneric;
        }

        private static string GetAttributeOrDefault(ImmutableDictionary<string, string> dict, string key, string fallback) =>
            dict.TryGetValue(key, out string? val) && !string.IsNullOrWhiteSpace(val) ? val.Trim() : fallback;

        private static double ParseDoubleOrDefault(ImmutableDictionary<string, string> dict, string key, double fallback) =>
            dict.TryGetValue(key, out string? val) && double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result)
                ? result
                : fallback;

        private static bool ParseBoolOrDefault(ImmutableDictionary<string, string> dict, string key, bool fallback) =>
            dict.TryGetValue(key, out string? val) && bool.TryParse(val, out bool result)
                ? result
                : fallback;
    }
}
