namespace Nebula.Cli.Core;

/// <summary>Directory copying: a one-way mirror (like robocopy /MIR) and a plain recursive copy with exclusions.</summary>
public static class FileSync
{
    /// <summary>Files the last Mirror could not copy because a process held them (Unity's own caches, usually).</summary>
    public static readonly List<string> Skipped = new();

    /// <summary>
    /// Copy, retrying briefly when the source or the destination is held open by another process (the Editor
    /// rewrites files under Library while it runs; a previous build's helper may still hold the scratch copy).
    /// Returns false when the file stays locked: the caller decides whether it matters.
    /// </summary>
    private static bool CopyWithRetry(string src, string dst)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(src, dst, overwrite: true);
                File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                return true;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(250 * (attempt + 1));
            }
            catch (IOException e)
            {
                Ui.Verbose($"skipping locked file {dst}: {e.Message}");
                return false;
            }
        }
    }
    /// <summary>Make <paramref name="dst"/> identical to <paramref name="src"/>: copy new/changed files, delete extras.</summary>
    public static (int copied, int deleted) Mirror(string src, string dst, Func<string, bool>? excludeDir = null)
    {
        int copied = 0, deleted = 0;
        Directory.CreateDirectory(dst);
        var srcDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var srcFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in Directory.EnumerateDirectories(src))
        {
            string name = Path.GetFileName(d);
            if (excludeDir != null && excludeDir(name)) continue;
            srcDirs.Add(name);
            var r = Mirror(d, Path.Combine(dst, name), excludeDir);
            copied += r.copied;
            deleted += r.deleted;
        }
        foreach (var f in Directory.EnumerateFiles(src))
        {
            string name = Path.GetFileName(f);
            srcFiles.Add(name);
            string target = Path.Combine(dst, name);
            var si = new FileInfo(f);
            var ti = new FileInfo(target);
            if (ti.Exists && ti.Length == si.Length && ti.LastWriteTimeUtc == si.LastWriteTimeUtc) continue;
            if (CopyWithRetry(f, target)) copied++;
            else Skipped.Add(target);
        }
        foreach (var d in Directory.EnumerateDirectories(dst))
        {
            if (srcDirs.Contains(Path.GetFileName(d))) continue;
            if (excludeDir != null && excludeDir(Path.GetFileName(d))) continue;
            Directory.Delete(d, recursive: true);
            deleted++;
        }
        foreach (var f in Directory.EnumerateFiles(dst))
        {
            if (srcFiles.Contains(Path.GetFileName(f))) continue;
            File.Delete(f);
            deleted++;
        }
        return (copied, deleted);
    }

    /// <summary>Copy a tree, overwriting files that exist. Returns the number of files written.</summary>
    public static int Copy(string src, string dst, Func<string, bool>? excludeDir = null, Func<string, bool>? skipFile = null)
    {
        int n = 0;
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.EnumerateDirectories(src))
        {
            string name = Path.GetFileName(d);
            if (excludeDir != null && excludeDir(name)) continue;
            n += Copy(d, Path.Combine(dst, name), excludeDir, skipFile);
        }
        foreach (var f in Directory.EnumerateFiles(src))
        {
            string target = Path.Combine(dst, Path.GetFileName(f));
            if (skipFile != null && skipFile(target)) continue;
            File.Copy(f, target, overwrite: true);
            n++;
        }
        return n;
    }
}
