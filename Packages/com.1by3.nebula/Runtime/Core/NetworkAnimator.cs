using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Replicates an <see cref="Animator"/> the way NGO's NetworkAnimator does. The Animator keeps running on every
    /// copy; what travels is what would otherwise diverge: parameter changes (int, float, bool; floats past a
    /// threshold), triggers (only through <see cref="SetTrigger(string)"/>, exactly like NGO), and per-layer state
    /// (state hash, normalized time, weight) plus in-progress transitions so a state entered on the authority is
    /// entered everywhere. Keyframes carry all of it, so late joiners and fresh ghosts start in the right pose.
    /// Parameters driven by animation curves are skipped (NGO does the same). Always reliable: a lost trigger would
    /// never be recovered.
    /// <para>
    /// Server authority (default): the worker drives the Animator; an owning client calling
    /// <see cref="SetTrigger(string)"/> asks the worker over a ServerRpc and sees the result one round trip later,
    /// as in NGO. Owner authority: the owning client drives it and the worker relays (NGO's OwnerNetworkAnimator).
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkAnimator : NetworkSyncBehaviour
    {
        [Tooltip("The Animator to replicate. Defaults to the one on this GameObject.")]
        [SerializeField] private Animator _animator;

        [Header("What to synchronize")]
        public bool SyncParameters = true;
        public bool SyncLayerStates = true;
        public bool SyncLayerWeights = true;
        public bool SyncTransitions = true;
        [Tooltip("A float parameter must move by at least this much to be sent.")]
        public float FloatThreshold = 0.001f;
        [Tooltip("Keyframes re-sync a looping state's playhead only if it drifted by more than this (normalized time).")]
        public float NormalizedTimeResyncThreshold = 0.25f;
        [Tooltip("Non-authoritative copies do not apply root motion: the entity's pose comes from the network, not the clip.")]
        public bool DisableRootMotionOnRemote = true;

        [Flags]
        private enum Fields : byte
        {
            None = 0,
            Parameters = 1,
            Triggers = 2,
            Layers = 4,
        }

        private struct Param
        {
            public int Hash;
            public AnimatorControllerParameterType Type;
            public bool Skip;         // controlled by a curve, or a trigger (triggers travel separately)
            public int LastInt;
            public float LastFloat;
            public bool LastBool;
        }

        private struct LayerTrack
        {
            public int LastStateHash;
            public float LastWeight;
            public int LastNextHash;
        }

        private Param[] _params = Array.Empty<Param>();
        private LayerTrack[] _layers = Array.Empty<LayerTrack>();
        private readonly List<int> _dirtyParams = new List<int>();
        private readonly List<int> _pendingTriggers = new List<int>();
        private readonly List<int> _dirtyLayers = new List<int>();
        private bool _initialised;
        private bool _rootMotionWas;

        public Animator Animator => _animator != null ? _animator : (_animator = GetComponent<Animator>());

        // ---- lifecycle -------------------------------------------------------------------------------------

        public override void OnNetworkSpawn()
        {
            EnsureInitialised();
            ApplyRootMotionPolicy();
        }

        public override void OnGainedAuthority()
        {
            base.OnGainedAuthority();
            ApplyRootMotionPolicy();
            // Fresh authority: the next AuthorityTick sends everything (SyncEverSent was reset by the identity).
            if (_initialised) CaptureBaseline();
        }

        public override void OnLostAuthority()
        {
            ApplyRootMotionPolicy();
        }

        private void EnsureInitialised()
        {
            if (_initialised) return;
            var a = Animator;
            if (a == null)
            {
                NebulaLog.Error($"NetworkAnimator on {name} has no Animator");
                return;
            }
            _initialised = true;
            _rootMotionWas = a.applyRootMotion;
            var ps = a.parameters;
            if (ps.Length > 255) NebulaLog.Warn($"NetworkAnimator on {name}: only the first 255 of {ps.Length} parameters are synchronised");
            int n = Math.Min(ps.Length, 255);
            _params = new Param[n];
            for (int i = 0; i < n; i++)
            {
                _params[i] = new Param
                {
                    Hash = ps[i].nameHash,
                    Type = ps[i].type,
                    Skip = ps[i].type == AnimatorControllerParameterType.Trigger || a.IsParameterControlledByCurve(ps[i].nameHash),
                };
            }
            _layers = new LayerTrack[a.layerCount];
            CaptureBaseline();
        }

        private void CaptureBaseline()
        {
            var a = Animator;
            for (int i = 0; i < _params.Length; i++)
            {
                ref var p = ref _params[i];
                switch (p.Type)
                {
                    case AnimatorControllerParameterType.Int: p.LastInt = a.GetInteger(p.Hash); break;
                    case AnimatorControllerParameterType.Float: p.LastFloat = a.GetFloat(p.Hash); break;
                    case AnimatorControllerParameterType.Bool: p.LastBool = a.GetBool(p.Hash); break;
                }
            }
            for (int l = 0; l < _layers.Length; l++)
            {
                _layers[l].LastStateHash = a.GetCurrentAnimatorStateInfo(l).fullPathHash;
                _layers[l].LastWeight = a.GetLayerWeight(l);
                _layers[l].LastNextHash = a.IsInTransition(l) ? a.GetNextAnimatorStateInfo(l).fullPathHash : 0;
            }
        }

        private void ApplyRootMotionPolicy()
        {
            if (!DisableRootMotionOnRemote || Animator == null) return;
            Animator.applyRootMotion = _rootMotionWas && (IsSyncAuthority || IsRelayingWorker);
        }

        // ---- public API ------------------------------------------------------------------------------------

        /// <summary>
        /// Fire a trigger on every copy. On the authority it fires locally and is replicated; an owning client under
        /// server authority forwards the request to the worker (one round trip of latency, as in NGO).
        /// </summary>
        public void SetTrigger(string name) => SetTrigger(Animator.StringToHash(name));

        public void SetTrigger(int hash)
        {
            if (IsSyncAuthority)
            {
                Animator.SetTrigger(hash);
                _pendingTriggers.Add(hash);
                MarkSyncDirty();
            }
            else if (IsOwner)
            {
                ServerRpc(RpcRequestTrigger, hash);
            }
            else
            {
                NebulaLog.Warn($"NetworkAnimator.SetTrigger on {name} from a copy that is neither the authority nor the owner; ignored");
            }
        }

        /// <summary>Reset a trigger locally on this copy. Never replicated (a trigger that was sent is already consumed remotely).</summary>
        public void ResetTrigger(string name) => ResetTrigger(Animator.StringToHash(name));

        public void ResetTrigger(int hash)
        {
            Animator.ResetTrigger(hash);
            _pendingTriggers.Remove(hash);
        }

        [ServerRpc]
        private void RpcRequestTrigger(int hash)
        {
            if (!HasAuthority || IsOwnerAuthoritative) return;
            SetTrigger(hash);
        }

        // ---- authority: change detection and writing -------------------------------------------------------

        protected override void AuthorityTick(uint tick, float deltaTime)
        {
            EnsureInitialised();
            if (!_initialised) return;
            var a = Animator;
            if (SyncParameters)
            {
                for (int i = 0; i < _params.Length; i++)
                {
                    ref var p = ref _params[i];
                    if (p.Skip) continue;
                    bool changed = false;
                    switch (p.Type)
                    {
                        case AnimatorControllerParameterType.Int:
                        {
                            int v = a.GetInteger(p.Hash);
                            if (v != p.LastInt) { p.LastInt = v; changed = true; }
                            break;
                        }
                        case AnimatorControllerParameterType.Float:
                        {
                            float v = a.GetFloat(p.Hash);
                            if (Mathf.Abs(v - p.LastFloat) >= FloatThreshold) { p.LastFloat = v; changed = true; }
                            break;
                        }
                        case AnimatorControllerParameterType.Bool:
                        {
                            bool v = a.GetBool(p.Hash);
                            if (v != p.LastBool) { p.LastBool = v; changed = true; }
                            break;
                        }
                    }
                    if (changed && !_dirtyParams.Contains(i)) _dirtyParams.Add(i);
                }
            }
            if (SyncLayerStates || SyncLayerWeights || SyncTransitions)
            {
                for (int l = 0; l < _layers.Length; l++)
                {
                    ref var t = ref _layers[l];
                    bool changed = false;
                    int state = a.GetCurrentAnimatorStateInfo(l).fullPathHash;
                    if (SyncLayerStates && state != t.LastStateHash) { t.LastStateHash = state; changed = true; }
                    float w = a.GetLayerWeight(l);
                    if (SyncLayerWeights && Mathf.Abs(w - t.LastWeight) >= 0.001f) { t.LastWeight = w; changed = true; }
                    int next = a.IsInTransition(l) ? a.GetNextAnimatorStateInfo(l).fullPathHash : 0;
                    if (SyncTransitions && next != t.LastNextHash) { t.LastNextHash = next; changed = next != 0 || changed; }
                    if (changed && !_dirtyLayers.Contains(l)) _dirtyLayers.Add(l);
                }
            }
            if (_dirtyParams.Count > 0 || _dirtyLayers.Count > 0 || _pendingTriggers.Count > 0) MarkSyncDirty();
        }

        public override void WriteSyncState(NetworkWriter writer, bool full)
        {
            EnsureInitialised();
            var a = Animator;
            var fields = Fields.None;
            bool parameters = SyncParameters && (full || _dirtyParams.Count > 0);
            bool triggers = _pendingTriggers.Count > 0;
            bool layers = (SyncLayerStates || SyncLayerWeights || SyncTransitions) && (full || _dirtyLayers.Count > 0);
            if (parameters) fields |= Fields.Parameters;
            if (triggers) fields |= Fields.Triggers;
            if (layers) fields |= Fields.Layers;
            writer.WriteByte((byte)fields);

            if (parameters)
            {
                int countAt = writer.Length;
                writer.WriteByte(0);
                byte n = 0;
                for (int i = 0; i < _params.Length; i++)
                {
                    ref var p = ref _params[i];
                    if (p.Skip) continue;
                    if (!full && !_dirtyParams.Contains(i)) continue;
                    writer.WriteByte((byte)i);
                    switch (p.Type)
                    {
                        case AnimatorControllerParameterType.Int: writer.WriteInt(a.GetInteger(p.Hash)); break;
                        case AnimatorControllerParameterType.Float: writer.WriteFloat(a.GetFloat(p.Hash)); break;
                        case AnimatorControllerParameterType.Bool: writer.WriteBool(a.GetBool(p.Hash)); break;
                    }
                    n++;
                }
                writer.Buffer[countAt] = n;
            }
            if (triggers)
            {
                writer.WriteByte((byte)Math.Min(_pendingTriggers.Count, 255));
                for (int i = 0; i < _pendingTriggers.Count && i < 255; i++) writer.WriteInt(_pendingTriggers[i]);
            }
            if (layers)
            {
                int countAt = writer.Length;
                writer.WriteByte(0);
                byte n = 0;
                for (int l = 0; l < _layers.Length; l++)
                {
                    if (!full && !_dirtyLayers.Contains(l)) continue;
                    var info = a.GetCurrentAnimatorStateInfo(l);
                    writer.WriteByte((byte)l);
                    writer.WriteInt(info.fullPathHash);
                    writer.WriteFloat(info.normalizedTime);
                    writer.WriteFloat(a.GetLayerWeight(l));
                    bool inTransition = SyncTransitions && a.IsInTransition(l);
                    writer.WriteBool(inTransition);
                    if (inTransition)
                    {
                        var next = a.GetNextAnimatorStateInfo(l);
                        var tr = a.GetAnimatorTransitionInfo(l);
                        writer.WriteInt(next.fullPathHash);
                        writer.WriteFloat(tr.duration);
                        writer.WriteFloat(tr.normalizedTime);
                    }
                    n++;
                }
                writer.Buffer[countAt] = n;
            }
        }

        protected internal override void OnSyncStateSent()
        {
            _dirtyParams.Clear();
            _dirtyLayers.Clear();
            _pendingTriggers.Clear();
        }

        // ---- non-authoritative copies ----------------------------------------------------------------------

        public override void ReadSyncState(NetworkReader reader, uint tick, bool full)
        {
            EnsureInitialised();
            var a = Animator;
            bool apply = !IsSyncAuthority && _initialised;
            bool relay = IsRelayingWorker;
            var fields = (Fields)reader.ReadByte();

            if ((fields & Fields.Parameters) != 0)
            {
                int n = reader.ReadByte();
                for (int k = 0; k < n; k++)
                {
                    int i = reader.ReadByte();
                    bool known = i < _params.Length;
                    // The prefab is the same everywhere, so the type at this index is the type on the wire.
                    var type = known ? _params[i].Type : AnimatorControllerParameterType.Int;
                    switch (type)
                    {
                        case AnimatorControllerParameterType.Int:
                        {
                            int v = reader.ReadInt();
                            if (apply && known) { a.SetInteger(_params[i].Hash, v); _params[i].LastInt = v; }
                            break;
                        }
                        case AnimatorControllerParameterType.Float:
                        {
                            float v = reader.ReadFloat();
                            if (apply && known) { a.SetFloat(_params[i].Hash, v); _params[i].LastFloat = v; }
                            break;
                        }
                        case AnimatorControllerParameterType.Bool:
                        {
                            bool v = reader.ReadBool();
                            if (apply && known) { a.SetBool(_params[i].Hash, v); _params[i].LastBool = v; }
                            break;
                        }
                    }
                    if (relay && known && !_dirtyParams.Contains(i)) _dirtyParams.Add(i);
                }
            }
            if ((fields & Fields.Triggers) != 0)
            {
                int n = reader.ReadByte();
                for (int k = 0; k < n; k++)
                {
                    int hash = reader.ReadInt();
                    if (apply) a.SetTrigger(hash);
                    if (relay) _pendingTriggers.Add(hash);
                }
            }
            if ((fields & Fields.Layers) != 0)
            {
                int n = reader.ReadByte();
                for (int k = 0; k < n; k++)
                {
                    int l = reader.ReadByte();
                    int stateHash = reader.ReadInt();
                    float normalizedTime = reader.ReadFloat();
                    float weight = reader.ReadFloat();
                    bool inTransition = reader.ReadBool();
                    int nextHash = 0; float duration = 0f, transitionTime = 0f;
                    if (inTransition)
                    {
                        nextHash = reader.ReadInt();
                        duration = reader.ReadFloat();
                        transitionTime = reader.ReadFloat();
                    }
                    if (!apply || l >= _layers.Length) continue;
                    ApplyLayer(l, stateHash, normalizedTime, weight, inTransition, nextHash, duration, transitionTime, full);
                    if (relay && !_dirtyLayers.Contains(l)) _dirtyLayers.Add(l);
                }
            }
        }

        private void ApplyLayer(int layer, int stateHash, float normalizedTime, float weight, bool inTransition, int nextHash, float duration, float transitionTime, bool full)
        {
            var a = Animator;
            if (SyncLayerWeights && layer > 0) a.SetLayerWeight(layer, weight);
            if (!SyncLayerStates) return;
            var current = a.GetCurrentAnimatorStateInfo(layer);
            bool alreadyHeadingThere = a.IsInTransition(layer) && a.GetNextAnimatorStateInfo(layer).fullPathHash == stateHash;
            if (current.fullPathHash != stateHash && !alreadyHeadingThere)
            {
                a.Play(stateHash, layer, normalizedTime);
            }
            else if (full && current.fullPathHash == stateHash && !a.IsInTransition(layer))
            {
                // Same state: only re-seat the playhead if it drifted (a looping state compares fractional parts).
                float mine = current.loop ? current.normalizedTime - Mathf.Floor(current.normalizedTime) : current.normalizedTime;
                float theirs = current.loop ? normalizedTime - Mathf.Floor(normalizedTime) : normalizedTime;
                float drift = Mathf.Abs(mine - theirs);
                if (current.loop) drift = Mathf.Min(drift, 1f - drift);
                if (drift > NormalizedTimeResyncThreshold) a.Play(stateHash, layer, normalizedTime);
            }
            if (SyncTransitions && inTransition && nextHash != 0)
            {
                bool heading = a.IsInTransition(layer) && a.GetNextAnimatorStateInfo(layer).fullPathHash == nextHash;
                if (!heading && a.GetCurrentAnimatorStateInfo(layer).fullPathHash != nextHash)
                {
                    a.CrossFadeInFixedTime(nextHash, Mathf.Max(0f, duration), layer, 0f, Mathf.Clamp01(transitionTime));
                }
            }
            _layers[layer].LastStateHash = stateHash;
            _layers[layer].LastWeight = weight;
            _layers[layer].LastNextHash = inTransition ? nextHash : 0;
        }
    }
}
