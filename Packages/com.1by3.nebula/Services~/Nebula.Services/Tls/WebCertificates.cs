using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nebula.Tls
{
    /// <summary>
    /// Keeps a certificate for the gateway's public address from an ACME certificate authority, so the web port can
    /// serve HTTPS: a page loaded over HTTPS (Unity Play, itch.io) may only send its WebRTC offer to an HTTPS address.
    /// Answers HTTP-01 challenges on port 80, renews at half the certificate's lifetime (about three days for
    /// Let's Encrypt's six-day IP address certificates), retries failures with backoff, and keeps the account key and
    /// the certificate in a folder so a restart does not ask the CA again.
    /// </summary>
    internal sealed class WebCertificates : IDisposable
    {
        private const string ChallengePrefix = "/.well-known/acme-challenge/";

        private readonly string _identifier, _directoryUrl, _store, _email, _profile;
        private readonly int _challengePort;
        private readonly ConcurrentDictionary<string, string> _challenges = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private WebApplication _challengeServer;
        private volatile SslStreamCertificateContext _current;
        private DateTime _renewAt, _notAfter;
        private Task _loop;
        private int _disposed;

        /// <param name="identifier">The address clients connect to: an IP address or a DNS name.</param>
        /// <param name="profile">ACME profile to request; see <see cref="DefaultProfile"/>.</param>
        /// <param name="challengePort">Where HTTP-01 is answered. A CA only ever checks port 80; tests use another.</param>
        public WebCertificates(string identifier, string directoryUrl, string store, string email, string profile, int challengePort = 80)
        {
            _identifier = identifier;
            _directoryUrl = directoryUrl;
            _store = Path.GetFullPath(store);
            _email = email;
            _profile = profile;
            _challengePort = challengePort;
        }

        /// <summary>The certificate to present, or null until the first one arrives.</summary>
        public SslStreamCertificateContext Current => _current;

        /// <summary>Let's Encrypt issues IP address certificates only under its six-day "shortlived" profile; a DNS name takes the CA's default.</summary>
        public static string DefaultProfile(string identifier) => IPAddress.TryParse(identifier, out _) ? "shortlived" : null;

        private string CertificatePath => Path.Combine(_store, "certificate.pem");
        private string CertificateKeyPath => Path.Combine(_store, "certificate-key.pem");
        private string IdentifierPath => Path.Combine(_store, "identifier.txt");
        private string AccountKeyPath => Path.Combine(_store, "account-key.pem");

        /// <summary>Load a stored certificate, open the challenge port, and keep a certificate current from then on.</summary>
        public void Start()
        {
            Directory.CreateDirectory(_store);
            try { LoadStored(); }
            catch (Exception e) { NebulaLog.Warn($"web TLS: ignoring the stored certificate in {_store}: {e.Message}"); }
            StartChallengeServer();
            _loop = Task.Run(() => KeepCurrentAsync(_stop.Token));
        }

        private void LoadStored()
        {
            if (!File.Exists(CertificatePath) || !File.Exists(CertificateKeyPath) || !File.Exists(IdentifierPath)) return;
            if (File.ReadAllText(IdentifierPath).Trim() != _identifier) return; // issued for an address this gateway no longer has
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(CertificateKeyPath));
            Use(File.ReadAllText(CertificatePath), key);
            if (DateTime.UtcNow >= _notAfter) { _current = null; return; }
            NebulaLog.Info($"web TLS: using the stored certificate for {_identifier}, valid until {_notAfter:u}");
        }

        private void Use(string chainPem, ECDsa key)
        {
            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPem(chainPem);
            if (certificates.Count == 0) throw new AcmeException("the certificate chain holds no certificate");
            var leaf = certificates[0].CopyWithPrivateKey(key);
            // Windows' TLS stack cannot use an in-memory key; a PKCS#12 round trip stores it where it can.
            if (OperatingSystem.IsWindows()) leaf = X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pkcs12), null);
            var intermediates = new X509Certificate2Collection();
            for (int i = 1; i < certificates.Count; i++) intermediates.Add(certificates[i]);
            _notAfter = leaf.NotAfter.ToUniversalTime();
            _renewAt = leaf.NotBefore.ToUniversalTime() + (leaf.NotAfter - leaf.NotBefore) / 2;
            _current = CreateContext(leaf, intermediates);
        }

        private static SslStreamCertificateContext CreateContext(X509Certificate2 leaf, X509Certificate2Collection intermediates)
        {
            try { return SslStreamCertificateContext.Create(leaf, intermediates, offline: true); }
            catch (CryptographicException) when (OperatingSystem.IsWindows())
            {
                // Windows' chain engine can refuse an offline build outright ("An unknown chain building error
                // occurred") when the root is not in the machine's store, as with a private ACME CA. An online build
                // accepts the same chain; it only reaches the network for a certificate that names an AIA location.
                return SslStreamCertificateContext.Create(leaf, intermediates, offline: false);
            }
        }

        private async Task KeepCurrentAsync(CancellationToken ct)
        {
            int failures = 0;
            while (!ct.IsCancellationRequested)
            {
                if (_current == null || DateTime.UtcNow >= _renewAt)
                {
                    try
                    {
                        await IssueAsync(ct);
                        failures = 0;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception e)
                    {
                        failures++;
                        NebulaLog.Warn($"web TLS: no certificate for {_identifier} from {_directoryUrl} (attempt {failures}): {e.GetBaseException().Message}");
                    }
                }
                TimeSpan wait = failures > 0 ? TimeSpan.FromMinutes(1 << Math.Min(failures - 1, 6)) : _renewAt - DateTime.UtcNow;
                if (wait > TimeSpan.FromHours(1)) wait = TimeSpan.FromHours(1);
                if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task IssueAsync(CancellationToken ct)
        {
            using var accountKey = LoadOrCreateAccountKey();
            using var client = new AcmeClient(_directoryUrl, accountKey);
            NebulaLog.Info($"web TLS: requesting a certificate for {_identifier} from {_directoryUrl}{(string.IsNullOrEmpty(_profile) ? "" : $" (profile {_profile})")}");
            var (chain, key) = await client.IssueAsync(_identifier, _profile, _email,
                (token, answer) => _challenges[token] = answer, token => _challenges.TryRemove(token, out _), ct);
            Use(chain, key);
            File.WriteAllText(CertificatePath, chain);
            WritePrivate(CertificateKeyPath, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(IdentifierPath, _identifier);
            NebulaLog.Info($"web TLS: certificate for {_identifier} valid until {_notAfter:u}; renewing after {_renewAt:u}");
        }

        private ECDsa LoadOrCreateAccountKey()
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(AccountKeyPath)) key.ImportFromPem(File.ReadAllText(AccountKeyPath));
            else WritePrivate(AccountKeyPath, key.ExportPkcs8PrivateKeyPem());
            return key;
        }

        private static void WritePrivate(string path, string text)
        {
            File.WriteAllText(path, text);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private void StartChallengeServer()
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(_challengePort));
            var app = builder.Build();
            app.Run(async context =>
            {
                string path = context.Request.Path.Value ?? "";
                if (path.StartsWith(ChallengePrefix, StringComparison.Ordinal) && _challenges.TryGetValue(path.Substring(ChallengePrefix.Length), out var answer))
                {
                    context.Response.ContentType = "application/octet-stream";
                    await context.Response.WriteAsync(answer);
                    return;
                }
                context.Response.StatusCode = 404;
            });
            try
            {
                app.StartAsync().GetAwaiter().GetResult();
                _challengeServer = app;
            }
            catch (Exception e)
            {
                NebulaLog.Warn($"web TLS: cannot answer certificate challenges on tcp/{_challengePort}, so no certificate can be issued: {e.GetBaseException().Message}");
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            if (_challengeServer != null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { _challengeServer.StopAsync(timeout.Token).GetAwaiter().GetResult(); }
                finally { _challengeServer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            _stop.Dispose();
        }
    }
}
