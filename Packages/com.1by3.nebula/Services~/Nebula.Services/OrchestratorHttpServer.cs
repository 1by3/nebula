using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nebula
{
    // Same dashboard contract as the Unity host, served by Kestrel without Windows URL ACLs.
    public sealed class OrchestratorHttpServer : IDisposable
    {
        public sealed class Request
        {
            public string Method, Path, Body;
            internal readonly TaskCompletionSource<Response> Completion = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public struct Response
        {
            public int Status;
            public string ContentType, Body;
            public static Response Json(int status, string body) => new Response { Status = status, ContentType = "application/json", Body = body };
            public static Response Error(int status, string message) => Json(status, $"{{\"ok\":false,\"error\":{JsonWriter.Quote(message)}}}");
        }
        public const long MaxBodyBytes = 8L * 1024 * 1024;
        public string Url { get; }
        private readonly ConcurrentDictionary<string, string> pages = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Func<Request, Response>> direct = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<Request> commands = new();
        private readonly string artifactDir;
        private readonly WebApplication app;
        private volatile string state = "{}";
        private volatile bool stopping;
        public OrchestratorHttpServer(string bind, ushort port, string page, string artifactDir = null)
        {
            this.artifactDir = string.IsNullOrEmpty(artifactDir) ? null : System.IO.Path.GetFullPath(artifactDir);
            if (string.IsNullOrEmpty(page)) throw new InvalidOperationException("Embedded dashboard page is missing");
            AddPage("/", page); AddPage("/index.html", page);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = MaxBodyBytes;
                if (string.IsNullOrEmpty(bind) || bind == "localhost") options.ListenLocalhost(port);
                else if (bind == "+" || bind == "*") options.ListenAnyIP(port);
                else options.Listen(IPAddress.Parse(bind), port);
            });
            app = builder.Build();
            app.Run(Handle);
            Url = $"http://localhost:{port}/";
        }
        public void Start() => app.StartAsync().GetAwaiter().GetResult();
        public void PublishState(string json) => state = json;
        public void AddPage(string path, string html) { if (!string.IsNullOrEmpty(path) && html != null) pages[path.Length > 1 ? path.TrimEnd('/') : path] = html; }
        public void MapDirect(string method, string path, Func<Request, Response> handler)
        {
            string key = method + " " + path.TrimEnd('/');
            if (handler == null) direct.TryRemove(key, out _); else direct[key] = handler;
        }
        public void Pump(Func<Request, Response> handler)
        {
            while (commands.TryDequeue(out var req))
            {
                if (req.Completion.Task.IsCompleted) continue;
                try { req.Completion.TrySetResult(handler(req)); }
                catch (Exception e) { req.Completion.TrySetResult(Response.Error(500, e.Message)); }
            }
        }
        private async Task Handle(HttpContext context)
        {
            var req = context.Request;
            string path = req.Path.Value ?? "/";
            string trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
            Response response;
            try
            {
                if (stopping) response = Response.Error(503, "orchestrator shutting down");
                else if (req.ContentLength > MaxBodyBytes) response = Response.Error(413, "request body too large");
                else if (req.Method == "GET" && pages.TryGetValue(trimmed, out var page)) response = new Response { Status = 200, ContentType = "text/html; charset=utf-8", Body = page };
                else if (req.Method == "GET" && trimmed == "/api/state") response = Response.Json(200, state);
                else if (req.Method == "GET" && path.StartsWith("/build/", StringComparison.Ordinal))
                {
                    string name = path.Substring("/build/".Length);
                    if (artifactDir == null || string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..")) response = Response.Error(404, "not found");
                    else
                    {
                        string file = System.IO.Path.Combine(artifactDir, name);
                        if (!File.Exists(file)) response = Response.Error(404, "not found");
                        else
                        {
                            context.Response.ContentType = "application/octet-stream";
                            context.Response.Headers.CacheControl = "no-store";
                            await context.Response.SendFileAsync(file, context.RequestAborted);
                            return;
                        }
                    }
                }
                else
                {
                    using var reader = new StreamReader(req.Body, Encoding.UTF8);
                    string body = await reader.ReadToEndAsync(context.RequestAborted);
                    var request = new Request { Method = req.Method, Path = path, Body = body };
                    if (direct.TryGetValue(req.Method + " " + trimmed, out var handler)) response = handler(request);
                    else if (path.StartsWith("/api/", StringComparison.Ordinal))
                    {
                        commands.Enqueue(request);
                        try { response = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3), context.RequestAborted); }
                        catch (TimeoutException) { response = Response.Error(503, "orchestrator did not answer in time"); request.Completion.TrySetResult(response); }
                    }
                    else response = Response.Error(404, "not found");
                }
            }
            catch (BadHttpRequestException e) { response = Response.Error(e.StatusCode, e.Message); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
            catch (Exception e) { response = Response.Error(500, e.Message); }
            context.Response.StatusCode = response.Status;
            context.Response.ContentType = response.ContentType;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(response.Body ?? "", context.RequestAborted);
        }
        public void Dispose()
        {
            stopping = true;
            while (commands.TryDequeue(out var request)) request.Completion.TrySetResult(Response.Error(503, "orchestrator shutting down"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { app.StopAsync(timeout.Token).GetAwaiter().GetResult(); }
            finally { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        public static bool TryGetInt(string body, string key, out int value)
        {
            value = 0; if (string.IsNullOrEmpty(body)) return false;
            var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        public static string GetString(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
