using System;
using System.IO;

namespace Nebula
{
    /// <summary>
    /// Where <see cref="ControlPlaneHost"/> keeps the control plane between orchestrator runs: one JSON document
    /// (<see cref="ControlPlaneJson"/>), written a little after every change and read once at startup. The document
    /// is small and changes at most a few times a second, so the contract is whole-document replace. A store that
    /// throws is logged and retried on the next change; the mesh keeps running on the in-memory state.
    /// </summary>
    public interface IControlPlaneStorage : IDisposable
    {
        /// <summary>Short backend name for logs and the dashboard ("memory", "file", "sqlite", "postgres").</summary>
        string Backend { get; }
        /// <summary>The stored document, or null when there is none.</summary>
        string Load();
        /// <summary>Replace the stored document.</summary>
        void Save(string json);
    }

    /// <summary>Keeps nothing: the control plane starts empty on every orchestrator run.</summary>
    public sealed class MemoryControlPlaneStorage : IControlPlaneStorage
    {
        public string Backend => "memory";
        public string Load() => null;
        public void Save(string json) { }
        public void Dispose() { }
    }

    /// <summary>The document in one file, written beside it and moved into place so a crash mid-write keeps the previous copy.</summary>
    public sealed class FileControlPlaneStorage : IControlPlaneStorage, IGatewaySessionStore
    {
        public string FilePath { get; }
        public string Backend => "file";

        public FileControlPlaneStorage(string filePath)
        {
            FilePath = filePath;
        }

        public string Load()
        {
            if (!File.Exists(FilePath)) return null;
            return File.ReadAllText(FilePath);
        }

        public void Save(string json)
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temp, FilePath);
        }

        public void Dispose() { }

        private string SessionPath(string identity)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return Path.Combine(FilePath + ".sessions", BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity))).Replace("-", "") + ".session");
        }

        string IGatewaySessionStore.LoadSession(string identity)
        {
            string path = SessionPath(identity);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        void IGatewaySessionStore.SaveSession(string identity, string value)
        {
            string path = SessionPath(identity);
            if (value == null) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, value);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        void IGatewaySessionStore.ClearSessions()
        {
            string directory = FilePath + ".sessions";
            if (!Directory.Exists(directory)) return;
            foreach (string path in Directory.GetFiles(directory, "*.session")) File.Delete(path);
        }
    }
}
