using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// The mesh's recent log lines, kept on the orchestrator so one place holds what every process of the mesh
    /// said last: workers, gateways and the orchestrator's own shipper post batches to <c>POST /api/logs</c>
    /// (<c>{"lines":[{"at","role","instance","level","message"}]}</c>), and readers page through
    /// <c>GET /api/logs?since=&lt;seq&gt;&amp;role=&amp;instance=&amp;limit=</c>, which answers the lines after
    /// <c>since</c> and the next cursor. A fixed number of lines is kept; older ones fall off. A machine that is
    /// deleted keeps its last lines here for as long as the buffer holds them, which is what makes a worker's last
    /// words readable after it is gone. Thread-safe: posts and reads happen on the dashboard's listener threads.
    /// </summary>
    public sealed class LogBuffer
    {
        public struct Line
        {
            public long Seq;
            public string At;
            public string Role;
            public string Instance;
            public string Level;
            public string Message;
        }

        /// <summary>Largest request body accepted, in characters.</summary>
        public const int MaxBodyChars = 1024 * 1024;
        public const int MaxLinesPerRequest = 5000;
        public const int MaxMessageChars = 4096;
        public const int DefaultQueryLimit = 500;

        private readonly object _lock = new object();
        private readonly Line[] _ring;
        private int _count, _head;
        private long _nextSeq = 1;
        private readonly StringBuilder _sb = new StringBuilder(1 << 16);

        public LogBuffer(int capacity = 20000)
        {
            _ring = new Line[Math.Max(16, capacity)];
        }

        public int Count { get { lock (_lock) return _count; } }
        /// <summary>The sequence number the next appended line gets; a reader that asks for <c>since = NextSeq - 1</c> gets only what comes after now.</summary>
        public long NextSeq { get { lock (_lock) return _nextSeq; } }

        public long Append(string role, string instance, string level, string at, string message)
        {
            if (message != null && message.Length > MaxMessageChars) message = message.Substring(0, MaxMessageChars) + "…";
            lock (_lock)
            {
                var line = new Line { Seq = _nextSeq++, At = string.IsNullOrEmpty(at) ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) : at, Role = role ?? "", Instance = instance ?? "", Level = string.IsNullOrEmpty(level) ? "info" : level, Message = message ?? "" };
                int index = (_head + _count) % _ring.Length;
                if (_count == _ring.Length) { _ring[_head] = line; _head = (_head + 1) % _ring.Length; }
                else { _ring[index] = line; _count++; }
                return line.Seq;
            }
        }

        /// <summary>Take a <c>{"lines":[...]}</c> document; null when it was accepted, otherwise why not.</summary>
        public string Accept(string body, out int accepted)
        {
            accepted = 0;
            if (string.IsNullOrEmpty(body)) return "empty body";
            if (body.Length > MaxBodyChars) return $"body larger than {MaxBodyChars} characters";
            if (!PersistenceJson.TryParseObject(body, out var root, out string error)) return error;
            if (!root.TryGetValue("lines", out var linesValue) || !(linesValue is List<object> lines)) return "body must be {\"lines\": [...]}";
            if (lines.Count > MaxLinesPerRequest) return $"at most {MaxLinesPerRequest} lines per request";
            foreach (var item in lines)
            {
                if (!(item is Dictionary<string, object> o)) continue;
                Append(PersistenceJson.GetString(o, "role"), PersistenceJson.GetString(o, "instance"), PersistenceJson.GetString(o, "level"), PersistenceJson.GetString(o, "at"), PersistenceJson.GetString(o, "message"));
                accepted++;
            }
            return null;
        }

        /// <summary>Lines with a sequence number above <paramref name="since"/> that match the filters, oldest first, at most <paramref name="limit"/>.</summary>
        public string Query(long since, string role, string instance, int limit)
        {
            if (limit <= 0) limit = DefaultQueryLimit;
            lock (_lock)
            {
                _sb.Clear();
                var w = new JsonWriter(_sb);
                w.BeginObject();
                w.Key("lines");
                w.BeginArray();
                int written = 0;
                long last = since;
                bool more = false;
                for (int i = 0; i < _count; i++)
                {
                    ref var line = ref _ring[(_head + i) % _ring.Length];
                    if (line.Seq <= since) continue;
                    if (!string.IsNullOrEmpty(role) && role != "all" && line.Role != role) { last = line.Seq; continue; }
                    if (!string.IsNullOrEmpty(instance) && line.Instance != instance) { last = line.Seq; continue; }
                    if (written == limit) { more = true; break; }
                    w.BeginObject();
                    w.Prop("seq", line.Seq);
                    w.Prop("at", line.At);
                    w.Prop("role", line.Role);
                    w.Prop("instance", line.Instance);
                    w.Prop("level", line.Level);
                    w.Prop("message", line.Message);
                    w.EndObject();
                    written++;
                    last = line.Seq;
                }
                w.EndArray();
                w.Prop("next", last);
                w.Prop("more", more);
                w.Prop("oldest", _count > 0 ? _ring[_head].Seq : _nextSeq);
                w.EndObject();
                return _sb.ToString();
            }
        }
    }
}
