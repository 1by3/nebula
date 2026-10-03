namespace Nebula
{
    /// <summary>The scripting backend of a Linux dedicated server build (<see cref="NebulaConfig.ServerBuildScriptingBackend"/>).</summary>
    public enum NebulaServerScriptingBackend
    {
        /// <summary>Mono: fast builds, JIT at run time. The default.</summary>
        Mono = 0,
        /// <summary>IL2CPP: ahead-of-time C++, usually faster at run time; managed code stripping applies.</summary>
        IL2CPP = 1,
    }
}
