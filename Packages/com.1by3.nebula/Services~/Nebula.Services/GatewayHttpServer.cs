using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Nebula.Tls;
using Nebula.WebRtc;

namespace Nebula
{
    /// <summary>
    /// The gateway's HTTP server for web builds. <c>POST /nebula/rtc</c> takes a browser's SDP offer and returns the
    /// gateway's answer (see <see cref="WebRtcServerTransport"/>); every other GET serves the web build from
    /// <c>webRoot</c> when there is one, with the <c>Content-Encoding</c> Unity's compressed build files need.
    /// </summary>
    internal sealed class GatewayHttpServer : IDisposable
    {
        public const string SignalingPath = "/nebula/rtc";
        private const long MaxOfferBytes = 64 * 1024;
        private readonly WebApplication _app;
        private readonly WebRtcServerTransport _rtc;
        private readonly string _webRoot;
        private readonly WebCertificates _certificates;

        /// <param name="certificates">Serve HTTPS with these (owned and disposed by this server); null serves plain HTTP.</param>
        public GatewayHttpServer(ushort port, WebRtcServerTransport rtc, string webRoot, WebCertificates certificates = null)
        {
            _rtc = rtc;
            _certificates = certificates;
            _webRoot = string.IsNullOrEmpty(webRoot) ? null : Path.GetFullPath(webRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = MaxOfferBytes;
                if (certificates == null)
                {
                    options.ListenAnyIP(port);
                    return;
                }
                options.ListenAnyIP(port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    // Chosen per connection, so a renewed certificate is used from the next connection on.
                    listen.UseHttps(new TlsHandshakeCallbackOptions
                    {
                        OnConnection = _ => new ValueTask<SslServerAuthenticationOptions>(new SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = certificates.Current ?? throw new AuthenticationException("the gateway has no certificate yet"),
                        }),
                    });
                });
            });
            _app = builder.Build();
            _app.Run(Handle);
        }

        public string WebRoot => _webRoot;
        public bool Secure => _certificates != null;

        public void Start() => _app.StartAsync().GetAwaiter().GetResult();

        private async Task Handle(HttpContext context)
        {
            var request = context.Request;
            var response = context.Response;
            string path = request.Path.Value ?? "/";
            if (path == SignalingPath)
            {
                // A web build hosted on another origin still signals here.
                response.Headers["Access-Control-Allow-Origin"] = "*";
                if (request.Method == "OPTIONS")
                {
                    response.Headers["Access-Control-Allow-Methods"] = "POST";
                    response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                    response.StatusCode = 204;
                    return;
                }
                if (request.Method != "POST")
                {
                    response.StatusCode = 405;
                    return;
                }
                string offer;
                using (var reader = new StreamReader(request.Body)) offer = await reader.ReadToEndAsync(context.RequestAborted);
                response.Headers.CacheControl = "no-store";
                try
                {
                    string answer = _rtc.Accept(offer, context.Connection.LocalIpAddress);
                    response.ContentType = "application/sdp";
                    await response.WriteAsync(answer, context.RequestAborted);
                }
                catch (FormatException e) { response.StatusCode = 400; await response.WriteAsync(e.Message, context.RequestAborted); }
                catch (InvalidOperationException e) { response.StatusCode = 503; await response.WriteAsync(e.Message, context.RequestAborted); }
                return;
            }
            if ((request.Method == "GET" || request.Method == "HEAD") && _webRoot != null)
            {
                await ServeFile(context, path);
                return;
            }
            response.StatusCode = 404;
        }

        private async Task ServeFile(HttpContext context, string path)
        {
            var response = context.Response;
            string relative = Uri.UnescapeDataString(path).TrimStart('/');
            if (relative.Length == 0 || relative.EndsWith("/", StringComparison.Ordinal)) relative += "index.html";
            string full = Path.GetFullPath(Path.Combine(_webRoot, relative));
            if (!full.StartsWith(_webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                response.StatusCode = 404;
                return;
            }
            // Unity names compressed build files <name>.<type>.gz / .br and expects the server to say so.
            string name = Path.GetFileName(full);
            if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) { response.Headers.ContentEncoding = "gzip"; name = name.Substring(0, name.Length - 3); }
            else if (name.EndsWith(".br", StringComparison.OrdinalIgnoreCase)) { response.Headers.ContentEncoding = "br"; name = name.Substring(0, name.Length - 3); }
            response.ContentType = ContentType(name);
            response.Headers.CacheControl = "no-cache";
            // Validators let the browser, and Unity's own cache of the build files, revalidate instead of downloading
            // a build of a hundred megabytes again on every page load.
            var info = new FileInfo(full);
            long modifiedSeconds = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            string etag = "\"" + info.Length.ToString("x", CultureInfo.InvariantCulture) + "-" + modifiedSeconds.ToString("x", CultureInfo.InvariantCulture) + "\"";
            response.Headers.ETag = etag;
            response.Headers.LastModified = DateTimeOffset.FromUnixTimeSeconds(modifiedSeconds).ToString("R", CultureInfo.InvariantCulture);
            string ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
            string ifModifiedSince = context.Request.Headers.IfModifiedSince.ToString();
            bool unchanged = ifNoneMatch.Length > 0
                ? ifNoneMatch == etag
                : DateTimeOffset.TryParse(ifModifiedSince, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since) && modifiedSeconds <= since.ToUnixTimeSeconds();
            if (unchanged)
            {
                response.StatusCode = 304;
                return;
            }
            response.ContentLength = info.Length;
            if (HttpMethods.IsHead(context.Request.Method)) return;
            await response.SendFileAsync(full, context.RequestAborted);
        }

        private static string ContentType(string name)
        {
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript";
                case ".wasm": return "application/wasm";
                case ".css": return "text/css";
                case ".json": return "application/json";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".ico": return "image/x-icon";
                case ".svg": return "image/svg+xml";
                default: return "application/octet-stream";
            }
        }

        public void Dispose()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { _app.StopAsync(timeout.Token).GetAwaiter().GetResult(); }
            finally
            {
                _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _certificates?.Dispose();
            }
        }
    }
}
