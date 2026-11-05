using System;
using System.Text.Json.Serialization;

namespace EnterpriseBIM.Core.Enums
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BimElementClassification : int
    {
        Undefined = 0,
        Wall = 1,
        Floor = 2,
        StructuralColumn = 3,
        StructuralFraming = 4,
        DirectShapeGeneric = 100,
        DirectShapeSpecialtyEquipment = 101,
        DirectShapeCurtainSystems = 102
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LengthUnitSystem : int
    {
        Millimeters = 0,
        Centimeters = 1,
        Meters = 2,
        DecimalInches = 3,
        DecimalFeet = 4
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GeometryRepresentationKind : int
    {
        CurveExtrusion = 0,
        ClosedPlanarBoundary = 1,
        TriangulatedManifoldMesh = 2
    }
}
