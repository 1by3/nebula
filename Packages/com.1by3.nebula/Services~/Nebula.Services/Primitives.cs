using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using N = System.Numerics;

// Portable value types preserve the Unity wire representation without loading Unity assemblies.
namespace Nebula.ServicePrimitives
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float this[int i] { get => i switch { 0 => x, 1 => y, 2 => z, _ => throw new IndexOutOfRangeException() }; set { switch (i) { case 0: x = value; break; case 1: y = value; break; case 2: z = value; break; default: throw new IndexOutOfRangeException(); } } }
        public static Vector3 zero => new Vector3();
        public static Vector3 one => new Vector3(1, 1, 1);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
        public static Vector3 operator /(Vector3 a, float b) => a * (1 / b);
        public static Vector3 Scale(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
    }
    public struct Vector3Int : IEquatable<Vector3Int>
    {
        public int x, y, z;
        public Vector3Int(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(Vector3Int b) => x == b.x && y == b.y && z == b.z;
        public override bool Equals(object b) => b is Vector3Int v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public static bool operator ==(Vector3Int a, Vector3Int b) => a.Equals(b);
        public static bool operator !=(Vector3Int a, Vector3Int b) => !a.Equals(b);
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        private N.Quaternion Value => new N.Quaternion(x, y, z, w);
        private static Quaternion Of(N.Quaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
        public static Quaternion identity => new Quaternion(0, 0, 0, 1);
        public void Normalize() { this = Normalize(this); }
        public Quaternion normalized => Normalize(this);
        public static Quaternion Normalize(Quaternion q) => q.Value.LengthSquared() < float.Epsilon ? identity : Of(N.Quaternion.Normalize(q.Value));
        public static Quaternion Inverse(Quaternion q) => Of(N.Quaternion.Inverse(q.Value));
        public static float Dot(Quaternion a, Quaternion b) => N.Quaternion.Dot(a.Value, b.Value);
        public static Quaternion operator *(Quaternion a, Quaternion b) => Of(a.Value * b.Value);
        public static Vector3 operator *(Quaternion q, Vector3 v) { var r = N.Vector3.Transform(new N.Vector3(v.x, v.y, v.z), q.Value); return new Vector3(r.X, r.Y, r.Z); }
        public static Quaternion Euler(float x, float y, float z) => Euler(new Vector3(x, y, z));
        public static Quaternion Euler(Vector3 e) => Of(N.Quaternion.CreateFromYawPitchRoll(e.y * MathF.PI / 180, e.x * MathF.PI / 180, e.z * MathF.PI / 180));
        public Vector3 eulerAngles
        {
            get
            {
                var q = normalized;
                float sx = Math.Clamp(2 * (q.w * q.x - q.y * q.z), -1, 1);
                float rx = MathF.Asin(sx), ry, rz;
                if (MathF.Abs(sx) < 0.999999f) { ry = MathF.Atan2(2 * (q.x * q.z + q.w * q.y), 1 - 2 * (q.x * q.x + q.y * q.y)); rz = MathF.Atan2(2 * (q.x * q.y + q.w * q.z), 1 - 2 * (q.x * q.x + q.z * q.z)); }
                else { ry = MathF.Atan2(2 * (q.w * q.y - q.x * q.z), 1 - 2 * (q.y * q.y + q.z * q.z)); rz = 0; }
                return new Vector3(Deg(rx), Deg(ry), Deg(rz));
            }
        }
        private static float Deg(float r) => (r * 180 / MathF.PI + 360) % 360;
    }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }
    public static class ColorUtility
    {
        public static string ToHtmlStringRGB(Color c) => $"{(byte)Math.Clamp(MathF.Round(c.r * 255), 0, 255):X2}{(byte)Math.Clamp(MathF.Round(c.g * 255), 0, 255):X2}{(byte)Math.Clamp(MathF.Round(c.b * 255), 0, 255):X2}";
    }
    public struct Bounds
    {
        public Vector3 center, size;
        public Bounds(Vector3 center, Vector3 size) { this.center = center; this.size = size; }
        public Vector3 min => center - size * 0.5f;
        public Vector3 max => center + size * 0.5f;
        public bool Contains(Vector3 p) => p.x >= min.x && p.x <= max.x && p.y >= min.y && p.y <= max.y && p.z >= min.z && p.z <= max.z;
        public bool Intersects(Bounds b) => min.x <= b.max.x && max.x >= b.min.x && min.y <= b.max.y && max.y >= b.min.y && min.z <= b.max.z && max.z >= b.min.z;
        public void Expand(float amount) { size += Vector3.one * amount; }
    }
    public static class Mathf
    {
        public static float Abs(float x) => MathF.Abs(x);
        public static int Abs(int x) => Math.Abs(x);
        public static float Sqrt(float x) => MathF.Sqrt(x);
        public static int FloorToInt(float x) => (int)MathF.Floor(x);
        public static int RoundToInt(float x) => (int)MathF.Round(x);
        public static int Clamp(int v, int min, int max) => Math.Clamp(v, min, max);
        public static float Clamp(float v, float min, float max) => Math.Clamp(v, min, max);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static ushort FloatToHalf(float x) => BitConverter.HalfToUInt16Bits((Half)x);
        public static float HalfToFloat(ushort x) => (float)BitConverter.UInt16BitsToHalf(x);
    }
    public static class Time
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        public static double realtimeSinceStartupAsDouble => Clock.Elapsed.TotalSeconds;
        public static float realtimeSinceStartup => (float)realtimeSinceStartupAsDouble;
        public static float unscaledTime => realtimeSinceStartup;
    }
    public static class JsonUtility
    {
        public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, ServiceManifest.Json);
    }
    public class TextAsset { public string text; }
    public static class Resources
    {
        public static T Load<T>(string name) where T : TextAsset, new()
        {
            using var stream = typeof(Resources).Assembly.GetManifestResourceStream(name + ".html");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return new T { text = reader.ReadToEnd() };
        }
    }
}
