using UnityEngine;
using UnityEngine.Serialization;

namespace Nebula
{
    /// <summary>
    /// The timing settings in ticks at the running rate (<see cref="NetworkTime.TickRate"/>), and the upgrade of
    /// configs saved before they were held in seconds (docs/tick-rate.md).
    /// </summary>
    /// <remarks>
    /// The fields are seconds, so a setting means the same at 30 Hz as at 60. These properties are what the runtime
    /// reads; each converts at the rate in effect when it is read, so a client that learns its mesh's rate from the
    /// gateway's welcome gets the right counts from then on. Setting one converts back at the current rate.
    /// </remarks>
    public sealed partial class NebulaConfig : ISerializationCallbackReceiver
    {
        /// <summary>Client: <see cref="InterpolationDelaySeconds"/> in ticks, rounded up.</summary>
        public int InterpolationDelayTicks
        {
            get => NetworkTime.SecondsToTicksCeil(InterpolationDelaySeconds);
            set => InterpolationDelaySeconds = TicksToSeconds(value);
        }

        /// <summary>Client: <see cref="MaxStreamLagSeconds"/> in ticks, rounded.</summary>
        public int MaxStreamLagTicks
        {
            get => NetworkTime.SecondsToTicks(MaxStreamLagSeconds);
            set => MaxStreamLagSeconds = TicksToSeconds(value);
        }

        /// <summary>Client: <see cref="SlowUpdateMaxDelaySeconds"/> in ticks, rounded.</summary>
        public int SlowUpdateMaxDelayTicks
        {
            get => NetworkTime.SecondsToTicks(SlowUpdateMaxDelaySeconds);
            set => SlowUpdateMaxDelaySeconds = TicksToSeconds(value);
        }

        /// <summary>Client: <see cref="InputLeadMarginSeconds"/> in ticks, rounded up.</summary>
        public int InputLeadMarginTicks
        {
            get => NetworkTime.SecondsToTicksCeil(InputLeadMarginSeconds);
            set => InputLeadMarginSeconds = TicksToSeconds(value);
        }

        /// <summary>Client: <see cref="InputLeadTargetSeconds"/> in ticks, rounded up.</summary>
        public int InputLeadTargetTicks
        {
            get => NetworkTime.SecondsToTicksCeil(InputLeadTargetSeconds);
            set => InputLeadTargetSeconds = TicksToSeconds(value);
        }

        /// <summary>Client: <see cref="InputLeadMaxAdjustSeconds"/> in ticks, rounded.</summary>
        public int InputLeadMaxAdjustTicks
        {
            get => NetworkTime.SecondsToTicks(InputLeadMaxAdjustSeconds);
            set => InputLeadMaxAdjustSeconds = TicksToSeconds(value);
        }

        /// <summary>Worker: <see cref="StateHistorySeconds"/> in ticks, rounded up, at most <see cref="StateHistory.MaxWindowTicks"/>.</summary>
        public int StateHistoryTicks
        {
            get => Mathf.Min(NetworkTime.SecondsToTicksCeil(StateHistorySeconds), StateHistory.MaxWindowTicks);
            set => StateHistorySeconds = TicksToSeconds(value);
        }

        private static float TicksToSeconds(int ticks) => ticks <= 0 ? 0f : ticks / (float)NetworkTime.TickRate;

        // Configs saved before the timing settings were seconds hold them as ticks under the old names. Every one of
        // those was tuned at 60 Hz, the only rate there was, so it is read as ticks at 60 Hz once and then dropped.
        [SerializeField, HideInInspector, FormerlySerializedAs("InterpolationDelayTicks")] private int _legacyInterpolationDelayTicks = -1;
        [SerializeField, HideInInspector, FormerlySerializedAs("MaxStreamLagTicks")] private int _legacyMaxStreamLagTicks = -1;
        [SerializeField, HideInInspector, FormerlySerializedAs("SlowUpdateMaxDelayTicks")] private int _legacySlowUpdateMaxDelayTicks = -1;
        [SerializeField, HideInInspector, FormerlySerializedAs("InputLeadMarginTicks")] private int _legacyInputLeadMarginTicks = -1;
        [SerializeField, HideInInspector, FormerlySerializedAs("InputLeadTargetTicks")] private int _legacyInputLeadTargetTicks = -1;
        [SerializeField, HideInInspector, FormerlySerializedAs("InputLeadMaxAdjustTicks")] private int _legacyInputLeadMaxAdjustTicks = -1;
        [SerializeField, HideInInspector, FormerlySerializedAs("StateHistoryTicks")] private int _legacyStateHistoryTicks = -1;

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize() => UpgradeLegacyTiming();

        /// <summary>Turn tick counts saved by an older Nebula (all at 60 Hz) into the seconds fields. Idempotent.</summary>
        internal void UpgradeLegacyTiming()
        {
            const float At60 = 1f / NetworkTime.DefaultTickRate;
            if (_legacyInterpolationDelayTicks >= 0) { InterpolationDelaySeconds = _legacyInterpolationDelayTicks * At60; _legacyInterpolationDelayTicks = -1; }
            if (_legacyMaxStreamLagTicks >= 0) { MaxStreamLagSeconds = _legacyMaxStreamLagTicks * At60; _legacyMaxStreamLagTicks = -1; }
            if (_legacySlowUpdateMaxDelayTicks >= 0) { SlowUpdateMaxDelaySeconds = _legacySlowUpdateMaxDelayTicks * At60; _legacySlowUpdateMaxDelayTicks = -1; }
            if (_legacyInputLeadMarginTicks >= 0) { InputLeadMarginSeconds = _legacyInputLeadMarginTicks * At60; _legacyInputLeadMarginTicks = -1; }
            if (_legacyInputLeadTargetTicks >= 0) { InputLeadTargetSeconds = _legacyInputLeadTargetTicks * At60; _legacyInputLeadTargetTicks = -1; }
            if (_legacyInputLeadMaxAdjustTicks >= 0) { InputLeadMaxAdjustSeconds = _legacyInputLeadMaxAdjustTicks * At60; _legacyInputLeadMaxAdjustTicks = -1; }
            if (_legacyStateHistoryTicks >= 0) { StateHistorySeconds = _legacyStateHistoryTicks * At60; _legacyStateHistoryTicks = -1; }
        }

        /// <summary>For a test: set the old tick fields as an asset saved by an older Nebula would have them.</summary>
        internal void SetLegacyTimingForTest(int interpolationDelay, int inputLeadMargin, int inputLeadTarget, int stateHistory)
        {
            _legacyInterpolationDelayTicks = interpolationDelay;
            _legacyInputLeadMarginTicks = inputLeadMargin;
            _legacyInputLeadTargetTicks = inputLeadTarget;
            _legacyStateHistoryTicks = stateHistory;
        }
    }
}
