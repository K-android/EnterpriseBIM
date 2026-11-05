using System;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using EnterpriseBIM.Core.Enums;
using EnterpriseBIM.Core.Primitives;

namespace EnterpriseBIM.Core.Contracts
{
    public sealed record LayerMetadata(
        [property: JsonPropertyName("layerId")] Guid LayerId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("fullPath")] string FullPath,
        [property: JsonPropertyName("colorArgb")] int ColorArgb);

    public sealed record BimElementPayload
    {
        [JsonPropertyName("sourceObjectId")]
        public Guid SourceObjectId { get; init; }

        [JsonPropertyName("elementName")]
        public string ElementName { get; init; } = string.Empty;

        [JsonPropertyName("classification")]
        public BimElementClassification Classification { get; init; }

        [JsonPropertyName("representationKind")]
        public GeometryRepresentationKind RepresentationKind { get; init; }

        [JsonPropertyName("targetRevitTypeName")]
        public string TargetRevitTypeName { get; init; } = string.Empty;

        [JsonPropertyName("targetLevelName")]
        public string TargetLevelName { get; init; } = string.Empty;

        [JsonPropertyName("unconnectedHeight")]
        public double UnconnectedHeight { get; init; }

        [JsonPropertyName("baseOffset")]
        public double BaseOffset { get; init; }

        [JsonPropertyName("isStructural")]
        public bool IsStructural { get; init; }

        [JsonPropertyName("layer")]
        public LayerMetadata Layer { get; init; } = new(Guid.Empty, "Default", "Default", -16777216);

        [JsonPropertyName("worldTransform")]
        public TransformMatrix4x4 WorldTransform { get; init; } = TransformMatrix4x4.Identity;

        [JsonPropertyName("axisCurve")]
        public Polyline3D? AxisCurve { get; init; }

        [JsonPropertyName("boundaryLoops")]
        public ImmutableArray<Polyline3D> BoundaryLoops { get; init; } = ImmutableArray<Polyline3D>.Empty;

        [JsonPropertyName("meshes")]
        public ImmutableArray<Mesh3D> Meshes { get; init; } = ImmutableArray<Mesh3D>.Empty;

        [JsonPropertyName("userAttributes")]
        public ImmutableDictionary<string, string> UserAttributes { get; init; } = ImmutableDictionary<string, string>.Empty;
    }

    public sealed record ExchangePackage
    {
        public const string CurrentSchemaVersion = "1.0.0";

        [JsonPropertyName("packageId")]
        public Guid PackageId { get; init; } = Guid.NewGuid();

        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; init; } = CurrentSchemaVersion;

        [JsonPropertyName("exportedAtUtc")]
        public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("sourceApplication")]
        public string SourceApplication { get; init; } = string.Empty;

        [JsonPropertyName("sourceDocumentPath")]
        public string SourceDocumentPath { get; init; } = string.Empty;

        [JsonPropertyName("authorDomainUser")]
        public string AuthorDomainUser { get; init; } = string.Empty;

        [JsonPropertyName("unitSystem")]
        public LengthUnitSystem UnitSystem { get; init; } = LengthUnitSystem.Millimeters;

        [JsonPropertyName("elements")]
        public ImmutableArray<BimElementPayload> Elements { get; init; } = ImmutableArray<BimElementPayload>.Empty;

        [JsonPropertyName("payloadChecksumSha256")]
        public string PayloadChecksumSha256 { get; init; } = string.Empty;

        public static double GetUnitToFeetConversionFactor(LengthUnitSystem unitSystem) => unitSystem switch
        {
            LengthUnitSystem.Millimeters => 1.0 / 304.8,
            LengthUnitSystem.Centimeters => 1.0 / 30.48,
            LengthUnitSystem.Meters => 1.0 / 0.3048,
            LengthUnitSystem.DecimalInches => 1.0 / 12.0,
            LengthUnitSystem.DecimalFeet => 1.0,
            _ => throw new ArgumentOutOfRangeException(nameof(unitSystem), unitSystem, "Unsupported unit system.")
        };

        public string ComputeDeterministicHash()
        {
            StringBuilder sb = new();
            sb.Append(PackageId.ToString("N")).Append('|');
            sb.Append(SchemaVersion).Append('|');
            sb.Append(UnitSystem).Append('|');
            sb.Append(Elements.Length);

            foreach (BimElementPayload element in Elements)
            {
                sb.Append('|').Append(element.SourceObjectId.ToString("N"));
                sb.Append(':').Append((int)element.Classification);
                sb.Append(':').Append(element.Meshes.Length);
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash);
        }
    }
}
