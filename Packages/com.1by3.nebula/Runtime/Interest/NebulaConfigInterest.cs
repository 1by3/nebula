using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The part of <see cref="NebulaConfig"/> that turns typed-in numbers into the interest settings every role
    /// runs on. It lives here, and not next to the fields, because the Unity config and the mirror the standalone
    /// services deserialize are two different declarations of the same settings: sharing the logic is what keeps
    /// a gateway and a worker from disagreeing about what "near" means.
    /// </summary>
    public sealed partial class NebulaConfig
    {
        /// <summary>
        /// The resolved, clamped interest settings. Repairs are silent here — <see cref="Validate(List{ConfigIssue})"/>
        /// is where they are reported — so that a role can start with values that work even when nobody is
        /// reading the log.
        /// </summary>
        public InterestSettings ToInterestSettings() => ToInterestSettings(WorldCellSize());

        /// <summary>As <see cref="ToInterestSettings()"/>, for a caller that knows the world's cell size itself (the standalone services read it from the exported manifest).</summary>
        public InterestSettings ToInterestSettings(float worldCellSize) => RawInterestSettings().Validate(null, worldCellSize, GhostBandMargin);

        /// <summary>The settings exactly as configured, before any repair. Validation works on this.</summary>
        public InterestSettings RawInterestSettings() => new InterestSettings
        {
            Radius = InterestRadius,
            ExitMargin = InterestExitMargin,
            LingerSeconds = InterestLingerSeconds,
            CellSize = InterestCellSize,
            Planar = InterestPlanar,
            EvalHz = InterestEvalHz,
            SubscribeMargin = InterestSubscribeMargin,
            RegionLingerSeconds = InterestRegionLingerSeconds,
            LinkLingerSeconds = InterestLinkLingerSeconds,
            ResyncSeconds = InterestResyncSeconds,
            MaxRadius = InterestMaxRadius,
            MaxFoci = InterestMaxFoci,
            HintMaxDistance = InterestHintMaxDistance,
            HintMaxHz = InterestHintMaxHz,
            MaxExplicitPerClient = InterestMaxExplicitPerClient,
            MaxFocusSpeed = InterestMaxFocusSpeed,
            NearRadius = InterestNearRadius,
            FarRadius = InterestFarRadius,
            MidDivisor = InterestMidDivisor,
            FarDivisor = InterestFarDivisor,
            ClientLoadRadiusCells = ClientLoadRadiusCells,
            PartitionWarnEntities = PartitionWarnEntities,
            PartitionWarnFilterMs = PartitionWarnFilterMs,
        };

        /// <summary>
        /// Everything wrong with this configuration, worst last-resort first: an <see cref="ConfigSeverity.Error"/>
        /// is a value no clamp can rescue and start-up should stop on, a <see cref="ConfigSeverity.Warning"/> is a
        /// value that was repaired and will not behave as typed.
        /// </summary>
        public void Validate(List<ConfigIssue> issues) => Validate(issues, WorldCellSize());

        public void Validate(List<ConfigIssue> issues, float worldCellSize)
        {
            RawInterestSettings().Validate(issues, worldCellSize, GhostBandMargin);
            if (ChunkRetireSeconds < 0)
                issues?.Add(new ConfigIssue(ConfigSeverity.Warning, nameof(ChunkRetireSeconds), "ChunkRetireSeconds cannot be negative; chunks are retired as soon as nobody needs them."));
        }

        /// <summary>
        /// The world definition's cell size, or 0 when the game has no world definition. Everything that has to
        /// agree on one notion of "near" (design §8) — content streaming, the allocator ring, the container
        /// window a client is told about — derives from this and <see cref="InterestSettings.NearCells"/>.
        /// </summary>
        public float ResolveWorldCellSize() => WorldCellSize();

        /// <summary>
        /// The interest grid this mesh uses: the resolved settings snapped onto the world's cells so a region
        /// never straddles a cell edge (design §3). Both ends derive it from this, which is what lets the worker
        /// reject a subscription built with a different one instead of filtering with ids that mean something else.
        /// </summary>
        public InterestGrid ToInterestGrid()
        {
            float cell = WorldCellSize();
            var settings = ToInterestSettings(cell);
            return InterestGrid.Resolve(settings, cell, WorldCellsCentred());
        }
    }
}
