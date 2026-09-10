using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Nebula
{
    /// <summary>
    /// The orchestrator's tiny web server: serves the dashboard page and a JSON state document, and forwards commands
    /// (POST) to the main thread. Runs on Mono's managed <see cref="HttpListener"/>, so it needs no URL ACLs and no
    /// external dependencies. Reads are answered from the listener thread with the last published state; writes are
    /// queued and executed by <see cref="Pump"/> from the orchestrator's Update, and the HTTP response waits for that.
    /// </summary>
    public sealed class OrchestratorHttpServer : IDisposable
    {
        public sealed class Request
        {
            public string Method;
            public string Path;
            public string Body;
            internal Response Result;
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        public struct Response
        {
            public int Status;
            public string ContentType;
            public string Body;

            public static Response Json(int status, string body) => new Response { Status = status, ContentType = "application/json", Body = body };
            public static Response Error(int status, string message) => Json(status, $"{{\"ok\":false,\"error\":{JsonWriter.Quote(message)}}}");
        }

        private const string FallbackPage = "<!doctype html><title>Nebula</title><body style='font-family:sans-serif;padding:2em'><h1>Nebula Dashboard</h1><p>The dashboard page (Resources/NebulaDashboard.html) is missing from this build. The API still works: GET <a href='/api/state'>/api/state</a>.</p></body>";

        public string Url { get; }

        private readonly HttpListener _listener = new HttpListener();
        private readonly string _page;
        private readonly ConcurrentQueue<Request> _commands = new ConcurrentQueue<Request>();
        private readonly string _artifactDir;
        private volatile string _state = "{}";
        private volatile bool _running;

        /// <param name="bind">"localhost" (default), "+" / "*" for every interface, or one address.</param>
        /// <param name="artifactDir">Directory served read-only under <c>/build/</c> (the Linux server tarball worker VMs fetch); null disables.</param>
        public OrchestratorHttpServer(string bind, ushort port, string page, string artifactDir = null)
        {
            _page = string.IsNullOrEmpty(page) ? FallbackPage : page;
            _artifactDir = string.IsNullOrEmpty(artifactDir) ? null : Path.GetFullPath(artifactDir);
            if (string.IsNullOrEmpty(bind) || bind == "localhost")
            {
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            }
            else if (bind == "+" || bind == "*")
            {
                _listener.Prefixes.Add($"http://*:{port}/");
            }
            else
            {
                _listener.Prefixes.Add($"http://{bind}:{port}/");
            }
            Url = $"http://localhost:{port}/";
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            _listener.BeginGetContext(OnContext, null);
        }

        /// <summary>Replace the state document served by GET /api/state. Call from the main thread whenever something changed.</summary>
        public void PublishState(string json) => _state = json;

        /// <summary>Main thread: execute queued commands. Each waiting HTTP response is released with the handler's result.</summary>
        public void Pump(Func<Request, Response> handler)
        {
            while (_commands.TryDequeue(out var req))
            {
                try { req.Result = handler(req); }
                catch (Exception e) { req.Result = Response.Error(500, e.Message); }
                req.Done.Set();
            }
        }

        private void OnContext(IAsyncResult ar)
        {
            if (!_running) return;
            HttpListenerContext ctx = null;
            try { ctx = _listener.EndGetContext(ar); }
            catch (Exception) { /* listener stopped */ }
            try { if (_running) _listener.BeginGetContext(OnContext, null); } catch { }
            if (ctx == null) return;
            ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                string path = req.Url.AbsolutePath;
                Response resp;
                if (req.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
                {
                    resp = new Response { Status = 200, ContentType = "text/html; charset=utf-8", Body = _page };
                }
                else if (req.HttpMethod == "GET" && path.TrimEnd('/') == "/api/state")
                {
                    resp = Response.Json(200, _state);
                }
                else if (req.HttpMethod == "GET" && path.StartsWith("/build/", StringComparison.Ordinal))
                {
                    ServeArtifact(ctx.Response, path.Substring("/build/".Length));
                    return;
                }
                else if (path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    string body;
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8)) body = reader.ReadToEnd();
                    var cmd = new Request { Method = req.HttpMethod, Path = path, Body = body };
                    _commands.Enqueue(cmd);
                    resp = cmd.Done.Wait(TimeSpan.FromSeconds(3)) ? cmd.Result : Response.Error(503, "orchestrator did not answer in time");
                }
                else
                {
                    resp = Response.Error(404, "not found");
                }
                Write(ctx.Response, resp);
            }
            catch (Exception e)
            {
                try { Write(ctx.Response, Response.Error(500, e.Message)); } catch { }
            }
        }

        /// <summary>Stream one file from the artifact directory. Only plain file names, no sub-paths.</summary>
        private void ServeArtifact(HttpListenerResponse r, string name)
        {
            if (_artifactDir == null || string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains(".."))
            {
                Write(r, Response.Error(404, "not found"));
                return;
            }
            string file = Path.Combine(_artifactDir, name);
            if (!File.Exists(file))
            {
                Write(r, Response.Error(404, $"no artifact '{name}'"));
                return;
            }
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                r.StatusCode = 200;
                r.ContentType = "application/octet-stream";
                r.ContentLength64 = fs.Length;
                r.Headers["Cache-Control"] = "no-store";
                using (var s = r.OutputStream) fs.CopyTo(s, 1 << 16);
            }
            r.Close();
        }

        private static void Write(HttpListenerResponse r, Response resp)
        {
            var bytes = Encoding.UTF8.GetBytes(resp.Body ?? "");
            r.StatusCode = resp.Status;
            r.ContentType = resp.ContentType;
            r.ContentLength64 = bytes.Length;
            r.Headers["Cache-Control"] = "no-store";
            using (var s = r.OutputStream) s.Write(bytes, 0, bytes.Length);
            r.Close();
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            while (_commands.TryDequeue(out var req))
            {
                req.Result = Response.Error(503, "orchestrator shutting down");
                req.Done.Set();
            }
        }

        // ---------------------------------------------------------------------------------------- minimal body parsing

        /// <summary>Reads an integer property from a flat JSON object body (the dashboard only ever sends {"key": value}).</summary>
        public static bool TryGetInt(string body, string key, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(body)) return false;
            var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Reads a string property from a flat JSON object body; "" when absent.</summary>
        public static string GetString(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
    }

    /// <summary>Just enough JSON writing for the dashboard state document; avoids a JSON library dependency in the runtime.</summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder _sb;
        private bool _needComma;

        public JsonWriter(StringBuilder sb) { _sb = sb; }

        private void Sep()
        {
            if (_needComma) _sb.Append(',');
            _needComma = true;
        }

        public void BeginObject() { Sep(); _sb.Append('{'); _needComma = false; }
        public void EndObject() { _sb.Append('}'); _needComma = true; }
        public void BeginArray() { Sep(); _sb.Append('['); _needComma = false; }
        public void EndArray() { _sb.Append(']'); _needComma = true; }

        public void Key(string name) { Sep(); _sb.Append(Quote(name)).Append(':'); _needComma = false; }

        public void Value(string s) { Sep(); _sb.Append(Quote(s)); }
        public void Value(bool b) { Sep(); _sb.Append(b ? "true" : "false"); }
        public void Value(long n) { Sep(); _sb.Append(n.ToString(CultureInfo.InvariantCulture)); }
        public void Value(ulong n) { Sep(); _sb.Append(n.ToString(CultureInfo.InvariantCulture)); }
        public void Value(double d)
        {
            Sep();
            if (double.IsNaN(d) || double.IsInfinity(d)) _sb.Append("null");
            else _sb.Append(Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture));
        }

        public void Prop(string name, string s) { Key(name); Value(s); }
        public void Prop(string name, bool b) { Key(name); Value(b); }
        public void Prop(string name, int n) { Key(name); Value((long)n); }
        public void Prop(string name, uint n) { Key(name); Value((ulong)n); }
        public void Prop(string name, long n) { Key(name); Value(n); }
        public void Prop(string name, ulong n) { Key(name); Value(n); }
        public void Prop(string name, float f) { Key(name); Value((double)f); }
        public void Prop(string name, double d) { Key(name); Value(d); }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
