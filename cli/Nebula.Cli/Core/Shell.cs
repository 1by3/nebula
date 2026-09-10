using System.Diagnostics;
using System.Text;

namespace Nebula.Cli.Core;

public sealed record ShellResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    public string Output => (Stdout + Stderr).Trim();
}

/// <summary>Running other programs: captured, streamed to the terminal, or detached from it.</summary>
public static class Shell
{
    /// <summary>Full path of a program on PATH (plus a few well-known install folders), or null.</summary>
    public static string? Which(string name, params string[] extraDirs)
    {
        var names = Platform.IsWindows ? new[] { name + ".exe", name + ".cmd", name + ".bat", name } : new[] { name };
        var dirs = new List<string>(extraDirs);
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var d in dirs)
        {
            foreach (var n in names)
            {
                try
                {
                    string p = Path.Combine(d, n);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        return null;
    }

    private static ProcessStartInfo Info(string exe, IEnumerable<string> args, string? cwd, IDictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (cwd != null) psi.WorkingDirectory = cwd;
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        return psi;
    }

    private static string Describe(string exe, IEnumerable<string> args) =>
        Path.GetFileName(exe) + " " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    /// <summary>Run and capture both streams. Never throws on a non-zero exit; check <see cref="ShellResult.Ok"/>.</summary>
    public static ShellResult Capture(string exe, IEnumerable<string> args, string? cwd = null, IDictionary<string, string>? env = null, string? stdin = null)
    {
        var list = args.ToList();
        Ui.Verbose("run: " + Describe(exe, list));
        var psi = Info(exe, list, cwd, env);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = stdin != null;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Exception e) { return new ShellResult(127, "", $"could not start {exe}: {e.Message}"); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin != null)
        {
            using var w = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
            w.NewLine = "\n";
            w.Write(stdin);
        }
        p.WaitForExit();
        return new ShellResult(p.ExitCode, so.ToString(), se.ToString());
    }

    /// <summary>Run with the terminal attached (the user sees and can answer the program). Returns the exit code.</summary>
    public static int Stream(string exe, IEnumerable<string> args, string? cwd = null, IDictionary<string, string>? env = null)
    {
        var list = args.ToList();
        Ui.Verbose("run: " + Describe(exe, list));
        var psi = Info(exe, list, cwd, env);
        using var p = Process.Start(psi) ?? throw new CliError($"could not start {exe}");
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Like <see cref="Stream"/> but fails loudly on a non-zero exit.</summary>
    public static void StreamOrThrow(string exe, IEnumerable<string> args, string what, string? cwd = null, IDictionary<string, string>? env = null)
    {
        int code = Stream(exe, args, cwd, env);
        if (code != 0) throw new CliError($"{what} failed (exit {code})");
    }

    /// <summary>
    /// Start a long-lived program that must outlive this CLI, with its output going to <paramref name="logFile"/>
    /// (or nowhere). Returns the launcher's pid where known; callers find the real process by name later.
    /// </summary>
    public static void Detach(string exe, IEnumerable<string> args, string? cwd, string? logFile)
    {
        var list = args.ToList();
        Ui.Verbose("detach: " + Describe(exe, list) + (logFile != null ? $" > {logFile}" : ""));
        if (logFile != null) Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        if (Platform.IsWindows)
        {
            // ShellExecute passes no handles to the child, so a pipe that captures this CLI's output is not held
            // open by the mesh after we exit (a child started the plain way inherits stdout and keeps it open).
            if (logFile == null)
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var a in list) psi.ArgumentList.Add(a);
                if (cwd != null) psi.WorkingDirectory = cwd;
                Process.Start(psi);
                return;
            }
            // A hidden cmd.exe owns the redirect to the log file.
            string inner = "\"" + exe + "\" " + string.Join(" ", list.Select(Quote)) + " > \"" + logFile + "\" 2>&1";
            var cmd = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "/c \"" + inner + "\"",
            };
            if (cwd != null) cmd.WorkingDirectory = cwd;
            Process.Start(cmd);
        }
        else
        {
            string redirect = logFile != null ? $"> {ShQuote(logFile)} 2>&1" : "> /dev/null 2>&1";
            string line = $"nohup {ShQuote(exe)} {string.Join(" ", list.Select(ShQuote))} {redirect} < /dev/null &";
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(line);
            if (cwd != null) psi.WorkingDirectory = cwd;
            using var p = Process.Start(psi);
            p?.WaitForExit();
        }
    }

    private static string Quote(string a) => a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
    private static string ShQuote(string a) => "'" + a.Replace("'", "'\\''") + "'";

    /// <summary>Kill every running process whose executable lives under <paramref name="directory"/> or has one of the names.</summary>
    public static int KillProcesses(string? directory, params string[] processNames)
    {
        int killed = 0;
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == self) continue;
            try
            {
                bool match = processNames.Any(n => string.Equals(p.ProcessName, n, StringComparison.OrdinalIgnoreCase));
                if (!match && directory != null)
                {
                    string? file = null;
                    try { file = p.MainModule?.FileName; } catch { }
                    match = file != null && file.StartsWith(directory, Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                }
                if (!match) continue;
                Ui.Verbose($"kill {p.ProcessName} pid {p.Id}");
                p.Kill(entireProcessTree: true);
                killed++;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return killed;
    }

    /// <summary>Processes running from an executable under <paramref name="directory"/> (never this CLI itself, whose name is also "nebula").</summary>
    public static IEnumerable<Process> RunningUnder(string directory)
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == self) continue;
            string? file = null;
            try { file = p.MainModule?.FileName; } catch { }
            if (file != null && file.StartsWith(directory, Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                yield return p;
        }
    }
}
