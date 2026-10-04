using System.Collections.Generic;
using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// Client: watches what a player sees of one entity (a pawn, a ship) for jumps. Each rendered frame it compares the
    /// entity's drawn position with where its motion over the last frames says it should be, and records any frame
    /// whose step is off by more than <see cref="JumpMetres"/>, any change of container (a frame crossing, a seam) with
    /// the step on that frame, and any frame longer than <see cref="HitchMs"/>. It is a <b>sample</b> diagnostic: drop it
    /// on a client, point it at an entity and read <see cref="Recent"/> or the log.
    /// <para>
    /// Runs in <c>LateUpdate</c>, after <see cref="NebulaClient"/> has posed every physics frame for rendering
    /// (<c>docs/container-tree.md</c> D11), so it measures render space, which is what a camera sees.
    /// </para>
    /// </summary>
    public sealed class FrameCrossingProbe : MonoBehaviour
    {
        [Tooltip("The entity to watch. Its world position is what is drawn.")]
        public NetworkIdentity Target;
        [Tooltip("A step this far from the predicted one, metres, is a jump.")]
        public float JumpMetres = 0.05f;
        [Tooltip("A frame longer than this, milliseconds, is a hitch.")]
        public float HitchMs = 33f;
        [Tooltip("Write each finding to the log as well.")]
        public bool Log = true;

        private const int Capacity = 256;
        private readonly List<string> _recent = new List<string>();
        private Vector3 _last, _lastStep;
        private bool _hasLast, _hasStep;
        private Container _lastContainer;
        private NetworkIdentity _watching;

        /// <summary>Findings, newest last (at most 256).</summary>
        public IReadOnlyList<string> Recent => _recent;
        /// <summary>The largest step error seen so far, metres.</summary>
        public float WorstJump { get; private set; }
        /// <summary>The largest step error seen on a frame where the container changed, metres.</summary>
        public float WorstJumpAtCrossing { get; private set; }

        private void LateUpdate()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            if (ms > HitchMs) Note($"hitch: frame of {ms:0.0} ms");
            if (Target != _watching) { _watching = Target; _hasLast = _hasStep = false; _lastContainer = Target != null ? Target.Container : null; }
            if (Target == null) return;

            var position = Target.transform.position;
            var container = Target.Container;
            bool crossed = container != _lastContainer;
            if (_hasLast)
            {
                var step = position - _last;
                // Predicted as the last frame's step: at a steady frame rate a steady motion predicts itself.
                float error = _hasStep ? Vector3.Distance(step, _lastStep) : 0f;
                WorstJump = Mathf.Max(WorstJump, error);
                if (crossed) WorstJumpAtCrossing = Mathf.Max(WorstJumpAtCrossing, error);
                if (crossed) Note($"crossing: {Name(_lastContainer)} -> {Name(container)}, step off by {error * 1000f:0.0} mm");
                else if (error > JumpMetres) Note($"jump: step off by {error * 1000f:0.0} mm in {Name(container)}");
                _lastStep = step;
                _hasStep = true;
            }
            _last = position;
            _hasLast = true;
            _lastContainer = container;
        }

        private static string Name(Container c) => c != null ? c.ContainerId : "none";

        private void Note(string what)
        {
            string line = $"[probe {Time.frameCount}] {what}";
            if (_recent.Count == Capacity) _recent.RemoveAt(0);
            _recent.Add(line);
            if (Log) Debug.Log(line);
        }
    }
}
