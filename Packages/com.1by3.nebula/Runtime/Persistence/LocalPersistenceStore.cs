using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Nebula
{
    /// <summary>
    /// In-process <see cref="IPersistenceStore"/> with the exact write rules of the persistence module (epoch check,
    /// version bump), optionally backed by a file so a single-process run survives a restart. This is what
    /// <c>-nebula-persistence-mode local</c> uses (and what the tests use); a mesh wants
    /// the orchestrator's database instead (<see cref="RemotePersistenceStore"/> on every worker), because every
    /// worker needs to see the same records.
    /// <para>
    /// Writes land in memory at once. Ordinary writes are debounced by <see cref="WriteIntervalSeconds"/>; durability
    /// barriers are grouped for <see cref="WriteBarrierIntervalSeconds"/> and share a background rewrite from an
    /// immutable snapshot. Callbacks are delivered from <see cref="Tick"/> after that rewrite has completed, so game
    /// code sees the same main-thread ordering whichever store it runs on.
    /// </para>
    /// </summary>
    public sealed class LocalPersistenceStore : IPersistenceStore
    {
        /// <summary>Magic and version at the head of the backing file.</summary>
        private const uint FileMagic = 0x504e4245; // "EBNP"
        private const byte FileVersion = 1;

        /// <summary>Seconds between rewrites of the backing file while records keep changing.</summary>
        public float WriteIntervalSeconds = 1f;
        /// <summary>Time used to gather nearby durability barriers before starting each rewrite.</summary>
        public float WriteBarrierIntervalSeconds = 0.1f;

        private readonly Dictionary<string, PersistedEntityRecord> _records = new Dictionary<string, PersistedEntityRecord>();
        private readonly Queue<Action> _callbacks = new Queue<Action>();
        private readonly List<(long Version, Action Done)> _writeBarriers = new List<(long, Action)>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly string _filePath;
        private bool _fileDirty;
        private double _nextWrite;
        private double _barrierWriteAt = double.PositiveInfinity;
        private long _changeVersion;
        private long _durableVersion;
        private Task<WriteResult> _writeTask;

        private sealed class WriteResult
        {
            public long Version;
            public double Milliseconds;
            public Exception Error;
        }

        /// <summary>An in-memory store that forgets everything when the process ends.</summary>
        public LocalPersistenceStore() : this(null) { }

        /// <summary>A store backed by <paramref name="filePath"/>; null or empty keeps the records in memory only.</summary>
        public LocalPersistenceStore(string filePath)
        {
            _filePath = string.IsNullOrEmpty(filePath) ? null : filePath;
        }

        public bool IsConnected { get; private set; }
        public string Backend => _filePath == null ? "memory" : "local";
        public int KnownCount => _records.Count;
        /// <summary>Where the records are written, or "" for an in-memory store.</summary>
        public string FilePath => _filePath ?? "";
        /// <summary>Successful full-file rewrites during this store's lifetime (useful for diagnostics).</summary>
        public long FileWriteCount { get; private set; }
        /// <summary>Total milliseconds spent in successful full-file rewrites during this store's lifetime.</summary>
        public double TotalFileWriteMilliseconds { get; private set; }
        /// <summary>Milliseconds spent in the most recent successful full-file rewrite.</summary>
        public double LastFileWriteMilliseconds { get; private set; }

        public void Connect()
        {
            if (IsConnected) return;
            if (_filePath != null) LoadFile();
            IsConnected = true;
            NebulaLog.Info($"persistence: local store ready ({(_filePath == null ? "in memory" : _filePath)}), {_records.Count} records");
        }

        public void Tick()
        {
            FinishBackgroundWrite(false);
            while (_callbacks.Count > 0)
            {
                var cb = _callbacks.Dequeue();
                try { cb(); }
                catch (Exception ex) { NebulaLog.Error($"persistence callback threw: {ex}"); }
            }
            if (!_fileDirty || _filePath == null) return;
            double now = _clock.Elapsed.TotalSeconds;
            if (_writeTask != null || now < _nextWrite && now < _barrierWriteAt) return;
            StartBackgroundWrite();
        }

        public void Save(PersistedEntityRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Key)) return;
            if (_records.TryGetValue(record.Key, out var existing))
            {
                // A worker that lost authority must not overwrite what its successor already wrote.
                if (record.Epoch < existing.Epoch)
                {
                    NebulaLog.Debugf($"persistence: stale save for {record.Key} (epoch {record.Epoch} < {existing.Epoch}); dropped");
                    return;
                }
                record.Version = existing.Version + 1;
            }
            else record.Version = 1;
            record.SavedAt = DateTime.UtcNow;
            _records[record.Key] = record.Clone();
            Changed();
        }

        public void Delete(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_records.Remove(key)) Changed();
        }

        public void WhenWritten(Action onWritten)
        {
            if (onWritten == null) return;
            if (_filePath == null || _durableVersion >= _changeVersion)
            {
                _callbacks.Enqueue(onWritten);
                return;
            }
            _writeBarriers.Add((_changeVersion, onWritten));
            if (double.IsPositiveInfinity(_barrierWriteAt))
                _barrierWriteAt = _clock.Elapsed.TotalSeconds + Math.Max(0, WriteBarrierIntervalSeconds);
        }

        public void Load(string key, Action<PersistedEntityRecord> onLoaded)
        {
            if (onLoaded == null) return;
            var record = key != null && _records.TryGetValue(key, out var r) ? r.Clone() : null;
            _callbacks.Enqueue(() => onLoaded(record));
        }

        public void LoadContainer(string containerId, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded)
        {
            LoadWhere(r => r.CarrierKey == "" && r.ContainerId == (containerId ?? ""), onLoaded);
        }

        public void LoadCarried(string carrierKey, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded)
        {
            LoadWhere(r => r.CarrierKey == (carrierKey ?? ""), onLoaded);
        }

        public void LoadWhere(Func<PersistedEntityRecord, bool> predicate, Action<IReadOnlyList<PersistedEntityRecord>> onLoaded)
        {
            if (onLoaded == null) return;
            var result = new List<PersistedEntityRecord>();
            foreach (var kv in _records)
            {
                if (predicate == null || predicate(kv.Value)) result.Add(kv.Value.Clone());
            }
            _callbacks.Enqueue(() => onLoaded(result));
        }

        public void Clear()
        {
            if (_records.Count == 0 && !_fileDirty) return;
            _records.Clear();
            Changed();
        }

        public void Dispose()
        {
            FinishBackgroundWrite(true);
            if (_fileDirty && _filePath != null) WriteFileSynchronously();
            IsConnected = false;
            _callbacks.Clear();
            _writeBarriers.Clear();
        }

        /// <summary>Write the backing file now instead of waiting for the debounce (shutdown, tests).</summary>
        public void Flush()
        {
            FinishBackgroundWrite(true);
            if (_filePath != null && _fileDirty) WriteFileSynchronously();
        }

        // ---------------------------------------------------------------------------------------- file backing

        private void Changed()
        {
            _changeVersion++;
            if (!_fileDirty)
            {
                _fileDirty = true;
                _nextWrite = _clock.Elapsed.TotalSeconds + Math.Max(0, WriteIntervalSeconds);
            }
        }

        private void StartBackgroundWrite()
        {
            var snapshot = new List<PersistedEntityRecord>(_records.Values);
            long version = _changeVersion;
            string path = _filePath;
            _barrierWriteAt = double.PositiveInfinity;
            _writeTask = Task.Run(() => WriteSnapshot(path, snapshot, version));
        }

        private void FinishBackgroundWrite(bool wait)
        {
            if (_writeTask == null || !wait && !_writeTask.IsCompleted) return;
            WriteResult result = _writeTask.GetAwaiter().GetResult();
            _writeTask = null;
            ApplyWriteResult(result);
        }

        private void WriteFileSynchronously()
        {
            var result = WriteSnapshot(_filePath, new List<PersistedEntityRecord>(_records.Values), _changeVersion);
            ApplyWriteResult(result);
        }

        private void ApplyWriteResult(WriteResult result)
        {
            if (result.Error != null)
            {
                NebulaLog.Warn($"persistence: writing {_filePath} failed: {result.Error.Message}");
                double retry = Math.Max(0.05, WriteBarrierIntervalSeconds);
                _nextWrite = _clock.Elapsed.TotalSeconds + retry;
                if (_writeBarriers.Count > 0) _barrierWriteAt = _nextWrite;
                return;
            }
            LastFileWriteMilliseconds = result.Milliseconds;
            TotalFileWriteMilliseconds += result.Milliseconds;
            FileWriteCount++;
            if (result.Version > _durableVersion) _durableVersion = result.Version;
            _fileDirty = _durableVersion < _changeVersion;
            _nextWrite = _clock.Elapsed.TotalSeconds + Math.Max(0, WriteIntervalSeconds);
            for (int i = 0; i < _writeBarriers.Count;)
            {
                if (_writeBarriers[i].Version > _durableVersion) { i++; continue; }
                _callbacks.Enqueue(_writeBarriers[i].Done);
                _writeBarriers.RemoveAt(i);
            }
            // Writes accepted while the snapshot was being serialized need another commit. Give them a fresh group
            // window instead of starting a full rewrite on every tick while the disk is saturated.
            if (_fileDirty && _writeBarriers.Count > 0)
                _barrierWriteAt = _clock.Elapsed.TotalSeconds + Math.Max(0, WriteBarrierIntervalSeconds);
        }

        private static WriteResult WriteSnapshot(string path, IReadOnlyList<PersistedEntityRecord> records, long version)
        {
            var result = new WriteResult { Version = version };
            var timer = Stopwatch.StartNew();
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var w = new NetworkWriter(4096);
                w.WriteUInt(FileMagic);
                w.WriteByte(FileVersion);
                w.WriteInt(records.Count);
                for (int i = 0; i < records.Count; i++) WriteRecord(w, records[i]);
                // Write beside the file and move into place: a half-written save is worse than yesterday's.
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, w.ToArray());
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex) { result.Error = ex; }
            timer.Stop();
            result.Milliseconds = timer.Elapsed.TotalMilliseconds;
            return result;
        }

        private void LoadFile()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var r = new NetworkReader(File.ReadAllBytes(_filePath));
                if (r.ReadUInt() != FileMagic) { NebulaLog.Warn($"persistence: {_filePath} is not a Nebula persistence file; ignored"); return; }
                byte version = r.ReadByte();
                if (version > FileVersion) { NebulaLog.Warn($"persistence: {_filePath} is version {version}, this build reads {FileVersion}; ignored"); return; }
                int count = r.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    var record = ReadRecord(r);
                    if (!string.IsNullOrEmpty(record.Key)) _records[record.Key] = record;
                }
            }
            catch (Exception ex)
            {
                NebulaLog.Warn($"persistence: reading {_filePath} failed: {ex.Message}");
            }
        }

        private static void WriteRecord(NetworkWriter w, PersistedEntityRecord r)
        {
            w.WriteString(r.Key);
            w.WriteUShort(r.PrefabId);
            w.WriteString(r.PrefabName);
            w.WriteUInt(r.SceneId);
            w.WriteString(r.ContainerId);
            w.WriteString(r.CarrierKey);
            w.WriteVector3(r.LocalPosition);
            w.WriteQuaternion(r.LocalRotation);
            w.WriteVector3(r.Velocity);
            w.WriteUInt(r.Epoch);
            w.WriteBool(r.ServerDriven);
            w.WriteBool(r.Owned);
            w.WriteString(r.Name);
            w.WriteBytes(r.State);
            w.WriteULong(r.Version);
            w.WriteLong(r.SavedAt.ToBinary());
            w.WriteString(r.SavedBy);
        }

        private static PersistedEntityRecord ReadRecord(NetworkReader r)
        {
            var record = new PersistedEntityRecord
            {
                Key = r.ReadString(),
                PrefabId = r.ReadUShort(),
                PrefabName = r.ReadString(),
                SceneId = r.ReadUInt(),
                ContainerId = r.ReadString(),
                CarrierKey = r.ReadString(),
                LocalPosition = r.ReadVector3(),
                LocalRotation = r.ReadQuaternion(),
                Velocity = r.ReadVector3(),
                Epoch = r.ReadUInt(),
                ServerDriven = r.ReadBool(),
                Owned = r.ReadBool(),
                Name = r.ReadString(),
            };
            var state = r.ReadBytes();
            record.State = state ?? Array.Empty<byte>();
            record.Version = r.ReadULong();
            record.SavedAt = DateTime.FromBinary(r.ReadLong());
            record.SavedBy = r.ReadString();
            return record;
        }
    }
}
