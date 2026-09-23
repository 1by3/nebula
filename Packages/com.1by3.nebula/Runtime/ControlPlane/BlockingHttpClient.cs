using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// A small HTTP/1.1 client that does all its work on the calling thread, for the control plane's dedicated reader
    /// and sender threads (<see cref="RemoteControlPlane"/>).
    /// <para>
    /// A thread that blocks on <c>HttpClient.SendAsync(...).GetAwaiter().GetResult()</c> still needs a thread-pool
    /// thread to finish the request: the socket's completion and every continuation after it run on the pool. When a
    /// game fills the pool with blocking work, the control plane's heartbeats stop and its mirror freezes, and the
    /// orchestrator declares the worker dead. This client resolves, connects, writes and reads with blocking socket
    /// calls (and <see cref="SslStream"/>'s synchronous methods for <c>https</c>), so it keeps working with the pool
    /// starved. It speaks only what the orchestrator answers with: <c>Content-Length</c> or chunked bodies, keep-alive
    /// or <c>Connection: close</c>.
    /// </para>
    /// <para>
    /// One instance holds at most one connection, kept open between requests, and serves one thread at a time.
    /// <see cref="Abort"/> may be called from any thread: it closes the connection, so a request blocked on it
    /// fails at once, and every later request fails too.
    /// </para>
    /// </summary>
    internal sealed class BlockingHttpClient : IDisposable
    {
        /// <summary>A response: the status code and the body decoded as UTF-8.</summary>
        public struct Response
        {
            public int Status;
            public string Body;
            public bool IsSuccessStatusCode => Status >= 200 && Status <= 299;
        }

        /// <summary>Largest response body accepted (a control-plane document is far smaller).</summary>
        public const int MaxBodyBytes = 64 * 1024 * 1024;
        private const int MaxLineBytes = 16 * 1024;
        private const int MaxHeaderLines = 128;
        /// <summary>How long one wait for a connection to complete lasts before the client checks for an abort.</summary>
        private const int ConnectSliceMicroseconds = 50 * 1000;

        private readonly int _timeoutMs;
        private readonly object _gate = new object();
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _bufferStart, _bufferEnd;
        private Socket _socket;
        private Stream _stream;
        private string _connectedTo;
        private bool _aborted;

        /// <param name="timeout">Longest a connect, or any one read or write, may take before the request fails.</param>
        public BlockingHttpClient(TimeSpan timeout)
        {
            _timeoutMs = (int)Math.Max(1, Math.Min(int.MaxValue, timeout.TotalMilliseconds));
        }

        /// <summary>
        /// Send one request to <paramref name="url"/> and read the whole response. <paramref name="token"/> goes in the
        /// mesh-token header when it is not null; <paramref name="jsonBody"/> is sent as <c>application/json</c> when it
        /// is not null. Throws <see cref="HttpRequestException"/> when the request cannot be completed. A request on a
        /// kept-alive connection that the server had closed in the meantime is sent once more on a new connection, as
        /// long as nothing of a response had arrived.
        /// </summary>
        public Response Send(string method, string url, string token, string jsonBody)
        {
            var uri = new Uri(url);
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException($"unsupported address {url} (http or https only)");
            byte[] request = BuildRequest(method, uri, token, jsonBody);
            for (int attempt = 0; ; attempt++)
            {
                bool reused = Connect(uri);
                bool responseStarted = false;
                try
                {
                    Stream stream = _stream;
                    stream.Write(request, 0, request.Length);
                    stream.Flush();
                    var response = ReadResponse(ref responseStarted, out bool keepAlive);
                    if (!keepAlive) CloseConnection();
                    return response;
                }
                catch (Exception e)
                {
                    CloseConnection();
                    if (Aborted) throw new HttpRequestException("the request was cancelled", e);
                    if (reused && !responseStarted && attempt == 0 && (e is IOException || e is SocketException || e is ObjectDisposedException)) continue;
                    throw Wrap(e, uri);
                }
            }
        }

        /// <summary>Close the connection and refuse every later request. Any thread.</summary>
        public void Abort()
        {
            lock (_gate)
            {
                _aborted = true;
                CloseLocked();
            }
        }

        public void Dispose() => Abort();

        private bool Aborted { get { lock (_gate) return _aborted; } }

        // ---------------------------------------------------------------------------------------- connection

        /// <summary>Make sure a connection to <paramref name="uri"/>'s host is open. True when an open one was reused.</summary>
        private bool Connect(Uri uri)
        {
            string key = uri.Scheme + "://" + uri.Authority;
            lock (_gate)
            {
                if (_aborted) throw new HttpRequestException("the request was cancelled");
                if (_stream != null && _connectedTo == key) return true;
                CloseLocked();
            }
            string host = uri.DnsSafeHost;
            IPAddress[] addresses;
            try { addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : Dns.GetHostAddresses(host); }
            catch (Exception e) { throw Wrap(e, uri); }
            if (addresses.Length == 0) throw new HttpRequestException($"{host} has no address");
            Exception last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                lock (_gate)
                {
                    if (_aborted) { socket.Close(); throw new HttpRequestException("the request was cancelled"); }
                    _socket = socket;
                }
                try
                {
                    socket.NoDelay = true;
                    socket.ReceiveTimeout = _timeoutMs;
                    socket.SendTimeout = _timeoutMs;
                    ConnectWithTimeout(socket, new IPEndPoint(address, uri.Port));
                    Stream stream = new NetworkStream(socket, true);
                    if (uri.Scheme == Uri.UriSchemeHttps)
                    {
                        var ssl = new SslStream(stream, false);
                        ssl.AuthenticateAsClient(host);
                        stream = ssl;
                    }
                    lock (_gate)
                    {
                        if (_aborted) { stream.Dispose(); throw new HttpRequestException("the request was cancelled"); }
                        _stream = stream;
                        _connectedTo = key;
                        _bufferStart = _bufferEnd = 0;
                    }
                    return false;
                }
                catch (Exception e)
                {
                    lock (_gate)
                    {
                        if (_socket == socket) _socket = null;
                        if (_aborted) { socket.Close(); throw new HttpRequestException("the request was cancelled", e); }
                    }
                    socket.Close();
                    last = e;
                }
            }
            throw Wrap(last, uri);
        }

        /// <summary>
        /// Connect without blocking past the timeout: a blocking <c>connect</c> to a host that drops packets waits for the
        /// operating system's own limit (minutes on Linux). The socket is non-blocking only for the connect itself.
        /// </summary>
        private void ConnectWithTimeout(Socket socket, EndPoint endPoint)
        {
            socket.Blocking = false;
            try { socket.Connect(endPoint); }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock || e.SocketErrorCode == SocketError.InProgress || e.SocketErrorCode == SocketError.AlreadyInProgress) { }
            var started = DateTime.UtcNow;
            while (true)
            {
                if (socket.Poll(ConnectSliceMicroseconds, SelectMode.SelectWrite)) break;
                // Windows reports a refused connection as an error, never as writable.
                if (socket.Poll(0, SelectMode.SelectError)) break;
                if (Aborted) throw new HttpRequestException("the request was cancelled");
                if ((DateTime.UtcNow - started).TotalMilliseconds > _timeoutMs) throw new SocketException((int)SocketError.TimedOut);
            }
            int error = (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
            if (error != 0) throw new SocketException(error);
            socket.Blocking = true;
        }

        private void CloseConnection()
        {
            lock (_gate) CloseLocked();
        }

        private void CloseLocked()
        {
            var socket = _socket;
            var stream = _stream;
            _socket = null;
            _stream = null;
            _connectedTo = null;
            _bufferStart = _bufferEnd = 0;
            // Shut the socket down first: on some platforms closing it alone does not wake a thread blocked reading it.
            try { socket?.Shutdown(SocketShutdown.Both); } catch (Exception) { }
            try { stream?.Dispose(); } catch (Exception) { }
            try { socket?.Close(); } catch (Exception) { }
        }

        // ---------------------------------------------------------------------------------------- request

        private static byte[] BuildRequest(string method, Uri uri, string token, string jsonBody)
        {
            if (token != null && (token.IndexOf('\r') >= 0 || token.IndexOf('\n') >= 0))
                throw new HttpRequestException("the mesh token contains a line break");
            byte[] body = jsonBody == null ? null : Encoding.UTF8.GetBytes(jsonBody);
            var head = new StringBuilder(256);
            head.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
            head.Append("Host: ").Append(uri.Authority).Append("\r\n");
            if (token != null) head.Append(ControlPlaneHost.TokenHeader).Append(": ").Append(token).Append("\r\n");
            if (body != null)
            {
                head.Append("Content-Type: application/json; charset=utf-8\r\n");
                head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            head.Append("\r\n");
            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            if (body == null) return headBytes;
            var all = new byte[headBytes.Length + body.Length];
            Buffer.BlockCopy(headBytes, 0, all, 0, headBytes.Length);
            Buffer.BlockCopy(body, 0, all, headBytes.Length, body.Length);
            return all;
        }

        // ---------------------------------------------------------------------------------------- response

        private Response ReadResponse(ref bool responseStarted, out bool keepAlive)
        {
            int status;
            bool http11, close, chunked;
            long contentLength;
            while (true)
            {
                string statusLine = ReadLine(allowEof: !responseStarted);
                if (statusLine == null) throw new IOException("the server closed the connection before answering");
                responseStarted = true;
                // "HTTP/1.1 200 OK"
                if (!statusLine.StartsWith("HTTP/", StringComparison.Ordinal) || statusLine.Length < 12 ||
                    !int.TryParse(statusLine.Substring(9, 3), NumberStyles.None, CultureInfo.InvariantCulture, out status))
                    throw new HttpRequestException($"not an HTTP response: {Trim(statusLine)}");
                http11 = !statusLine.StartsWith("HTTP/1.0", StringComparison.Ordinal);
                close = !http11;
                chunked = false;
                contentLength = -1;
                for (int i = 0; ; i++)
                {
                    if (i == MaxHeaderLines) throw new HttpRequestException("too many response headers");
                    string line = ReadLine(allowEof: false);
                    if (line.Length == 0) break;
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string name = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength))
                            throw new HttpRequestException($"bad Content-Length: {Trim(value)}");
                    }
                    else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        chunked = value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
                    else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0) close = true;
                        else if (value.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0) close = false;
                    }
                }
                if (status >= 100 && status < 200) continue; // an interim answer (100 Continue): the real one follows
                break;
            }
            var body = new MemoryStream();
            if (status == 204 || status == 304) { }
            else if (chunked) ReadChunked(body);
            else if (contentLength >= 0)
            {
                if (contentLength > MaxBodyBytes) throw new HttpRequestException($"response body too large ({contentLength} bytes)");
                ReadExactly(body, (int)contentLength);
            }
            else
            {
                // No length: the body runs to the end of the connection.
                ReadToEnd(body);
                close = true;
            }
            keepAlive = !close;
            return new Response { Status = status, Body = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length) };
        }

        private void ReadChunked(MemoryStream body)
        {
            while (true)
            {
                string line = ReadLine(allowEof: false);
                int semicolon = line.IndexOf(';');
                string hex = (semicolon >= 0 ? line.Substring(0, semicolon) : line).Trim();
                if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int size) || size < 0)
                    throw new HttpRequestException($"bad chunk size: {Trim(line)}");
                if (size == 0)
                {
                    while (ReadLine(allowEof: false).Length > 0) { } // trailers
                    return;
                }
                if (body.Length + size > MaxBodyBytes) throw new HttpRequestException("response body too large");
                ReadExactly(body, size);
                if (ReadLine(allowEof: false).Length != 0) throw new HttpRequestException("malformed chunk");
            }
        }

        private void ReadExactly(MemoryStream body, int count)
        {
            while (count > 0)
            {
                if (_bufferStart == _bufferEnd && !Fill()) throw new IOException("the server closed the connection mid-response");
                int n = Math.Min(count, _bufferEnd - _bufferStart);
                body.Write(_buffer, _bufferStart, n);
                _bufferStart += n;
                count -= n;
            }
        }

        private void ReadToEnd(MemoryStream body)
        {
            while (true)
            {
                if (_bufferStart == _bufferEnd && !Fill()) return;
                int n = _bufferEnd - _bufferStart;
                if (body.Length + n > MaxBodyBytes) throw new HttpRequestException("response body too large");
                body.Write(_buffer, _bufferStart, n);
                _bufferStart = _bufferEnd;
            }
        }

        /// <summary>One CRLF-terminated line without its terminator; null at the end of the stream when <paramref name="allowEof"/>.</summary>
        private string ReadLine(bool allowEof)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_bufferStart == _bufferEnd && !Fill())
                {
                    if (allowEof && line.Length == 0) return null;
                    throw new IOException("the server closed the connection mid-response");
                }
                byte b = _buffer[_bufferStart++];
                if (b == (byte)'\n')
                {
                    if (line.Length > 0 && line[line.Length - 1] == '\r') line.Length--;
                    return line.ToString();
                }
                if (line.Length >= MaxLineBytes) throw new HttpRequestException("response line too long");
                line.Append((char)b);
            }
        }

        /// <summary>Read more of the response into the empty buffer. False at the end of the stream.</summary>
        private bool Fill()
        {
            Stream stream = _stream;
            if (stream == null) throw new IOException("the connection was closed");
            int n = stream.Read(_buffer, 0, _buffer.Length);
            _bufferStart = 0;
            _bufferEnd = Math.Max(0, n);
            return n > 0;
        }

        // ---------------------------------------------------------------------------------------- errors

        private Exception Wrap(Exception e, Uri uri)
        {
            if (e is HttpRequestException) return e;
            var socket = e as SocketException ?? e?.InnerException as SocketException;
            if (socket != null && socket.SocketErrorCode == SocketError.TimedOut)
                return new HttpRequestException($"no answer from {uri.Authority} within {_timeoutMs / 1000.0:0.#} s", e);
            return new HttpRequestException($"{e?.Message} ({uri.Authority})", e);
        }

        private static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 200 ? s.Substring(0, 200) + "..." : s;
    }
}
