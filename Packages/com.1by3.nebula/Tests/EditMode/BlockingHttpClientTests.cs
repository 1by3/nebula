using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The control plane's blocking HTTP client (NEB-253): the framing the orchestrator answers with, keep-alive, and
    /// cancellation. The server here, like the client, runs on its own threads and never touches the thread pool.
    /// </summary>
    public class BlockingHttpClientTests
    {
        [Test]
        public void SendsTheTokenAndBodyAndKeepsTheConnection()
        {
            using var server = new TestHttpServer(req => new TestHttpServer.Reply
            {
                Body = $"{req.Method} {req.Target} token={req.Header(ControlPlaneHost.TokenHeader)} type={req.Header("Content-Type")} body={req.Body}",
            });
            using var client = new BlockingHttpClient(TimeSpan.FromSeconds(10));
            var get = client.Send("GET", server.Url + "/api/control-plane?since=3&wait=10", "s3cret", null);
            Assert.That(get.Status, Is.EqualTo(200));
            Assert.That(get.Body, Is.EqualTo("GET /api/control-plane?since=3&wait=10 token=s3cret type= body="));
            var post = client.Send("POST", server.Url + "/api/control-plane", null, "{\"ops\":[\"é\"]}");
            Assert.That(post.IsSuccessStatusCode, Is.True);
            Assert.That(post.Body, Is.EqualTo("POST /api/control-plane token= type=application/json; charset=utf-8 body={\"ops\":[\"é\"]}"));
            Assert.That(server.Accepted, Is.EqualTo(1), "both requests went over one kept-alive connection");
        }

        [Test]
        public void ReadsChunkedAndUnframedBodies()
        {
            string big = new string('x', 40000);
            using var server = new TestHttpServer(req => req.Target == "/chunked"
                ? new TestHttpServer.Reply { Status = 503, Body = big, Chunked = true }
                : new TestHttpServer.Reply { Body = "until close", Unframed = true });
            using var client = new BlockingHttpClient(TimeSpan.FromSeconds(10));
            var chunked = client.Send("GET", server.Url + "/chunked", null, null);
            Assert.That(chunked.Status, Is.EqualTo(503));
            Assert.That(chunked.IsSuccessStatusCode, Is.False);
            Assert.That(chunked.Body, Is.EqualTo(big));
            Assert.That(client.Send("GET", server.Url + "/unframed", null, null).Body, Is.EqualTo("until close"));
            Assert.That(client.Send("GET", server.Url + "/chunked", null, null).Body.Length, Is.EqualTo(big.Length));
            Assert.That(server.Accepted, Is.EqualTo(2), "a body that runs to the end of the connection closes it");
        }

        [Test]
        public void ResendsOnceWhenTheServerClosedAnIdleConnection()
        {
            int requests = 0;
            using var server = new TestHttpServer(req => new TestHttpServer.Reply { Body = Interlocked.Increment(ref requests).ToString(CultureInfo.InvariantCulture) });
            using var client = new BlockingHttpClient(TimeSpan.FromSeconds(10));
            Assert.That(client.Send("POST", server.Url + "/a", null, "{}").Body, Is.EqualTo("1"));
            server.DropConnections();
            Assert.That(client.Send("POST", server.Url + "/a", null, "{}").Body, Is.EqualTo("2"));
            Assert.That(server.Accepted, Is.EqualTo(2));
        }

        [Test]
        public void AbortCutsARequestShortAndRefusesLaterOnes()
        {
            using var release = new ManualResetEventSlim();
            using var server = new TestHttpServer(req => { release.Wait(TimeSpan.FromSeconds(30)); return new TestHttpServer.Reply(); });
            var client = new BlockingHttpClient(TimeSpan.FromSeconds(30));
            Exception failure = null;
            var sw = Stopwatch.StartNew();
            var caller = new Thread(() =>
            {
                try { client.Send("GET", server.Url + "/slow", null, null); }
                catch (Exception e) { failure = e; }
            }) { IsBackground = true };
            try
            {
                caller.Start();
                while (server.Received == 0 && sw.Elapsed.TotalSeconds < 10) Thread.Sleep(5);
                client.Abort();
                Assert.That(caller.Join(TimeSpan.FromSeconds(5)), Is.True, "the blocked request returned");
                Assert.That(failure, Is.InstanceOf<HttpRequestException>());
                Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(10));
                Assert.Throws<HttpRequestException>(() => client.Send("GET", server.Url + "/slow", null, null));
            }
            finally { release.Set(); }
        }

        [Test]
        public void AnUnreachableAddressFailsWithAnHttpRequestException()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using var client = new BlockingHttpClient(TimeSpan.FromSeconds(5));
            var e = Assert.Throws<HttpRequestException>(() => client.Send("GET", $"http://127.0.0.1:{port}/", null, null));
            Assert.That(e.Message, Does.Contain($"127.0.0.1:{port}"));
        }
    }

    /// <summary>
    /// A small HTTP/1.1 server for tests that must not depend on the thread pool: one thread accepts, and each connection
    /// gets its own thread that serves requests in turn until the client or the reply closes it.
    /// </summary>
    internal sealed class TestHttpServer : IDisposable
    {
        public sealed class Request
        {
            public string Method = "", Target = "", Body = "";
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
            public string Path { get { int q = Target.IndexOf('?'); return q < 0 ? Target : Target.Substring(0, q); } }
            public string Query { get { int q = Target.IndexOf('?'); return q < 0 ? "" : Target.Substring(q + 1); } }
        }

        public sealed class Reply
        {
            public int Status = 200;
            public string Body = "";
            /// <summary>Send the body in chunks instead of with a Content-Length.</summary>
            public bool Chunked;
            /// <summary>Send the body with no length at all and close the connection after it.</summary>
            public bool Unframed;
        }

        private readonly TcpListener _listener;
        private readonly Func<Request, Reply> _handler;
        private readonly List<Socket> _open = new List<Socket>();
        private volatile bool _stopped;
        private int _accepted, _received;

        public TestHttpServer(Func<Request, Reply> handler)
        {
            _handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(AcceptLoop) { IsBackground = true, Name = "test-http-accept" }.Start();
        }

        public int Port { get; }
        public string Url => $"http://127.0.0.1:{Port}";
        /// <summary>Connections accepted so far.</summary>
        public int Accepted => Volatile.Read(ref _accepted);
        /// <summary>Requests read so far (counted before the handler runs).</summary>
        public int Received => Volatile.Read(ref _received);

        /// <summary>Close every open connection, as a server does with one that sat idle.</summary>
        public void DropConnections()
        {
            lock (_open)
            {
                foreach (var s in _open) Close(s);
                _open.Clear();
            }
        }

        public void Dispose()
        {
            _stopped = true;
            try { _listener.Stop(); } catch (Exception) { }
            DropConnections();
        }

        private void AcceptLoop()
        {
            while (!_stopped)
            {
                Socket socket;
                try { socket = _listener.AcceptSocket(); }
                catch (Exception) { return; }
                Interlocked.Increment(ref _accepted);
                lock (_open) _open.Add(socket);
                new Thread(() => Serve(socket)) { IsBackground = true, Name = "test-http-connection" }.Start();
            }
        }

        private void Serve(Socket socket)
        {
            try
            {
                using (var stream = new NetworkStream(socket, false))
                {
                    while (!_stopped)
                    {
                        var request = ReadRequest(stream);
                        if (request == null) break;
                        Interlocked.Increment(ref _received);
                        var reply = _handler(request) ?? new Reply { Status = 500 };
                        byte[] body = Encoding.UTF8.GetBytes(reply.Body ?? "");
                        var head = new StringBuilder();
                        head.Append("HTTP/1.1 ").Append(reply.Status.ToString(CultureInfo.InvariantCulture)).Append(" X\r\nContent-Type: application/json\r\n");
                        if (reply.Chunked) head.Append("Transfer-Encoding: chunked\r\n");
                        else if (reply.Unframed) head.Append("Connection: close\r\n");
                        else head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                        head.Append("\r\n");
                        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
                        stream.Write(headBytes, 0, headBytes.Length);
                        if (reply.Chunked)
                        {
                            for (int at = 0; at < body.Length; at += 7000)
                            {
                                int n = Math.Min(7000, body.Length - at);
                                var size = Encoding.ASCII.GetBytes(n.ToString("x", CultureInfo.InvariantCulture) + ";ext=1\r\n");
                                stream.Write(size, 0, size.Length);
                                stream.Write(body, at, n);
                                stream.Write(new[] { (byte)'\r', (byte)'\n' }, 0, 2);
                            }
                            var end = Encoding.ASCII.GetBytes("0\r\nX-Trailer: 1\r\n\r\n");
                            stream.Write(end, 0, end.Length);
                        }
                        else stream.Write(body, 0, body.Length);
                        stream.Flush();
                        if (reply.Unframed) break;
                    }
                }
            }
            catch (Exception) { /* the client or the test closed the connection */ }
            finally
            {
                lock (_open) _open.Remove(socket);
                Close(socket);
            }
        }

        private static Request ReadRequest(Stream stream)
        {
            string first = ReadLine(stream);
            if (string.IsNullOrEmpty(first)) return null;
            var parts = first.Split(' ');
            var request = new Request { Method = parts[0], Target = parts.Length > 1 ? parts[1] : "/" };
            for (string line = ReadLine(stream); !string.IsNullOrEmpty(line); line = ReadLine(stream))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            if (int.TryParse(request.Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out int length) && length > 0)
            {
                var body = new byte[length];
                for (int read = 0; read < length;)
                {
                    int n = stream.Read(body, read, length - read);
                    if (n <= 0) return null;
                    read += n;
                }
                request.Body = Encoding.UTF8.GetString(body);
            }
            return request;
        }

        private static string ReadLine(Stream stream)
        {
            var line = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return line.Length == 0 ? null : line.ToString();
                if (b == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)b);
            }
        }

        private static void Close(Socket socket)
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
            try { socket.Close(); } catch (Exception) { }
        }
    }
}
