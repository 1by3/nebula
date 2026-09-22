using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Nebula
{
    /// <summary>
    /// Loads and hosts the one <see cref="IGatewayExtension"/> a standalone gateway was configured with, and
    /// stands between it and the gateway: it owns the context, the cross-thread post queue and the exception
    /// isolation, so a game's code cannot take the gateway down and a throwing policy cannot let an entity
    /// through.
    /// <para>
    /// Nothing is ever discovered: <see cref="NebulaConfig.GatewayExtension"/> names one file, and either that
    /// file loads and yields exactly one extension type or the gateway refuses to start. A gateway fleet is
    /// consistent for the same reason — every gateway reads the same exported configuration, so every gateway
    /// loads the same extension from the same relative path inside its own install.
    /// </para>
    /// </summary>
    public sealed class GatewayExtensionHost : IDisposable
    {
        /// <summary>Posted actions held at once before new ones are dropped. A game thread that outruns the gateway loop this badly has a bug, and an unbounded queue would turn it into an out-of-memory.</summary>
        private const int MaxQueuedWork = 8192;
        /// <summary>Posted actions run per tick, so a burst is spread over a few ticks instead of stalling one.</summary>
        private const int MaxWorkPerTick = 256;

        private readonly NebulaGateway _gateway;
        private readonly IGatewayExtension _extension;
        private readonly Context _context;
        private readonly string _name;
        private bool _shutdown;

        /// <summary>The extension's type name, for logs and tests.</summary>
        public string ExtensionName => _name;
        /// <summary>Exceptions this extension has thrown since the gateway started, over every callback. Reported as <see cref="GatewayStats.ExtensionErrors"/>.</summary>
        public uint ErrorCount { get; private set; }

        private GatewayExtensionHost(NebulaGateway gateway, IGatewayExtension extension, Type type, NebulaConfig config)
        {
            _gateway = gateway;
            _extension = extension;
            _name = type.FullName ?? type.Name;
            _context = new Context(this, gateway, config);
        }

        /// <summary>
        /// The extension named in <paramref name="config"/>, loaded, constructed and initialized, or null when
        /// no extension is configured. Throws when one is configured and anything about it is wrong: a missing
        /// file, an assembly that is not a Nebula extension, no type or several, a constructor or an
        /// <see cref="IGatewayExtension.Initialize"/> that threw. The caller lets that stop the process.
        /// </summary>
        public static GatewayExtensionHost TryLoad(NebulaGateway gateway, NebulaConfig config)
        {
            if (gateway == null) throw new ArgumentNullException(nameof(gateway));
            string configured = config?.GatewayExtension?.Trim();
            if (string.IsNullOrEmpty(configured)) return null;

            string file = Resolve(configured);
            var assembly = LoadAssembly(file);
            CheckCompatibility(assembly, file);
            var type = FindType(assembly, file, config.GatewayExtensionType?.Trim());
            var instance = Construct(type);
            var host = new GatewayExtensionHost(gateway, instance, type, config);
            gateway.ClientJoined += host._context.RaiseJoined;
            gateway.ClientLeft += host._context.RaiseLeft;
            NebulaLog.Info($"gateway extension {type.FullName} from {file}");
            try { instance.Initialize(host._context); }
            catch (Exception e)
            {
                host.Detach();
                throw new InvalidOperationException($"the gateway extension {type.FullName} failed to initialize: {e.GetBaseException().Message}", e);
            }
            NebulaLog.Info($"gateway extension ready: interest policy {(gateway.InterestPolicy is DefaultInterestPolicy ? "unchanged (Nebula's default)" : gateway.InterestPolicy.GetType().FullName)}" +
                           (host._context.OptionCount > 0 ? $", {host._context.OptionCount} option(s)" : ""));
            return host;
        }

        /// <summary>Drain what other threads posted, then tick the extension. Called on the gateway loop, before the gateway's own tick, so a change posted this tick is in force for this tick's evaluations.</summary>
        public void Tick(double now)
        {
            if (_shutdown) return;
            _context.DrainWork(MaxWorkPerTick);
            try { _extension.Tick(now); }
            catch (Exception e) { Failed("Tick", e); }
        }

        /// <summary>Tell the extension the gateway is going away, once. Safe to call twice; <see cref="Dispose"/> does it.</summary>
        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            Detach();
            try { _extension.Shutdown(); }
            catch (Exception e) { Failed("Shutdown", e); }
        }

        public void Dispose() => Shutdown();

        private void Detach()
        {
            _gateway.ClientJoined -= _context.RaiseJoined;
            _gateway.ClientLeft -= _context.RaiseLeft;
        }

        /// <summary>
        /// One extension failure: counted, published in the heartbeat, and logged — the first few in full, then
        /// one in a hundred, because an extension throwing on every entity of every evaluation would otherwise
        /// cost more in log writing than in the throw.
        /// </summary>
        private void Failed(string what, Exception e)
        {
            ErrorCount++;
            _gateway.ExtensionErrors = ErrorCount;
            if (ErrorCount <= 5 || ErrorCount % 100 == 0)
                NebulaLog.Error($"gateway extension {_name}.{what} threw ({ErrorCount} so far): {e}");
        }

        // ------------------------------------------------------------------------------------------- loading

        /// <summary>
        /// Where an extension may sit: an absolute path as given, or a relative one next to the gateway
        /// executable (where <c>nebula build</c> puts it and the deploy tarball carries it), beside the service
        /// manifest, or under the working directory. Nothing is scanned — each candidate is one exact file.
        /// </summary>
        private static string Resolve(string configured)
        {
            var candidates = new List<string>();
            if (Path.IsPathRooted(configured)) candidates.Add(Path.GetFullPath(configured));
            else
            {
                candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)));
                string manifest = ServiceManifest.PathOnDisk;
                if (!string.IsNullOrEmpty(manifest))
                {
                    string beside = Path.GetDirectoryName(manifest);
                    if (!string.IsNullOrEmpty(beside)) candidates.Add(Path.GetFullPath(Path.Combine(beside, configured)));
                }
                candidates.Add(Path.GetFullPath(configured));
            }
            foreach (string candidate in candidates) if (File.Exists(candidate)) return candidate;
            throw new FileNotFoundException(
                $"GatewayExtension '{configured}' was not found. Looked for: {string.Join(", ", candidates.Distinct())}. " +
                "Put the extension assembly next to the gateway executable (it ships and deploys with the build folder) " +
                "and give GatewayExtension its file name, or an absolute path.", configured);
        }

        private static Assembly LoadAssembly(string file)
        {
            try { return new ExtensionLoadContext(file).LoadFromAssemblyPath(file); }
            catch (Exception e)
            {
                throw new InvalidOperationException($"the gateway extension {file} could not be loaded: {e.GetBaseException().Message}", e);
            }
        }

        /// <summary>
        /// The extension must have been built against this gateway's own <c>Nebula.Services</c>, or the types it
        /// implements are not the types the gateway calls. A newer reference than the gateway carries is a
        /// mismatch we can see from here; an older one is allowed, because the contract is additive.
        /// </summary>
        private static void CheckCompatibility(Assembly assembly, string file)
        {
            var self = typeof(GatewayExtensionHost).Assembly.GetName();
            var referenced = assembly.GetReferencedAssemblies().FirstOrDefault(a => string.Equals(a.Name, self.Name, StringComparison.OrdinalIgnoreCase));
            if (referenced == null)
                throw new InvalidOperationException($"the gateway extension {file} does not reference {self.Name}.dll: a gateway extension is a class library that references the gateway's {self.Name}.dll and implements Nebula.IGatewayExtension");
            if (referenced.Version != null && self.Version != null && referenced.Version > self.Version)
                throw new InvalidOperationException($"the gateway extension {file} was built against {self.Name} {referenced.Version} but this gateway carries {self.Version}: rebuild the extension against this gateway's {self.Name}.dll");
        }

        private static Type FindType(Assembly assembly, string file, string configuredType)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                throw new InvalidOperationException($"the gateway extension {file} could not be inspected: {string.Join("; ", e.LoaderExceptions.Where(x => x != null).Select(x => x.Message).Distinct())}", e);
            }
            bool IsExtension(Type t) => typeof(IGatewayExtension).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface;
            if (!string.IsNullOrEmpty(configuredType))
            {
                var named = assembly.GetType(configuredType, false, false);
                if (named == null)
                    throw new InvalidOperationException($"GatewayExtensionType '{configuredType}' is not a type in {file}. It holds: {Describe(types.Where(IsExtension))}");
                if (!IsExtension(named))
                    throw new InvalidOperationException($"GatewayExtensionType '{configuredType}' in {file} does not implement Nebula.IGatewayExtension. " +
                        "If it looks like it does, the extension has its own copy of Nebula.Services.dll beside it: reference the gateway's assembly instead of copying it.");
                return named;
            }
            var found = types.Where(IsExtension).ToList();
            if (found.Count == 1) return found[0];
            if (found.Count == 0)
                throw new InvalidOperationException($"the gateway extension {file} holds no public class implementing Nebula.IGatewayExtension. " +
                    "If it has one, the extension has its own copy of Nebula.Services.dll beside it: reference the gateway's assembly instead of copying it.");
            throw new InvalidOperationException($"the gateway extension {file} holds {found.Count} classes implementing Nebula.IGatewayExtension ({Describe(found)}): name the one to load in GatewayExtensionType (-nebula-gateway-extension-type)");
        }

        private static string Describe(IEnumerable<Type> types)
        {
            var names = types.Select(t => t.FullName).ToList();
            return names.Count == 0 ? "no extension types" : string.Join(", ", names);
        }

        private static IGatewayExtension Construct(Type type)
        {
            try { return (IGatewayExtension)Activator.CreateInstance(type); }
            catch (Exception e)
            {
                throw new InvalidOperationException($"the gateway extension {type.FullName} could not be constructed (it needs a public parameterless constructor): {e.GetBaseException().Message}", e);
            }
        }

        /// <summary>
        /// The extension's own load context: it may bring its own dependencies, but anything the gateway
        /// process already has — Nebula.Services above all — resolves to the copy already loaded, so the
        /// interfaces on both sides are one type.
        /// </summary>
        private sealed class ExtensionLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;
            public ExtensionLoadContext(string file) : base("nebula-gateway-extension", isCollectible: false) => _resolver = new AssemblyDependencyResolver(file);

            protected override Assembly Load(AssemblyName name)
            {
                foreach (var loaded in Default.Assemblies)
                    if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)) return null;
                string path = _resolver.ResolveAssemblyToPath(name);
                return path != null ? LoadFromAssemblyPath(path) : null;
            }

            protected override IntPtr LoadUnmanagedDll(string name)
            {
                string path = _resolver.ResolveUnmanagedDllToPath(name);
                return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
            }
        }

        // ------------------------------------------------------------------------------------------- context

        /// <summary>The gateway surface as the extension sees it. Every call out to the extension goes through <see cref="GatewayExtensionHost.Failed"/>.</summary>
        private sealed class Context : IGatewayExtensionContext
        {
            private readonly GatewayExtensionHost _host;
            private readonly NebulaGateway _gateway;
            private readonly NebulaConfig _config;
            private readonly Dictionary<string, string> _options;
            private readonly ConcurrentQueue<Action> _work = new ConcurrentQueue<Action>();
            private int _queued;

            public Context(GatewayExtensionHost host, NebulaGateway gateway, NebulaConfig config)
            {
                _host = host; _gateway = gateway; _config = config;
                _options = ParseOptions(config.GatewayExtensionOptions);
            }

            public int OptionCount => _options.Count;

            public string GatewayId => _gateway.GatewayId;
            public NebulaConfig Config => _config;

            public string Option(string key, string fallback = null)
            {
                if (string.IsNullOrEmpty(key)) return fallback;
                string fromCommandLine = CommandLine.Get("nebula-ext-" + key);
                if (fromCommandLine != null) return fromCommandLine;
                return _options.TryGetValue(key, out string value) ? value : fallback;
            }

            public void SetInterestPolicy(IInterestPolicy policy) =>
                _gateway.InterestPolicy = policy == null ? null : Guarded.Wrap(policy, _host);

            public event Action<NebulaGateway.GatewayClientInfo> ClientJoined;
            public event Action<NebulaGateway.GatewayClientInfo> ClientLeft;

            public void RaiseJoined(NebulaGateway.GatewayClientInfo client) => Raise(ClientJoined, client, nameof(ClientJoined));
            public void RaiseLeft(NebulaGateway.GatewayClientInfo client) => Raise(ClientLeft, client, nameof(ClientLeft));

            private void Raise(Action<NebulaGateway.GatewayClientInfo> handler, in NebulaGateway.GatewayClientInfo client, string what)
            {
                if (handler == null) return;
                try { handler(client); }
                catch (Exception e) { _host.Failed(what, e); }
            }

            public void SetClientTag(ulong clientId, byte team) => _gateway.SetClientTag(clientId, team);
            public byte GetClientTag(ulong clientId) => _gateway.GetClientTag(clientId);
            public void SetClientTags(ulong clientId, ulong tags) => _gateway.SetClientTags(clientId, tags);
            public ulong GetClientTags(ulong clientId) => _gateway.GetClientTags(clientId);
            public void SetClientFocusMode(ulong clientId, FocusMode mode) => _gateway.SetClientFocusMode(clientId, mode);
            public FocusMode GetClientFocusMode(ulong clientId) => _gateway.GetClientFocusMode(clientId);
            public void MarkInterestDirty(ulong clientId) => _gateway.MarkInterestDirty(clientId);
            public void MarkAllInterestDirty() => _gateway.MarkAllInterestDirty();
            public void RevalidateInterest(ulong clientId) => _gateway.RevalidateInterest(clientId);
            public void RevalidateAllInterest() => _gateway.RevalidateAllInterest();

            public void Post(Action work)
            {
                if (work == null || _host._shutdown) return;
                if (System.Threading.Interlocked.Increment(ref _queued) > MaxQueuedWork)
                {
                    System.Threading.Interlocked.Decrement(ref _queued);
                    _host.Failed("Post", new InvalidOperationException($"more than {MaxQueuedWork} actions are waiting for the gateway loop; the work is being dropped"));
                    return;
                }
                _work.Enqueue(work);
            }

            /// <summary>Run what other threads posted, up to <paramref name="max"/> of it, on the gateway loop.</summary>
            public void DrainWork(int max)
            {
                for (int i = 0; i < max && _work.TryDequeue(out var work); i++)
                {
                    System.Threading.Interlocked.Decrement(ref _queued);
                    try { work(); }
                    catch (Exception e) { _host.Failed("Post", e); }
                }
            }

            public void Log(string message) => NebulaLog.Info($"[{_host._name}] {message}");
            public void LogWarning(string message) => NebulaLog.Warn($"[{_host._name}] {message}");
            public void LogError(string message) => NebulaLog.Error($"[{_host._name}] {message}");

            /// <summary><c>key=value;key=value</c>. Whitespace around either side is trimmed; an empty entry is ignored; a repeated key keeps the last.</summary>
            private static Dictionary<string, string> ParseOptions(string text)
            {
                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(text)) return options;
                foreach (string pair in text.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    string key = (eq < 0 ? pair : pair.Substring(0, eq)).Trim();
                    if (key.Length == 0) continue;
                    options[key] = eq < 0 ? "" : pair.Substring(eq + 1).Trim();
                }
                return options;
            }
        }

        // ------------------------------------------------------------------------------------------- guard

        /// <summary>
        /// The game's policy with a boundary around it. <c>Authorize</c> is a security filter, so an exception
        /// out of it is a refusal: a policy that fails must hide the world, not reveal it. <c>Collect</c> is not
        /// a boundary — a throw there costs the client the foci the policy had not added yet, which shows up as
        /// a client that sees less, and that is the safe way round as well.
        /// </summary>
        private class Guarded : IInterestPolicy
        {
            protected readonly IInterestPolicy Inner;
            protected readonly GatewayExtensionHost Host;

            protected Guarded(IInterestPolicy inner, GatewayExtensionHost host) { Inner = inner; Host = host; }

            /// <summary>Wraps so that a policy that also decides focus hints keeps doing so, and one that does not is not asked.</summary>
            public static IInterestPolicy Wrap(IInterestPolicy inner, GatewayExtensionHost host) =>
                inner is IFocusHintPolicy hints ? new GuardedWithHints(inner, hints, host) : new Guarded(inner, host);

            public void Collect(in InterestClient client, InterestQuery query)
            {
                try { Inner.Collect(client, query); }
                catch (Exception e) { Host.Failed("IInterestPolicy.Collect", e); }
            }

            public bool Authorize(in InterestClient client, in InterestEntity entity)
            {
                try { return Inner.Authorize(client, entity); }
                catch (Exception e) { Host.Failed("IInterestPolicy.Authorize", e); return false; }
            }
        }

        private sealed class GuardedWithHints : Guarded, IFocusHintPolicy
        {
            private readonly IFocusHintPolicy _hints;
            public GuardedWithHints(IInterestPolicy inner, IFocusHintPolicy hints, GatewayExtensionHost host) : base(inner, host) => _hints = hints;

            /// <summary>A throw here rejects the hint: the client keeps the view its pawn earns it and nothing more.</summary>
            public FocusHintDecision AuthorizeFocusHint(in InterestClient client, in FocusHintDecision request)
            {
                try { return _hints.AuthorizeFocusHint(client, request); }
                catch (Exception e) { Host.Failed("IFocusHintPolicy.AuthorizeFocusHint", e); return FocusHintDecision.Reject(); }
            }
        }
    }
}
