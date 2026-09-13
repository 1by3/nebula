using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Nebula
{
    /// <summary>
    /// In-process <see cref="IPersistenceStore"/> with the exact write rules of the persistence module (epoch check,
    /// version bump), optionally backed by a file so a single-process run survives a restart. This is what
    /// <c>-nebula-persistence-mode local</c> uses (and what the tests use); a mesh wants
    /// <see cref="SpacetimePersistenceStore"/> instead, because every worker needs to see the same records.
    /// <para>
    /// Writes land in memory at once and the file is rewritten at most once per <see cref="WriteIntervalSeconds"/>
    /// from <see cref="Tick"/>, so a busy checkpoint pass costs no disk I/O. Callbacks are queued and delivered from
    /// <see cref="Tick"/> like the remote stores, so game code sees the same ordering whichever store it runs on.
    /// </para>
    /// </summary>
    public sealed class LocalPersistenceStore : IPersistenceStore
    {
        /// <summary>Magic and version at the head of the backing file.</summary>
        private const uint FileMagic = 0x504e4245; // "EBNP"
        private const byte FileVersion = 1;

        /// <summary>Seconds between rewrites of the backing file while records keep changing.</summary>
        public float WriteIntervalSeconds = 1f;

        private readonly Dictionary<string, PersistedEntityRecord> _records = new Dictionary<string, PersistedEntityRecord>();
        private readonly Queue<Action> _callbacks = new Queue<Action>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly string _filePath;
        private bool _fileDirty;
        private double _nextWrite;

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

        public void Connect()
        {
            if (IsConnected) return;
            if (_filePath != null) LoadFile();
            IsConnected = true;
            NebulaLog.Info($"persistence: local store ready ({(_filePath == null ? "in memory" : _filePath)}), {_records.Count} records");
        }

        public void Tick()
        {
            while (_callbacks.Count > 0)
            {
                var cb = _callbacks.Dequeue();
                try { cb(); }
                catch (Exception ex) { NebulaLog.Error($"persistence callback threw: {ex}"); }
            }
            if (!_fileDirty || _filePath == null) return;
            double now = _clock.Elapsed.TotalSeconds;
            if (now < _nextWrite) return;
            _nextWrite = now + WriteIntervalSeconds;
            WriteFile();
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
            _fileDirty = true;
        }

        public void Delete(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_records.Remove(key)) _fileDirty = true;
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
            _fileDirty = true;
            if (_filePath != null) WriteFile();
        }

        public void Dispose()
        {
            if (_fileDirty && _filePath != null) WriteFile();
            IsConnected = false;
            _callbacks.Clear();
        }

        /// <summary>Write the backing file now instead of waiting for the debounce (shutdown, tests).</summary>
        public void Flush()
        {
            if (_filePath != null) WriteFile();
        }

        // ---------------------------------------------------------------------------------------- file backing

        private void WriteFile()
        {
            _fileDirty = false;
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var w = new NetworkWriter(4096);
                w.WriteUInt(FileMagic);
                w.WriteByte(FileVersion);
                w.WriteInt(_records.Count);
                foreach (var kv in _records) WriteRecord(w, kv.Value);
                // Write beside the file and move into place: a half-written save is worse than yesterday's.
                string temp = _filePath + ".tmp";
                File.WriteAllBytes(temp, w.ToArray());
                if (File.Exists(_filePath)) File.Delete(_filePath);
                File.Move(temp, _filePath);
            }
            catch (Exception ex)
            {
                NebulaLog.Warn($"persistence: writing {_filePath} failed: {ex.Message}");
            }
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
