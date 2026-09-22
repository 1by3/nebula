using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// What one procedural chunk grid is: the cell arithmetic every role must agree on, plus the one chunk that is
    /// brought into being with the scope so a client always has somewhere to arrive. It is the
    /// <see cref="ScopeDefinition.Payload"/> of a <see cref="ScopeKind.Grid"/> scope, which is why it is pure C#
    /// and carries no Unity asset reference: a matchmaker, the orchestrator and a test activate a grid without a
    /// <c>WorldDefinition</c> asset, and every role rebuilds the same grid from the scope row alone.
    /// Design of record: <c>docs/scoped-chunk-grids.md</c>.
    /// </summary>
    [Serializable]
    public sealed class ChunkGridDefinition
    {
        /// <summary>Size of one chunk in metres, per axis. On a <see cref="Planar"/> grid Y is the column's height.</summary>
        public Vector3 CellSize = new Vector3(64f, 512f, 64f);
        /// <summary>Chunks are columns: one layer at y = 0, centred on absolute y = 0 (see <c>RuntimeGrid.Planar</c>).</summary>
        public bool Planar = true;
        /// <summary>Chebyshev ring the worker keeps leased around every pawn; 0 takes the role's own interest-derived ring.</summary>
        public int Ring;
        /// <summary>Seconds an unwanted, unoccupied chunk stays leased; 0 or less takes <c>NebulaConfig.ChunkRetireSeconds</c>.</summary>
        public float RetireSeconds;
        /// <summary>The chunk activation creates: the scope's guaranteed spawn area, and the anchor the allocator keeps.</summary>
        public Vector3Int Anchor;

        /// <summary>The reason this grid cannot be activated, or null when it is well formed.</summary>
        public string Validate()
        {
            if (CellSize.x <= 0f || CellSize.y <= 0f || CellSize.z <= 0f) return "a chunk grid needs a positive cell size on every axis";
            var anchor = Planar ? new Vector3Int(Anchor.x, 0, Anchor.z) : Anchor;
            if (!ChunkKeys.IsValidCoordinate(anchor)) return "the anchor chunk is outside the packable coordinate range";
            if (Ring < 0) return "a chunk grid's ring cannot be negative";
            return null;
        }

        /// <summary>The anchor with the vertical component a <see cref="Planar"/> grid does not have dropped.</summary>
        public Vector3Int NormalizedAnchor() => Planar ? new Vector3Int(Anchor.x, 0, Anchor.z) : Anchor;

        /// <summary>Box of a chunk in <b>absolute</b> world coordinates — the frame lease rows and scope parts use.</summary>
        public Bounds AbsoluteBoundsOf(Vector3Int coord)
        {
            if (Planar) coord = new Vector3Int(coord.x, 0, coord.z);
            var center = new Vector3(
                (float)((coord.x + 0.5) * CellSize.x),
                Planar ? 0f : (float)((coord.y + 0.5) * CellSize.y),
                (float)((coord.z + 0.5) * CellSize.z));
            return new Bounds(center, CellSize);
        }

        public string ToJson()
        {
            var sb = new StringBuilder(160);
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Key("cell"); ControlPlaneJson.Vec(w, CellSize);
            w.Prop("planar", Planar);
            w.Prop("ring", Ring);
            w.Prop("retire", RetireSeconds);
            w.Key("anchor");
            w.BeginArray(); w.Value(Anchor.x); w.Value(Anchor.y); w.Value(Anchor.z); w.EndArray();
            w.EndObject();
            return sb.ToString();
        }

        /// <summary>Read a definition written by <see cref="ToJson"/>. Null when the payload is empty or not a grid payload.</summary>
        public static ChunkGridDefinition FromJson(string json)
        {
            if (string.IsNullOrEmpty(json) || !PersistenceJson.TryParseObject(json, out var o, out _)) return null;
            var d = new ChunkGridDefinition
            {
                CellSize = ControlPlaneJson.Vec(o, "cell"),
                Planar = ControlPlaneJson.Bool(o, "planar"),
                Ring = (int)ControlPlaneJson.Num(o, "ring"),
                RetireSeconds = (float)ControlPlaneJson.Num(o, "retire"),
            };
            if (o.TryGetValue("anchor", out var a) && a is List<object> list && list.Count >= 3)
                d.Anchor = new Vector3Int((int)Num(list[0]), (int)Num(list[1]), (int)Num(list[2]));
            return d.CellSize.x > 0 && d.CellSize.y > 0 && d.CellSize.z > 0 ? d : null;
        }

        private static double Num(object o) => o is double d ? d : o is long l ? l : 0;

        /// <summary>The scope definition that activates this grid under <paramref name="scopeKey"/>.</summary>
        public ScopeDefinition ToScopeDefinition()
        {
            var anchor = NormalizedAnchor();
            var box = AbsoluteBoundsOf(anchor);
            return new ScopeDefinition
            {
                Kind = ScopeKind.Grid,
                Payload = ToJson(),
                Parts = { new ScopePart { PartId = ChunkKeys.PartId(anchor), Center = box.center, Size = box.size } },
            };
        }

        /// <summary>
        /// The grid a single chunk is a cell of, from its coordinate and its box in absolute world coordinates.
        /// A role without a control plane — a client — never sees the scope row, only the container rows the
        /// gateway sent it, and those are enough: the box's size <i>is</i> the cell size, and a column grid is the
        /// one whose boxes are centred on y = 0 whatever the coordinate (<see cref="AbsoluteBoundsOf"/>), which no
        /// volumetric cell ever is. Null when the box is degenerate.
        /// </summary>
        public static ChunkGridDefinition Infer(Vector3Int coord, Bounds absoluteBox)
        {
            if (absoluteBox.size.x <= 0f || absoluteBox.size.y <= 0f || absoluteBox.size.z <= 0f) return null;
            return new ChunkGridDefinition
            {
                CellSize = absoluteBox.size,
                Planar = absoluteBox.center.y == 0f,
                Anchor = coord,
            };
        }

        /// <summary>The grid a scope row describes, or null when the row is not a grid scope.</summary>
        public static ChunkGridDefinition Of(ScopeInfo scope) =>
            scope?.Definition != null && scope.Definition.Kind == ScopeKind.Grid ? FromJson(scope.Definition.Payload) : null;
    }

    /// <summary>
    /// How a chunk coordinate becomes the ids a scope's containers are named by. Two derivations, one per scope
    /// kind, and the choice is the whole of <c>docs/scoped-chunk-grids.md</c> D2:
    /// <list type="bullet">
    /// <item>the <b>public</b> scope keeps the pinned three-axis 21-bit packing (<c>RuntimeGrid.PackId</c> /
    /// <see cref="InterestGrid.PackRegion"/>), so every id persisted before scoped grids existed still resolves to
    /// the cell it always did;</item>
    /// <item>a <b>scoped</b> grid derives its ids exactly as every other scope does
    /// (<see cref="ScopeKeys.ContainerId"/> of the key and the chunk's part id), so chunk (x,y,z) of two scopes is
    /// two container ids, two lease rows and two persistence records without a bit of the runtime id being spent
    /// on a scope field.</item>
    /// </list>
    /// Pure C#, so the gateway, the orchestrator and a test name a scope's chunks without Unity.
    /// </summary>
    public static class ChunkKeys
    {
        /// <summary>Coordinates outside this range cannot be packed into a public runtime id.</summary>
        public const int MinCoordinate = -(1 << 20);
        public const int MaxCoordinate = (1 << 20) - 1;

        public static bool IsValidCoordinate(Vector3Int c) =>
            c.x >= MinCoordinate && c.x <= MaxCoordinate && c.y >= MinCoordinate && c.y <= MaxCoordinate &&
            c.z >= MinCoordinate && c.z <= MaxCoordinate;

        /// <summary>
        /// The scope part id of a chunk: <c>c/x/y/z</c>. It is hashed with the scope key into the container id and
        /// carried on the lease row (<see cref="InstanceContainerInfo.PartId"/>), which is how a role that never
        /// computed the id — a client, a gateway — recovers the coordinate from a row it was handed.
        /// </summary>
        public static string PartId(Vector3Int coord) =>
            "c/" + coord.x.ToString(CultureInfo.InvariantCulture) + "/" + coord.y.ToString(CultureInfo.InvariantCulture)
            + "/" + coord.z.ToString(CultureInfo.InvariantCulture);

        /// <summary>Inverse of <see cref="PartId"/>. False for any other part id (an instance's "interior").</summary>
        public static bool TryParsePartId(string partId, out Vector3Int coord)
        {
            coord = default;
            // "c/0/0/0" is the shortest chunk part id there is.
            if (string.IsNullOrEmpty(partId) || partId.Length < 7 || partId[0] != 'c' || partId[1] != '/') return false;
            var parts = partId.Split('/');
            if (parts.Length != 4) return false;
            if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z)) return false;
            coord = new Vector3Int(x, y, z);
            return true;
        }

        /// <summary>
        /// The 64-bit runtime container id of chunk <paramref name="coord"/> in <paramref name="scopeKey"/>. An
        /// empty key is the public world and gets the pinned packing; any other key gets the scope's hash.
        /// </summary>
        public static ulong RuntimeId(string scopeKey, Vector3Int coord)
        {
            if (string.IsNullOrEmpty(scopeKey))
            {
                if (!IsValidCoordinate(coord)) throw new ArgumentOutOfRangeException(nameof(coord), "Grid coordinate exceeds the packed runtime id range.");
                return InterestGrid.PackRegion(coord.x, coord.y, coord.z);
            }
            return ScopeKeys.Hash(scopeKey + "/" + PartId(coord));
        }

        /// <summary>The control-plane container id of a chunk: <see cref="RuntimeId"/> in the <c>rt_</c> form.</summary>
        public static string ContainerId(string scopeKey, Vector3Int coord) =>
            ScopeKeys.RuntimeIdPrefix + RuntimeId(scopeKey, coord).ToString(CultureInfo.InvariantCulture);
    }
}
