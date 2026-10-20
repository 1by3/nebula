using System;
using System.IO;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// <see cref="ISharedCacheStore"/> in a folder, one file per object. Used when the process is its own cache: the
    /// Editor (including Multiplayer Play Mode), a single-process run, and an orchestrator that keeps its data in files.
    /// <para>
    /// A file is named after the hash of its key, so any valid key is a safe file name on every platform and keys
    /// that differ only by letter case do not collide. It holds the key, the checksum and the bytes. An object is
    /// written to a temporary file in the same folder and then moved into place, so a reader never sees a half-written
    /// file and two processes that write the same key at once both succeed. The file's last-write time is the
    /// entry's last-read time: nothing else rewrites a finished file.
    /// </para>
    /// </summary>
    public sealed class FolderSharedCacheStore : ISharedCacheStore
    {
        private const string Extension = ".nsc";
        private const string TempExtension = ".tmp";
        private static readonly byte[] Magic = { (byte)'N', (byte)'S', (byte)'C', 1 };
        private const int ChecksumBytes = 32;
        /// <summary>A temporary file older than this belongs to a writer that died.</summary>
        private static readonly TimeSpan StaleTemp = TimeSpan.FromHours(1);

        private readonly string _root;

        public FolderSharedCacheStore(string folder)
        {
            if (string.IsNullOrEmpty(folder)) throw new ArgumentException("a folder is required", nameof(folder));
            _root = Path.GetFullPath(folder);
            Directory.CreateDirectory(_root);
        }

        public string Backend => "folder";

        /// <summary>The folder the store writes to.</summary>
        public string Folder => _root;

        public bool TryGet(string key, out byte[] bytes, out string checksum)
        {
            bytes = null;
            checksum = null;
            string path = PathFor(key);
            byte[] file;
            try { file = File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            if (!TryParse(file, key, out byte[] payload, out string sum))
            {
                // Truncated or altered on disk: forget it so the next put can write the real object.
                TryDelete(path);
                return false;
            }
            try
            {
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > SharedCacheKey.TouchIntervalSeconds)
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (IOException) { /* a refresh that loses a race with a sweep is harmless */ }
            bytes = payload;
            checksum = sum;
            return true;
        }

        public bool Put(string key, byte[] bytes, string checksum)
        {
            string path = PathFor(key);
            if (File.Exists(path)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + TempExtension;
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                    fs.Write(Magic, 0, Magic.Length);
                    fs.Write(BitConverter.GetBytes(keyBytes.Length), 0, 4);
                    fs.Write(keyBytes, 0, keyBytes.Length);
                    byte[] sum = FromHex(checksum);
                    fs.Write(sum, 0, ChecksumBytes);
                    fs.Write(bytes, 0, bytes.Length);
                }
                try { File.Move(temp, path); return true; }
                catch (IOException)
                {
                    // Another writer finished first: the key is stored, which is all this put wanted.
                    if (File.Exists(path)) return false;
                    throw;
                }
            }
            finally
            {
                TryDelete(temp);
            }
        }

        public int RemoveUnreadSince(DateTime cutoffUtc)
        {
            int removed = 0;
            var staleTemp = DateTime.UtcNow - StaleTemp;
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (file.EndsWith(Extension, StringComparison.Ordinal))
                    {
                        if (written < cutoffUtc && TryDelete(file)) removed++;
                    }
                    else if (file.EndsWith(TempExtension, StringComparison.Ordinal) && written < staleTemp) TryDelete(file);
                }
                catch (IOException) { }
            }
            return removed;
        }

        public int Clear()
        {
            int removed = 0;
            foreach (string file in Directory.EnumerateFiles(_root, "*" + Extension, SearchOption.AllDirectories))
                if (TryDelete(file)) removed++;
            return removed;
        }

        public long Count()
        {
            long n = 0;
            foreach (string _ in Directory.EnumerateFiles(_root, "*" + Extension, SearchOption.AllDirectories)) n++;
            return n;
        }

        public long TotalBytes()
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(_root, "*" + Extension, SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { }
            }
            return total;
        }

        public void Dispose() { }

        private string PathFor(string key)
        {
            string name = SharedCacheKey.Checksum(Encoding.UTF8.GetBytes(key));
            return Path.Combine(_root, name.Substring(0, 2), name + Extension);
        }

        private static bool TryParse(byte[] file, string key, out byte[] payload, out string checksum)
        {
            payload = null;
            checksum = null;
            if (file.Length < Magic.Length + 4 + ChecksumBytes) return false;
            for (int i = 0; i < Magic.Length; i++) if (file[i] != Magic[i]) return false;
            int keyLength = BitConverter.ToInt32(file, Magic.Length);
            int at = Magic.Length + 4;
            if (keyLength < 0 || keyLength > SharedCacheKey.MaxKeyLength * 4 || file.Length < at + keyLength + ChecksumBytes) return false;
            if (Encoding.UTF8.GetString(file, at, keyLength) != key) return false;
            at += keyLength;
            var sum = new byte[ChecksumBytes];
            Buffer.BlockCopy(file, at, sum, 0, ChecksumBytes);
            at += ChecksumBytes;
            payload = new byte[file.Length - at];
            Buffer.BlockCopy(file, at, payload, 0, payload.Length);
            checksum = SharedCacheKey.Hex(sum);
            if (SharedCacheKey.SameChecksum(SharedCacheKey.Checksum(payload), checksum)) return true;
            payload = null;
            checksum = null;
            return false;
        }

        private static byte[] FromHex(string hex)
        {
            if (hex == null || hex.Length != ChecksumBytes * 2) throw new ArgumentException("the checksum must be 64 hexadecimal characters", nameof(hex));
            var bytes = new byte[ChecksumBytes];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static bool TryDelete(string path)
        {
            try { File.Delete(path); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
