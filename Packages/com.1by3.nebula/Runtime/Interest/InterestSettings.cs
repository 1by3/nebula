using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>How much a <see cref="ConfigIssue"/> matters. Only <see cref="Error"/> is worth refusing to start for.</summary>
    public enum ConfigSeverity : byte
    {
        Info = 0,
        /// <summary>The value was clamped or raised to something that works; the mesh runs, but not as configured.</summary>
        Warning = 1,
        /// <summary>The value cannot produce correct behaviour (a non-positive size). Start-up should stop.</summary>
        Error = 2,
    }

    /// <summary>One problem found in a configuration, named by field so an inspector or <c>nebula doctor</c> can point at it.</summary>
    public readonly struct ConfigIssue
    {
        public readonly ConfigSeverity Severity;
        public readonly string Field;
        public readonly string Message;

        public ConfigIssue(ConfigSeverity severity, string field, string message)
        {
            Severity = severity; Field = field; Message = message;
        }

        public override string ToString() => $"{Severity}: {Field}: {Message}";
    }

    /// <summary>
    /// The interest knobs, resolved from <see cref="NebulaConfig"/> and already clamped into a set of values that
    /// work together (see <see cref="Validate"/>). Everything downstream — grid, per-client evaluation,
    /// subscription margins, worker publishing — reads this and nothing else, so the standalone gateway and the
    /// Unity worker cannot drift apart over what "near" means.
    /// </summary>
    public struct InterestSettings
    {
        /// <summary>Metres a client hears about an entity at, unless the entity's prefab overrides it.</summary>
        public float Radius;
        /// <summary>Extra metres an entity must travel past <see cref="Radius"/> before it leaves the set (hysteresis).</summary>
        public float ExitMargin;
        /// <summary>Seconds an entity must stay outside <c>Radius + ExitMargin</c> before it is despawned.</summary>
        public float LingerSeconds;
        /// <summary>Region edge in metres, before the alignment snap of design §3.</summary>
        public float CellSize;
        /// <summary>Regions are columns: Y does not enter a region key. True for surface worlds.</summary>
        public bool Planar;
        /// <summary>How often a client's interest set is re-evaluated, in hertz.</summary>
        public float EvalHz;
        /// <summary>Extra metres of regions subscribed beyond the exit radius, so a moving client is never late.</summary>
        public float SubscribeMargin;
        /// <summary>Seconds a region stays subscribed after it stops being needed (anti-thrash along an edge).</summary>
        public float RegionLingerSeconds;
        /// <summary>Seconds a worker link is kept after the last reason to hold it has gone.</summary>
        public float LinkLingerSeconds;
        /// <summary>How often a gateway re-sends a Full subscription snapshot as an audit.</summary>
        public float ResyncSeconds;
        /// <summary>Ceiling on a prefab's <c>RelevanceRadius</c>; also how far a wide entity may reach.</summary>
        public float MaxRadius;
        /// <summary>Most foci one client may have (RTS camera plus owned units, spectators).</summary>
        public int MaxFoci;
        /// <summary>Metres a client's focus hint may sit from its pawn before it is clamped.</summary>
        public float HintMaxDistance;
        /// <summary>Most focus hints accepted from one client per second; the rest are dropped.</summary>
        public float HintMaxHz;
        /// <summary>Most explicit per-entity subscriptions one client's policy may ask for.</summary>
        public int MaxExplicitPerClient;
        /// <summary>The fastest a focus is expected to move, in metres per second. Only used to validate <see cref="SubscribeMargin"/>.</summary>
        public float MaxFocusSpeed;
        /// <summary>Rate tier inside the set: entities within this radius get every tick.</summary>
        public float NearRadius;
        /// <summary>Rate tier inside the set: entities beyond this radius get every <see cref="FarDivisor"/>-th tick.</summary>
        public float FarRadius;
        public int MidDivisor;
        public int FarDivisor;
        /// <summary>Cells of content a client keeps loaded; raised to cover the interest radius (design §8).</summary>
        public int ClientLoadRadiusCells;
        /// <summary>Entities in one container above which the worker warns that the world wants partitioning.</summary>
        public int PartitionWarnEntities;
        /// <summary>Milliseconds of per-gateway filtering per tick above which the worker warns the same way.</summary>
        public float PartitionWarnFilterMs;

        /// <summary>The shipped defaults (design §9). A game that sets nothing gets these.</summary>
        public static InterestSettings Default => new InterestSettings
        {
            Radius = 120f,
            ExitMargin = 16f,
            LingerSeconds = 1f,
            CellSize = 64f,
            Planar = true,
            EvalHz = 4f,
            SubscribeMargin = 32f,
            RegionLingerSeconds = 3f,
            LinkLingerSeconds = 10f,
            ResyncSeconds = 30f,
            MaxRadius = 1024f,
            MaxFoci = 8,
            HintMaxDistance = 60f,
            HintMaxHz = 5f,
            MaxExplicitPerClient = 16,
            MaxFocusSpeed = 12f,
            NearRadius = 30f,
            FarRadius = 80f,
            MidDivisor = 4,
            FarDivisor = 12,
            ClientLoadRadiusCells = 1,
            PartitionWarnEntities = 2000,
            PartitionWarnFilterMs = 2f,
        };

        /// <summary>Metres an entity must be outside before the linger clock even starts.</summary>
        public float ExitRadius => Radius + ExitMargin;

        /// <summary>Metres of regions a gateway subscribes around a focus: the exit radius plus the pre-subscription margin.</summary>
        public float SubscribeRadius => Radius + ExitMargin + SubscribeMargin;

        /// <summary>Seconds between two evaluations of one client.</summary>
        public float EvalInterval => EvalHz > 0 ? 1f / EvalHz : 0.25f;

        /// <summary>
        /// One notion of "near" (design §8): the cells of content that must exist before interest needs them.
        /// Content streaming, the runtime allocator ring and the container window a client is told about all
        /// derive from this, so a client can never be spawned an entity standing on geometry it does not have.
        /// </summary>
        public int NearCells(float cellSize)
        {
            if (cellSize <= 0) return Math.Max(0, ClientLoadRadiusCells);
            // The exit radius is where an entity *starts* to leave, not where it is gone: it must be outside for
            // LingerSeconds, and evaluation only runs EvalHz times a second, so a moving client can legitimately
            // still hold an entity that is a further MaxFocusSpeed x (linger + one interval) away. Sizing the
            // content window to ExitRadius alone leaves no room for that, and the client then holds a replica
            // standing on a cell it has not loaded -- rare, transient, and exactly what InterestProbe's
            // `orphans` counter reports. Costs nothing where a cell is much larger than the overshoot.
            float overshoot = Math.Max(0f, MaxFocusSpeed) * (Math.Max(0f, LingerSeconds) + EvalInterval);
            int needed = (int)Math.Ceiling((ExitRadius + overshoot) / cellSize);
            return Math.Max(Math.Max(0, ClientLoadRadiusCells), needed);
        }

        /// <summary>
        /// Check the relationships design §9 requires and return the values that will actually be used. Anything
        /// that can be repaired is repaired and reported as a <see cref="ConfigSeverity.Warning"/>; only a
        /// non-positive size is an <see cref="ConfigSeverity.Error"/>, because no clamp of it would be what the
        /// game asked for. <paramref name="worldCellSize"/> is the world definition's cell size (0 = none) and
        /// <paramref name="ghostBandMargin"/> the meshing band, both of which constrain the region size.
        /// </summary>
        public InterestSettings Validate(List<ConfigIssue> issues, float worldCellSize = 0f, float ghostBandMargin = 0f)
        {
            var e = this;
            void Warn(string field, string message) => issues?.Add(new ConfigIssue(ConfigSeverity.Warning, field, message));
            void Error(string field, string message) => issues?.Add(new ConfigIssue(ConfigSeverity.Error, field, message));

            if (e.Radius <= 0)
            {
                Error(InterestRadiusField, $"InterestRadius must be greater than 0 (is {e.Radius}); using {Default.Radius}.");
                e.Radius = Default.Radius;
            }
            // MaxRadius is a security and cost boundary, not a hint: it caps how far a prefab may claim to be
            // relevant and therefore how much a client can be made to receive. "0 = uncapped" would make the
            // boundary opt-in, and a mesh that forgot to set it would have none, so a non-positive value means
            // the mesh radius and is said out loud. Everything downstream may clamp unconditionally.
            if (e.MaxRadius <= 0)
            {
                Warn(InterestMaxRadiusField, $"InterestMaxRadius must be greater than 0 (is {e.MaxRadius}); it is the ceiling on what a prefab's RelevanceRadius may ask for, so it is set to InterestRadius ({e.Radius}) rather than left uncapped.");
                e.MaxRadius = e.Radius;
            }
            else if (e.MaxRadius < e.Radius)
            {
                Warn(InterestMaxRadiusField, $"InterestMaxRadius ({e.MaxRadius}) is below InterestRadius ({e.Radius}); raised to InterestRadius.");
                e.MaxRadius = e.Radius;
            }
            if (e.ExitMargin <= 0)
            {
                Warn(InterestExitMarginField, $"InterestExitMargin must be greater than 0 (is {e.ExitMargin}) or entities flicker on the boundary; using {Default.ExitMargin}.");
                e.ExitMargin = Default.ExitMargin;
            }
            if (e.LingerSeconds < 0)
            {
                Warn(InterestLingerSecondsField, "InterestLingerSeconds cannot be negative; using 0.");
                e.LingerSeconds = 0;
            }
            if (e.EvalHz <= 0)
            {
                Warn(InterestEvalHzField, $"InterestEvalHz must be greater than 0 (is {e.EvalHz}); using {Default.EvalHz}.");
                e.EvalHz = Default.EvalHz;
            }
            if (e.CellSize <= 0)
            {
                Error(InterestCellSizeField, $"InterestCellSize must be greater than 0 (is {e.CellSize}); using {Default.CellSize}.");
                e.CellSize = Default.CellSize;
            }
            if (worldCellSize > 0)
            {
                double snapped = InterestGrid.SnapEdge(e.CellSize, worldCellSize);
                if (Math.Abs(snapped - e.CellSize) > 1e-4)
                {
                    Warn(InterestCellSizeField, $"InterestCellSize ({e.CellSize}) is not an integer divisor of the world cell size ({worldCellSize}); regions use {snapped} m so their edges fall on cell edges.");
                    e.CellSize = (float)snapped;
                }
            }
            if (ghostBandMargin >= e.CellSize)
            {
                Warn(nameof(NebulaConfig.GhostBandMargin), $"GhostBandMargin ({ghostBandMargin}) is not smaller than the interest cell size ({e.CellSize}); a region would be narrower than the band a gateway must subscribe around it.");
            }

            // Rate tiers are an LOD inside the set, so they only make sense inside it.
            if (e.NearRadius < 0) { Warn(nameof(NebulaConfig.InterestNearRadius), "InterestNearRadius cannot be negative; using 0."); e.NearRadius = 0; }
            if (e.FarRadius < e.NearRadius)
            {
                Warn(nameof(NebulaConfig.InterestFarRadius), $"InterestFarRadius ({e.FarRadius}) is below InterestNearRadius ({e.NearRadius}); raised to it.");
                e.FarRadius = e.NearRadius;
            }
            if (e.FarRadius > e.Radius)
            {
                Warn(nameof(NebulaConfig.InterestFarRadius), $"InterestFarRadius ({e.FarRadius}) is beyond InterestRadius ({e.Radius}), where no entity is ever sent; lowered to InterestRadius.");
                e.FarRadius = e.Radius;
                if (e.NearRadius > e.FarRadius) e.NearRadius = e.FarRadius;
            }
            if (e.MidDivisor < 1) { Warn(nameof(NebulaConfig.InterestMidDivisor), "InterestMidDivisor must be at least 1; using 1."); e.MidDivisor = 1; }
            if (e.FarDivisor < 1) { Warn(nameof(NebulaConfig.InterestFarDivisor), "InterestFarDivisor must be at least 1; using 1."); e.FarDivisor = 1; }

            // A client that can cross the margin between two evaluations would be sent an entity late.
            float travel = e.MaxFocusSpeed / e.EvalHz;
            if (e.MaxFocusSpeed > 0 && e.SubscribeMargin < travel)
            {
                Warn(InterestSubscribeMarginField, $"InterestSubscribeMargin ({e.SubscribeMargin}) is less than one evaluation of travel at InterestMaxFocusSpeed ({travel:0.##} m); raised to it.");
                e.SubscribeMargin = travel;
            }
            if (e.RegionLingerSeconds < 0) { Warn(InterestRegionLingerSecondsField, "InterestRegionLingerSeconds cannot be negative; using 0."); e.RegionLingerSeconds = 0; }
            if (e.LinkLingerSeconds < 0) { Warn(InterestLinkLingerSecondsField, "InterestLinkLingerSeconds cannot be negative; using 0."); e.LinkLingerSeconds = 0; }
            if (e.ResyncSeconds <= 0) { Warn(InterestResyncSecondsField, $"InterestResyncSeconds must be greater than 0; using {Default.ResyncSeconds}."); e.ResyncSeconds = Default.ResyncSeconds; }
            if (e.MaxFoci < 1) { Warn(InterestMaxFociField, "InterestMaxFoci must be at least 1; using 1."); e.MaxFoci = 1; }
            if (e.MaxExplicitPerClient < 0) { Warn(InterestMaxExplicitPerClientField, "InterestMaxExplicitPerClient cannot be negative; using 0."); e.MaxExplicitPerClient = 0; }
            if (e.HintMaxDistance < 0) { Warn(InterestHintMaxDistanceField, "InterestHintMaxDistance cannot be negative; using 0 (hints snap to the pawn)."); e.HintMaxDistance = 0; }
            if (e.HintMaxHz < 0) { Warn(InterestHintMaxHzField, "InterestHintMaxHz cannot be negative; using 0 (hints are ignored)."); e.HintMaxHz = 0; }

            // Content must exist before interest can spawn an entity standing on it.
            if (worldCellSize > 0)
            {
                int needed = (int)Math.Ceiling(e.ExitRadius / worldCellSize);
                if (e.ClientLoadRadiusCells < needed)
                {
                    Warn(nameof(NebulaConfig.ClientLoadRadiusCells), $"ClientLoadRadiusCells ({e.ClientLoadRadiusCells}) x cell size ({worldCellSize}) does not cover InterestRadius + InterestExitMargin ({e.ExitRadius}); raised to {needed}.");
                    e.ClientLoadRadiusCells = needed;
                }
            }
            if (e.PartitionWarnEntities < 0) e.PartitionWarnEntities = 0;
            if (e.PartitionWarnFilterMs < 0) e.PartitionWarnFilterMs = 0;
            return e;
        }

        // Field names as the config spells them, so an issue points at the inspector row and not at this struct.
        private const string InterestRadiusField = "InterestRadius";
        private const string InterestMaxRadiusField = "InterestMaxRadius";
        private const string InterestExitMarginField = "InterestExitMargin";
        private const string InterestLingerSecondsField = "InterestLingerSeconds";
        private const string InterestEvalHzField = "InterestEvalHz";
        private const string InterestCellSizeField = "InterestCellSize";
        private const string InterestSubscribeMarginField = "InterestSubscribeMargin";
        private const string InterestRegionLingerSecondsField = "InterestRegionLingerSeconds";
        private const string InterestLinkLingerSecondsField = "InterestLinkLingerSeconds";
        private const string InterestResyncSecondsField = "InterestResyncSeconds";
        private const string InterestMaxFociField = "InterestMaxFoci";
        private const string InterestMaxExplicitPerClientField = "InterestMaxExplicitPerClient";
        private const string InterestHintMaxDistanceField = "InterestHintMaxDistance";
        private const string InterestHintMaxHzField = "InterestHintMaxHz";
    }
}
