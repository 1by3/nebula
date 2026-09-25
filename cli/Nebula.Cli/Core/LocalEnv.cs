namespace Nebula.Cli.Core;

/// <summary>
/// The project's .env.nebula: environment variables for the workers of local runs (<c>nebula start</c> and the
/// Editor). Kept out of version control; <c>nebula env pull --out .env.nebula</c> fills it from a deployment.
/// </summary>
public static class LocalEnv
{
    public const string FileName = Nebula.NebulaEnv.LocalFileName;

    public static string PathOf(NebulaProject project) => Path.Combine(project.Root, FileName);

    /// <summary>
    /// Check the file and report what the workers will get: a warning per line or name it skips (never a value).
    /// Returns the variables, or null when there is no file.
    /// </summary>
    public static Dictionary<string, string>? Check(NebulaProject project)
    {
        string path = PathOf(project);
        if (!File.Exists(path)) return null;
        return Nebula.NebulaEnv.ReadFile(path, m => Ui.Warn(m));
    }
}
