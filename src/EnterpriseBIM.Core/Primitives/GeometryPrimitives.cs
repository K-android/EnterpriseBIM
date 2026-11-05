using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace EnterpriseBIM.Core.Primitives
{
    public readonly record struct Point3D(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z)
    {
        public static Point3D Origin => new(0.0, 0.0, 0.0);

        public bool IsValid() =>
            !double.IsNaN(X) && !double.IsInfinity(X) &&
            !double.IsNaN(Y) && !double.IsInfinity(Y) &&
            !double.IsNaN(Z) && !double.IsInfinity(Z);

        public double DistanceTo(Point3D other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            double dz = Z - other.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        public Point3D Scale(double factor) => new(X * factor, Y * factor, Z * factor);
    }

    public readonly record struct Vector3D(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z)
    {
        public static Vector3D Zero => new(0.0, 0.0, 0.0);
        public static Vector3D UnitX => new(1.0, 0.0, 0.0);
        public static Vector3D UnitY => new(0.0, 1.0, 0.0);
        public static Vector3D UnitZ => new(0.0, 0.0, 1.0);

        public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

        public bool IsValid() =>
            !double.IsNaN(X) && !double.IsInfinity(X) &&
            !double.IsNaN(Y) && !double.IsInfinity(Y) &&
            !double.IsNaN(Z) && !double.IsInfinity(Z);

        public Vector3D Normalize()
        {
            double len = Length;
            if (len < 1e-12)
            {
                return Zero;
            }
            return new Vector3D(X / len, Y / len, Z / len);
        }
    }

    public sealed record TransformMatrix4x4
    {
        [JsonPropertyName("m")]
        public ImmutableArray<double> Matrix { get; init; }

        [JsonConstructor]
        public TransformMatrix4x4(ImmutableArray<double> matrix)
        {
            if (matrix.IsDefault || matrix.Length != 16)
            {
                throw new ArgumentException("Transformation matrix must contain exactly 16 elements in row-major order.", nameof(matrix));
            }
            Matrix = matrix;
        }

        public static TransformMatrix4x4 Identity => new(ImmutableArray.Create<double>(
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0));

        public Point3D TransformPoint(Point3D pt)
        {
            double x = (Matrix[0] * pt.X) + (Matrix[1] * pt.Y) + (Matrix[2] * pt.Z) + Matrix[3];
            double y = (Matrix[4] * pt.X) + (Matrix[5] * pt.Y) + (Matrix[6] * pt.Z) + Matrix[7];
            double z = (Matrix[8] * pt.X) + (Matrix[9] * pt.Y) + (Matrix[10] * pt.Z) + Matrix[11];
            double w = (Matrix[12] * pt.X) + (Matrix[13] * pt.Y) + (Matrix[14] * pt.Z) + Matrix[15];

            if (Math.Abs(w) > 1e-12 && Math.Abs(w - 1.0) > 1e-12)
            {
                return new Point3D(x / w, y / w, z / w);
            }
            return new Point3D(x, y, z);
        }
    }

    public readonly record struct MeshFaceIndex(
        [property: JsonPropertyName("a")] int A,
        [property: JsonPropertyName("b")] int B,
        [property: JsonPropertyName("c")] int C,
        [property: JsonPropertyName("d")] int? D = null)
    {
        [JsonIgnore]
        public bool IsQuad => D.HasValue && D.Value >= 0;
    }

    public sealed record Polyline3D
    {
        [JsonPropertyName("vertices")]
        public ImmutableArray<Point3D> Vertices { get; init; }

        [JsonPropertyName("isClosed")]
        public bool IsClosed { get; init; }

        [JsonConstructor]
        public Polyline3D(ImmutableArray<Point3D> vertices, bool isClosed)
        {
            if (vertices.IsDefault || vertices.Length < 2)
            {
                throw new ArgumentException("Polyline3D requires at least two vertices.", nameof(vertices));
            }
            Vertices = vertices;
            IsClosed = isClosed;
        }

        public double ComputeTotalLength()
        {
            double length = 0.0;
            for (int i = 0; i < Vertices.Length - 1; i++)
            {
                length += Vertices[i].DistanceTo(Vertices[i + 1]);
            }
            if (IsClosed && Vertices.Length > 2)
            {
                length += Vertices[^1].DistanceTo(Vertices[0]);
            }
            return length;
        }
    }

    public sealed record Mesh3D
    {
        [JsonPropertyName("vertices")]
        public ImmutableArray<Point3D> Vertices { get; init; }

        [JsonPropertyName("normals")]
        public ImmutableArray<Vector3D> Normals { get; init; }

        [JsonPropertyName("faces")]
        public ImmutableArray<MeshFaceIndex> Faces { get; init; }

        [JsonPropertyName("isClosedManifold")]
        public bool IsClosedManifold { get; init; }

        [JsonConstructor]
        public Mesh3D(
            ImmutableArray<Point3D> vertices,
            ImmutableArray<Vector3D> normals,
            ImmutableArray<MeshFaceIndex> faces,
            bool isClosedManifold)
        {
            if (vertices.IsDefault || vertices.Length < 3)
            {
                throw new ArgumentException("Mesh3D must contain at least 3 vertices.", nameof(vertices));
            }
            if (faces.IsDefault || faces.Length < 1)
            {
                throw new ArgumentException("Mesh3D must contain at least 1 face.", nameof(faces));
            }
            Vertices = vertices;
            Normals = normals.IsDefault ? ImmutableArray<Vector3D>.Empty : normals;
            Faces = faces;
            IsClosedManifold = isClosedManifold;
        }
    }
}
