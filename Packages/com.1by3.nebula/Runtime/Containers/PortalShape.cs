using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    internal enum PortalShapeKind : byte { Box, Sphere, Capsule, Mesh }

    /// <summary>
    /// What a proxy collider is, with the scale of the collider it copies already folded into its dimensions (<c>docs/portals.md</c> D26): two
    /// colliders with the same key are interchangeable, so a proxy built for one can stand in for the other. This is what proxies are pooled by.
    /// </summary>
    internal readonly struct PortalShapeKey : IEquatable<PortalShapeKey>
    {
        public readonly PortalShapeKind Kind;
        /// <summary>Box: the size. Sphere: the radius in <see cref="A"/>. Capsule: radius and height in <see cref="A"/> and <see cref="B"/>. Mesh: the scale.</summary>
        public readonly float A, B, C;
        /// <summary>A capsule's axis: 0, 1 or 2.</summary>
        public readonly byte Axis;
        /// <summary>A mesh's instance id (0 for the others).</summary>
        /// <summary>A mesh shape's mesh (reference identity); null for the others.</summary>
        public readonly UnityEngine.Object Mesh;
        public readonly bool Convex;

        public PortalShapeKey(PortalShapeKind kind, float a, float b, float c, byte axis, UnityEngine.Object mesh, bool convex)
        {
            Kind = kind; A = a; B = b; C = c; Axis = axis; Mesh = mesh; Convex = convex;
        }

        public bool Equals(PortalShapeKey o) =>
            Kind == o.Kind && A == o.A && B == o.B && C == o.C && Axis == o.Axis && ReferenceEquals(Mesh, o.Mesh) && Convex == o.Convex;

        public override bool Equals(object obj) => obj is PortalShapeKey o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Kind * 397 ^ Axis;
                h = h * 397 ^ A.GetHashCode();
                h = h * 397 ^ B.GetHashCode();
                h = h * 397 ^ C.GetHashCode();
                h = h * 397 ^ (Mesh != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Mesh) : 0);
                return h * 397 ^ (Convex ? 1 : 0);
            }
        }

        public override string ToString() => $"{Kind} {A:0.###} {B:0.###} {C:0.###}{(Kind == PortalShapeKind.Capsule ? " axis " + Axis : "")}";
    }

    /// <summary>Reading a collider as a <see cref="PortalShapeKey"/>, and making a collider of one (<c>docs/portals.md</c> D26).</summary>
    internal static class PortalShapes
    {
        private static float Q(float v) => Mathf.Round(v * 10000f) / 10000f;

        /// <summary>
        /// Describe <paramref name="c"/>: a box, sphere, capsule or mesh collider, or a character controller as a capsule. False for a terrain, a trigger
        /// and anything else (never copied). <paramref name="centerLocal"/> is the centre of the shape in the collider's own transform space, so a proxy
        /// can be posed at the shape's centre with no offset of its own; <paramref name="upright"/> is true for a character controller, whose capsule does
        /// not turn with its transform.
        /// </summary>
        public static bool TryDescribe(Collider c, out PortalShapeKey key, out Vector3 centerLocal, out bool upright)
        {
            key = default;
            centerLocal = default;
            upright = false;
            if (c == null) return false;
            var s = c.transform.lossyScale;
            var a = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            switch (c)
            {
                case BoxCollider box:
                    key = new PortalShapeKey(PortalShapeKind.Box, Q(box.size.x * a.x), Q(box.size.y * a.y), Q(box.size.z * a.z), 0, null, true);
                    centerLocal = box.center;
                    return true;
                case SphereCollider sphere:
                    key = new PortalShapeKey(PortalShapeKind.Sphere, Q(sphere.radius * Mathf.Max(a.x, Mathf.Max(a.y, a.z))), 0f, 0f, 0, null, true);
                    centerLocal = sphere.center;
                    return true;
                case CapsuleCollider capsule:
                    return Capsule(capsule.radius, capsule.height, capsule.direction, capsule.center, a, out key, out centerLocal);
                case CharacterController controller:
                    upright = true;
                    return Capsule(controller.radius, controller.height, 1, controller.center, a, out key, out centerLocal);
                case MeshCollider mesh when mesh.sharedMesh != null:
                    key = new PortalShapeKey(PortalShapeKind.Mesh, Q(a.x), Q(a.y), Q(a.z), 0, mesh.sharedMesh, mesh.convex);
                    centerLocal = Vector3.zero;
                    return true;
                default:
                    return false;
            }
        }

        private static bool Capsule(float radius, float height, int direction, Vector3 center, Vector3 scale, out PortalShapeKey key, out Vector3 centerLocal)
        {
            int axis = Mathf.Clamp(direction, 0, 2);
            float along = scale[axis];
            float round = 0f;
            for (int i = 0; i < 3; i++) if (i != axis) round = Mathf.Max(round, scale[i]);
            float r = radius * round;
            float h = Mathf.Max(height * along, 2f * r);
            key = new PortalShapeKey(PortalShapeKind.Capsule, Q(r), Q(h), 0f, (byte)axis, null, true);
            centerLocal = center;
            return true;
        }

        /// <summary>Put a collider of this shape on <paramref name="go"/>. A mesh shape needs the mesh of <paramref name="source"/>; the others read nothing from it.</summary>
        public static Collider AddCollider(GameObject go, in PortalShapeKey key, Collider source)
        {
            switch (key.Kind)
            {
                case PortalShapeKind.Box:
                {
                    var c = go.AddComponent<BoxCollider>();
                    c.size = new Vector3(key.A, key.B, key.C);
                    return c;
                }
                case PortalShapeKind.Sphere:
                {
                    var c = go.AddComponent<SphereCollider>();
                    c.radius = key.A;
                    return c;
                }
                case PortalShapeKind.Capsule:
                {
                    var c = go.AddComponent<CapsuleCollider>();
                    c.direction = key.Axis;
                    c.radius = key.A;
                    c.height = key.B;
                    return c;
                }
                default:
                {
                    var c = go.AddComponent<MeshCollider>();
                    var from = source as MeshCollider;
                    c.sharedMesh = from != null ? from.sharedMesh : null;
                    c.convex = key.Convex;
                    if (from != null) c.cookingOptions = from.cookingOptions;
                    go.transform.localScale = new Vector3(key.A, key.B, key.C);
                    return c;
                }
            }
        }
    }

    /// <summary>
    /// Proxies kept by shape so that a body entering a band takes one that is already built (<c>docs/portals.md</c> D26): <see cref="Acquire"/> reuses a
    /// released one of the same shape in the same scene and builds only when none is. <typeparamref name="T"/> is what a game of this class pools; the
    /// class knows nothing of Unity, so its rules are tested without a scene.
    /// </summary>
    internal sealed class PortalShapePool<T> where T : class
    {
        private readonly Dictionary<(ulong scene, PortalShapeKey key), Stack<T>> _free = new Dictionary<(ulong, PortalShapeKey), Stack<T>>();

        /// <summary>How many times <see cref="Acquire"/> had to build.</summary>
        public int Built { get; private set; }
        /// <summary>How many times <see cref="Acquire"/> reused one.</summary>
        public int Reused { get; private set; }
        /// <summary>How many are waiting to be reused.</summary>
        public int Free { get; private set; }

        /// <summary>A free item of this shape in this scene, or a new one from <paramref name="make"/>.</summary>
        public T Acquire(ulong scene, in PortalShapeKey key, Func<T> make)
        {
            if (_free.TryGetValue((scene, key), out var stack) && stack.Count > 0)
            {
                Reused++;
                Free--;
                return stack.Pop();
            }
            Built++;
            return make();
        }

        /// <summary>How many free items of this shape are waiting in this scene.</summary>
        public int FreeCount(ulong scene, in PortalShapeKey key) => _free.TryGetValue((scene, key), out var stack) ? stack.Count : 0;

        /// <summary>Add an item that was built ahead of need (<see cref="Built"/> counts it) to the free items of its shape.</summary>
        public void Add(ulong scene, in PortalShapeKey key, T item)
        {
            Built++;
            Release(scene, key, item);
        }

        /// <summary>Whether a free item of this shape is waiting in this scene.</summary>
        public bool Has(ulong scene, in PortalShapeKey key) => _free.TryGetValue((scene, key), out var stack) && stack.Count > 0;

        /// <summary>Put an item back for the next body of this shape.</summary>
        public void Release(ulong scene, in PortalShapeKey key, T item)
        {
            if (!_free.TryGetValue((scene, key), out var stack)) _free.Add((scene, key), stack = new Stack<T>(4));
            stack.Push(item);
            Free++;
        }

        /// <summary>Every free item, for destruction; the pool is empty afterwards.</summary>
        public void Drain(List<T> into)
        {
            foreach (var stack in _free.Values) { while (stack.Count > 0) into.Add(stack.Pop()); }
            _free.Clear();
            Free = 0;
        }

        /// <summary>Every free item in one scene, for destruction.</summary>
        public void DrainScene(ulong scene, List<T> into)
        {
            List<(ulong, PortalShapeKey)> keys = null;
            foreach (var kv in _free)
            {
                if (kv.Key.scene != scene) continue;
                while (kv.Value.Count > 0) { into.Add(kv.Value.Pop()); Free--; }
                (keys ??= new List<(ulong, PortalShapeKey)>()).Add(kv.Key);
            }
            if (keys != null) foreach (var k in keys) _free.Remove(k);
        }
    }
}
