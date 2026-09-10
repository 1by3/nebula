using System.Text;

namespace Nebula.Cli.Core;

/// <summary>Console output and prompts. Everything the user sees goes through here so it looks the same everywhere.</summary>
public static class Ui
{
    private static readonly bool Color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null;

    private static void Write(string prefix, ConsoleColor color, string message, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (Color) Console.ForegroundColor = color;
        writer.Write(prefix);
        if (Color) Console.ResetColor();
        writer.WriteLine(message);
    }

    public static void Step(string message) => Write("==> ", ConsoleColor.Cyan, message);
    public static void Ok(string message) => Write("  + ", ConsoleColor.Green, message);
    public static void Info(string message) => Console.WriteLine("    " + message);
    public static void Warn(string message) => Write("  ! ", ConsoleColor.Yellow, message);
    public static void Fail(string message) => Write("error: ", ConsoleColor.Red, message, Console.Error);
    public static void Hint(string message) => Write("hint: ", ConsoleColor.DarkGray, message, Console.Error);
    public static void Verbose(string message)
    {
        if (Context.VerboseEnabled) Write("  . ", ConsoleColor.DarkGray, message);
    }

    public static void Title(string message)
    {
        if (Color) Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(message);
        if (Color) Console.ResetColor();
    }

    public static void Blank() => Console.WriteLine();

    public static void Table(string[] header, IEnumerable<string[]> rows)
    {
        var all = new List<string[]> { header };
        all.AddRange(rows);
        int cols = header.Length;
        var widths = new int[cols];
        foreach (var r in all)
            for (int c = 0; c < cols && c < r.Length; c++)
                widths[c] = Math.Max(widths[c], r[c].Length);
        for (int i = 0; i < all.Count; i++)
        {
            var sb = new StringBuilder("    ");
            for (int c = 0; c < cols; c++)
            {
                string cell = c < all[i].Length ? all[i][c] : "";
                sb.Append(cell.PadRight(widths[c] + 2));
            }
            if (i == 0 && Color) Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(sb.ToString().TrimEnd());
            if (i == 0 && Color) Console.ResetColor();
        }
    }

    // --- prompts ---------------------------------------------------------------------------------

    private static void RequireInteractive(string what)
    {
        if (Console.IsInputRedirected)
            throw new CliError($"{what} needs an interactive terminal", "pass the value as an option, or run without redirected input");
    }

    public static string Ask(string prompt, string? fallback = null)
    {
        RequireInteractive($"'{prompt}'");
        while (true)
        {
            Console.Write(fallback != null ? $"{prompt} [{fallback}]: " : $"{prompt}: ");
            string? line = Console.ReadLine();
            if (line == null) throw new OperationCanceledException();
            line = line.Trim();
            if (line.Length > 0) return line;
            if (fallback != null) return fallback;
        }
    }

    public static string AskSecret(string prompt)
    {
        RequireInteractive($"'{prompt}'");
        Console.Write($"{prompt}: ");
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) throw new OperationCanceledException();
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        return sb.ToString().Trim();
    }

    public static bool Confirm(string prompt, bool fallback, bool yes)
    {
        if (yes) return true;
        RequireInteractive($"'{prompt}'");
        Console.Write($"{prompt} [{(fallback ? "Y/n" : "y/N")}]: ");
        string? line = Console.ReadLine();
        if (line == null) throw new OperationCanceledException();
        line = line.Trim().ToLowerInvariant();
        if (line.Length == 0) return fallback;
        return line is "y" or "yes";
    }

    /// <summary>Numbered menu; returns the chosen index.</summary>
    public static int Choose(string prompt, IReadOnlyList<string> options, int fallback = 0)
    {
        RequireInteractive($"'{prompt}'");
        Console.WriteLine(prompt);
        for (int i = 0; i < options.Count; i++) Console.WriteLine($"  {i + 1}) {options[i]}");
        while (true)
        {
            Console.Write($"choice [{fallback + 1}]: ");
            string? line = Console.ReadLine();
            if (line == null) throw new OperationCanceledException();
            line = line.Trim();
            if (line.Length == 0) return fallback;
            if (int.TryParse(line, out int n) && n >= 1 && n <= options.Count) return n - 1;
        }
    }
}
