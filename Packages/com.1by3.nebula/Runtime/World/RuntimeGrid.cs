using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// Opt-in helper for a game whose runtime containers are cells of a procedural, unbounded 3D grid (an
    /// "infinite chunked world") rather than a fixed set the game enumerates itself. Nebula still does not decide
    /// the game's world architecture (that stays the game's job, see <c>docs/dynamic-worlds.md</c>); this only
    /// packages the arithmetic every such game was writing by hand: a stable 64-bit id per cell, cell bounds in the
    /// current floating-origin frame, and neighbourhood queries.
    /// <para>
    /// The id packing (three signed 21-bit fields) is fixed and must never change: ids are persisted (a runtime
    /// container id is a control-plane lease key and may be a database key in a game's own persistence). It matches
    /// what Holospace's <c>WorldChunks.IdOf</c>/<c>CoordOf</c> already produce, so existing persisted ids keep
    /// resolving to the same cell after adopting this type.
    /// </para>
    /// </summary>
    public sealed class RuntimeGrid
    {
        /// <summary>Coordinates outside [MinCoordinate, MaxCoordinate] on any axis cannot be packed into a runtime id.</summary>
        public const int MinCoordinate = -(1 << 20);
        public const int MaxCoordinate = (1 << 20) - 1;
        private const ulong Mask = (1UL << 21) - 1;

        /// <summary>Size of one cell in metres, per axis.</summary>
        public Vector3 CellSize { get; }

        public RuntimeGrid(float cellSize) : this(new Vector3(cellSize, cellSize, cellSize)) { }

        public RuntimeGrid(Vector3 cellSize)
        {
            if (cellSize.x <= 0f || cellSize.y <= 0f || cellSize.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive on every axis.");
            CellSize = cellSize;
        }

        /// <summary>Whether a coordinate is within the range that <see cref="PackId"/> can represent.</summary>
        public static bool IsValid(Vector3Int c) => c.x >= MinCoordinate && c.x <= MaxCoordinate &&
            c.y >= MinCoordinate && c.y <= MaxCoordinate && c.z >= MinCoordinate && c.z <= MaxCoordinate;

        /// <summary>
        /// Pack a grid coordinate into a stable 64-bit runtime container id: three signed 21-bit fields, x in the
        /// high bits. Bit-for-bit identical to Holospace's <c>WorldChunks.IdOf</c> (pinned by
        /// <c>RuntimeGridTests</c>) because ids are already persisted; this packing must never change.
        /// </summary>
        public static ulong PackId(Vector3Int coord)
        {
            if (!IsValid(coord)) throw new ArgumentOutOfRangeException(nameof(coord), "Grid coordinate exceeds the packed runtime id range.");
            return (((ulong)(uint)coord.x & Mask) << 42) | (((ulong)(uint)coord.y & Mask) << 21) | ((ulong)(uint)coord.z & Mask);
        }

        private static int Signed(ulong value)
        {
            int c = (int)(value & Mask);
            return c >= (1 << 20) ? c - (1 << 21) : c;
        }

        /// <summary>Inverse of <see cref="PackId"/>.</summary>
        public static Vector3Int UnpackId(ulong id) => new Vector3Int(Signed(id >> 42), Signed(id >> 21), Signed(id));

        /// <summary>Which cell a position in the current floating-origin frame falls in.</summary>
        public Vector3Int CoordOf(Vector3 framePosition) => new Vector3Int(
            WorldOrigin.Cell.x + Mathf.FloorToInt(framePosition.x / CellSize.x),
            WorldOrigin.Cell.y + Mathf.FloorToInt(framePosition.y / CellSize.y),
            WorldOrigin.Cell.z + Mathf.FloorToInt(framePosition.z / CellSize.z));

        /// <summary>
        /// Which cell an entity is in: its container's cell (if it sits in a runtime container registered by this
        /// grid) offset by its local position within that container, or its frame position otherwise.
        /// </summary>
        public Vector3Int CoordOf(NetworkIdentity entity)
        {
            if (entity.Container == null || !entity.Container.IsRuntime) return CoordOf(entity.transform.position);
            var c = UnpackId(entity.Container.RuntimeId);
            var local = entity.LocalPosition;
            return c + new Vector3Int(
                Mathf.FloorToInt((local.x + CellSize.x / 2) / CellSize.x),
                Mathf.FloorToInt((local.y + CellSize.y / 2) / CellSize.y),
                Mathf.FloorToInt((local.z + CellSize.z / 2) / CellSize.z));
        }

        /// <summary>Centre of a cell in the current floating-origin frame.</summary>
        public Vector3 CenterOf(Vector3Int coord) => new Vector3(
            (float)(((long)coord.x - WorldOrigin.Cell.x + 0.5) * CellSize.x),
            (float)(((long)coord.y - WorldOrigin.Cell.y + 0.5) * CellSize.y),
            (float)(((long)coord.z - WorldOrigin.Cell.z + 0.5) * CellSize.z));

        /// <summary>Box of a cell in the current floating-origin frame.</summary>
        public Bounds BoundsOf(Vector3Int coord) => new Bounds(CenterOf(coord), CellSize);

        /// <summary><see cref="BoundsOf"/> of the cell a packed runtime id names. Matches the
        /// <see cref="ContainerRegistry.RuntimeBoundsInFrame"/> delegate signature; see <see cref="UseAsRuntimeBounds"/>.</summary>
        public Bounds BoundsOfId(ulong id, Bounds fallback) => BoundsOf(UnpackId(id));

        /// <summary>Chebyshev (ring) distance between two coordinates, independent of cell size.</summary>
        public static bool IsNear(Vector3Int a, Vector3Int b, int ring) =>
            Math.Abs((long)a.x - b.x) <= ring && Math.Abs((long)a.y - b.y) <= ring && Math.Abs((long)a.z - b.z) <= ring;

        /// <summary>Every valid coordinate within <paramref name="ring"/> cells of <paramref name="center"/> on every axis (a cube).</summary>
        public static IEnumerable<Vector3Int> Neighborhood(Vector3Int center, int ring)
        {
            for (int x = -ring; x <= ring; x++)
                for (int y = -ring; y <= ring; y++)
                    for (int z = -ring; z <= ring; z++)
                    {
                        var c = center + new Vector3Int(x, y, z);
                        if (IsValid(c)) yield return c;
                    }
        }

        /// <summary>
        /// Point <see cref="ContainerRegistry.RuntimeBoundsInFrame"/> at this grid, so a game that registers runtime
        /// containers by <see cref="PackId"/>'d coordinate no longer needs to supply the bounds hook itself. Games
        /// with their own container shape keep using the hook directly; this is purely opt-in and touches nothing
        /// else. Call once at boot (mirrors the pattern a game previously wrote inline), and clear
        /// (<c>ContainerRegistry.RuntimeBoundsInFrame = null</c>) on shutdown if another hook should take over.
        /// </summary>
        public void UseAsRuntimeBounds() => ContainerRegistry.RuntimeBoundsInFrame = BoundsOfId;

        private static readonly List<CharacterController> Suspended = new List<CharacterController>();

        /// <summary>
        /// Opt-in floating-origin policy for a grid world: shift the origin to <paramref name="target"/> (a no-op
        /// when it is already there, or when no runtime world is loaded), suspending enabled
        /// <see cref="CharacterController"/>s on entities of runtime containers across the shift and syncing
        /// physics transforms after. PhysX controllers must be reinserted at the new pose, not sweep over the
        /// shift. Call from the game's own update once it has decided where the origin belongs, or use
        /// <see cref="KeepOriginNear"/> for the usual policy.
        /// </summary>
        public static void ShiftOriginTo(Vector3Int target)
        {
            if (NebulaWorld.Streamer == null || target == WorldOrigin.Cell) return;
            Suspended.Clear();
            foreach (var c in ContainerRegistry.Runtime)
                foreach (var entity in c.Entities)
                {
                    if (entity == null) continue;
                    var controller = entity.GetComponent<CharacterController>();
                    if (controller != null && controller.enabled) { controller.enabled = false; Suspended.Add(controller); }
                }
            NebulaWorld.Streamer.ShiftOrigin(target);
            foreach (var controller in Suspended) if (controller != null) controller.enabled = true;
            Suspended.Clear();
            Physics.SyncTransforms();
        }

        /// <summary>
        /// Keep the floating origin within <paramref name="ring"/> cells of <paramref name="entity"/> — the usual
        /// policy for a client following its local player. Shifts through <see cref="ShiftOriginTo"/> when the
        /// entity leaves that ring around <see cref="WorldOrigin.Cell"/>, and does nothing otherwise.
        /// </summary>
        public void KeepOriginNear(NetworkIdentity entity, int ring = 1)
        {
            if (entity == null) return;
            var cell = CoordOf(entity);
            if (!IsNear(cell, WorldOrigin.Cell, ring)) ShiftOriginTo(cell);
        }
    }
}
