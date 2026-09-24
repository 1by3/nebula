using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Binds the clients of the Multiplayer Play Mode dev loop to the server their own session started, so a client
    /// never joins some other mesh that happens to hold the port. The server writes <c>Library/Nebula/dev-session.json</c>
    /// once its gateway and worker have bound their ports, or failed to, and deletes it when it stops. A client waits
    /// for the file, connects to the port it names while the process that wrote it is alive, and refuses with the
    /// server's reason when the server could not start.
    /// </summary>
    public static class EditorDevSession
    {
        /// <summary>What the server wrote.</summary>
        [Serializable]
        public sealed class Record
        {
            /// <summary><c>listening</c> or <c>failed</c>.</summary>
            public string state = "";
            /// <summary>The gateway port the server listens on, or failed to bind.</summary>
            public int port;
            /// <summary>The server's process id: a file whose writer is gone is stale.</summary>
            public int pid;
            /// <summary>The gateway's incarnation, so the log can tell one server start from the next.</summary>
            public string incarnation = "";
            /// <summary>Why the server could not start, when <see cref="state"/> is <c>failed</c>.</summary>
            public string error = "";
        }

        /// <summary>What a client should do about the server.</summary>
        public enum Verdict
        {
            /// <summary>No server of this session has started yet: keep waiting.</summary>
            Waiting,
            /// <summary>The server is listening: connect to <see cref="Record.port"/>.</summary>
            Ready,
            /// <summary>The server could not start: do not connect anywhere.</summary>
            Failed,
        }

        public const string ListeningState = "listening";
        public const string FailedState = "failed";

        /// <summary>The file: <c>&lt;project&gt;/Library/Nebula/dev-session.json</c>.</summary>
        public static string PathFor(string projectRoot) => System.IO.Path.Combine(EditorDevPaths.Folder(projectRoot), "dev-session.json");

        /// <summary>
        /// Judge a record. A missing record, one whose writer is no longer running (a server that crashed without
        /// removing it), or one in an unknown state means the session's server has not started yet.
        /// </summary>
        public static Verdict Evaluate(Record record, Func<int, bool> isAlive)
        {
            if (record == null || record.pid <= 0 || !isAlive(record.pid)) return Verdict.Waiting;
            if (record.state == FailedState) return Verdict.Failed;
            if (record.state == ListeningState && record.port > 0 && record.port <= 65535) return Verdict.Ready;
            return Verdict.Waiting;
        }

        /// <summary>The error a client logs when the server of its session could not start.</summary>
        public static string FailureMessage(Record record) =>
            $"The Editor-hosted server could not start ({record.error}). A client of this session does not join any other server. Stop the other mesh (nebula stop) or change NebulaConfig.EditorPortOffset, then press Play again.";

        /// <summary>Write the server's record; replaces the previous one.</summary>
        public static void Write(string path, Record record)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(record));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                NebulaLog.Warn($"dev session: could not write {path} ({e.Message}); clients of this session will not find the server");
            }
        }

        /// <summary>Read the record, or null when there is none or it is being written.</summary>
        public static Record Read(string path)
        {
            try { return File.Exists(path) ? JsonUtility.FromJson<Record>(File.ReadAllText(path)) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Remove the record when it is this process's, as a server stops.</summary>
        public static void Remove(string path, int pid)
        {
            try
            {
                var record = Read(path);
                if (record != null && record.pid == pid) File.Delete(path);
            }
            catch (Exception) { }
        }

        /// <summary>Whether a process with this id is running.</summary>
        public static bool IsAlive(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid)) return !process.HasExited;
            }
            catch (Exception) { return false; }
        }

        /// <summary>This process's id.</summary>
        public static int CurrentPid
        {
            get
            {
                using (var process = Process.GetCurrentProcess()) return process.Id;
            }
        }
    }
}
