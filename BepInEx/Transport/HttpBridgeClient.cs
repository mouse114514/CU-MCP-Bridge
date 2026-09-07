using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using CUMCP.Protocol;

namespace CUMCP.Transport
{
    /// <summary>
    /// Localhost HTTP link to the Python MCP server. Replaces the old
    /// <c>NamedPipeClientStream</c> transport with the same public surface
    /// (<see cref="ConnectAsync"/> / <see cref="Send"/> / <see cref="Connected"/> /
    /// events), so the rest of the mod is unchanged.
    ///
    /// The mod drives both directions:
    ///   * a poll thread long-polls <c>GET {base}/poll</c> for the next
    ///     Python-&gt;mod <see cref="Message"/> (204 == nothing yet, just re-poll);
    ///   * <see cref="Send"/> queues a message that a sender thread delivers with
    ///     <c>POST {base}/message</c>.
    /// </summary>
    public class HttpBridgeClient : IDisposable
    {
        public static Action<string> Log; // Set by BridgePlugin

        public event Action<Message> OnMessage;
        public event Action<string> OnError;
        public event Action OnDisconnected;

        public bool Connected => _connected;
        public long LastReadTicks => _lastReadTicks;

        private readonly string _baseUrl;
        private readonly int _pollTimeoutSec;
        private readonly ConcurrentQueue<Message> _outbox = new ConcurrentQueue<Message>();
        private readonly AutoResetEvent _outboxSignal = new AutoResetEvent(false);

        private Thread _pollThread;
        private Thread _sendThread;
        private volatile bool _running;
        private volatile bool _connected;
        private long _lastReadTicks;

        public HttpBridgeClient(string baseUrl = "http://127.0.0.1:8765", int pollTimeoutSec = 25)
        {
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = "http://127.0.0.1:8765";
            _baseUrl = baseUrl.TrimEnd('/');
            _pollTimeoutSec = pollTimeoutSec < 1 ? 1 : pollTimeoutSec;
        }

        public void ConnectAsync()
        {
            if (_running) return;
            _running = true;

            _pollThread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "CU-MCP-HttpPoll"
            };
            _sendThread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = "CU-MCP-HttpSend"
            };
            _pollThread.Start();
            _sendThread.Start();
            Log?.Invoke($"[CU-MCP] HTTP transport starting ({_baseUrl})");
        }

        public void Disconnect()
        {
            _running = false;
            _connected = false;
            _outboxSignal.Set();
        }

        public void Send(Message msg)
        {
            if (!_running || msg == null) return;
            _outbox.Enqueue(msg);
            _outboxSignal.Set();
        }

        // ------------------------------------------------------------------ poll
        private void PollLoop()
        {
            Log?.Invoke("[CU-MCP] PollLoop started");
            while (_running)
            {
                try
                {
                    var req = NewRequest($"{_baseUrl}/poll?timeout={_pollTimeoutSec}", "GET");
                    req.Timeout = (_pollTimeoutSec + 10) * 1000;
                    req.ReadWriteTimeout = (_pollTimeoutSec + 10) * 1000;

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        MarkAlive();
                        if (resp.StatusCode == HttpStatusCode.NoContent)
                            continue;

                        string body;
                        using (var stream = resp.GetResponseStream())
                        using (var reader = new StreamReader(stream ?? new MemoryStream(), Encoding.UTF8))
                            body = reader.ReadToEnd();

                        DispatchBody(body);
                    }
                }
                catch (WebException wex)
                {
                    // A timed-out long-poll can surface as 204-in-a-WebException on some Mono builds.
                    if (wex.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.NoContent)
                    {
                        r.Dispose();
                        MarkAlive();
                        continue;
                    }
                    HandleDown(wex.Message);
                    Sleep(3000);
                }
                catch (Exception ex) when (_running)
                {
                    HandleDown(ex.Message);
                    Sleep(3000);
                }
            }
            Log?.Invoke("[CU-MCP] PollLoop ended");
        }

        private void DispatchBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return;
            foreach (var raw in body.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    var msg = Message.FromJson(line);
                    if (msg == null) continue;
                    Log?.Invoke($"[CU-MCP] poll: type={msg.Type} seq={msg.Seq}");
                    var handler = OnMessage;
                    if (handler != null) handler.Invoke(msg);
                    else Log?.Invoke("[CU-MCP] ERROR: OnMessage is NULL!");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[CU-MCP] poll parse error: {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------ send
        private void SendLoop()
        {
            Log?.Invoke("[CU-MCP] SendLoop started");
            while (_running)
            {
                _outboxSignal.WaitOne(1000);
                while (_running && _outbox.TryDequeue(out var msg))
                {
                    try
                    {
                        PostMessage(msg);
                        MarkAlive();
                    }
                    catch (Exception ex)
                    {
                        HandleDown(ex.Message);
                        // Drop the message (the pipe transport dropped on failure too).
                    }
                }
            }
            Log?.Invoke("[CU-MCP] SendLoop ended");
        }

        private void PostMessage(Message msg)
        {
            var bytes = new UTF8Encoding(false).GetBytes(msg.ToJson() + "\n");
            var req = NewRequest($"{_baseUrl}/message", "POST");
            req.ContentType = "application/json";
            req.Timeout = 5000;
            req.ReadWriteTimeout = 5000;
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream())
                s.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                if ((int)resp.StatusCode >= 300)
                    throw new WebException($"POST /message -> {(int)resp.StatusCode}");
            }
        }

        // ----------------------------------------------------------------- misc
        private static HttpWebRequest NewRequest(string url, string method)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Proxy = null;              // skip system-proxy probing for localhost
            req.KeepAlive = true;
            req.AllowAutoRedirect = false;
            return req;
        }

        private void MarkAlive()
        {
            _lastReadTicks = DateTime.UtcNow.Ticks;
            if (!_connected)
            {
                _connected = true;
                Log?.Invoke("[CU-MCP] HTTP transport connected");
            }
        }

        private void HandleDown(string reason)
        {
            bool wasConnected = _connected;
            _connected = false;
            if (wasConnected)
            {
                try { OnError?.Invoke(reason); } catch { }
                try { OnDisconnected?.Invoke(); } catch { }
            }
        }

        private void Sleep(int ms)
        {
            for (int i = 0; i < ms && _running; i += 50)
                Thread.Sleep(50);
        }

        public void Dispose() => Disconnect();
    }
}
