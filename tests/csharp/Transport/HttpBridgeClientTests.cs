using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CUMCP.Transport;
using CUMCP.Protocol;
using NUnit.Framework;

namespace CUMCP.Tests.Transport
{
    /// <summary>
    /// Exercises <see cref="HttpBridgeClient"/> against a throwaway
    /// <see cref="HttpListener"/> standing in for the Python bridge server.
    /// </summary>
    [TestFixture]
    public class HttpBridgeClientTests
    {
        private HttpListener _listener;
        private Thread _serverThread;
        private volatile bool _serverRunning;
        private string _baseUrl;

        private readonly ConcurrentQueue<string> _outbound = new ConcurrentQueue<string>();
        private readonly BlockingCollection<string> _received = new BlockingCollection<string>();

        [SetUp]
        public void SetUp()
        {
            int port = FreeTcpPort();
            _baseUrl = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(_baseUrl + "/");
            _listener.Start();

            _serverRunning = true;
            _serverThread = new Thread(ServeLoop) { IsBackground = true };
            _serverThread.Start();
        }

        [TearDown]
        public void TearDown()
        {
            _serverRunning = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private void ServeLoop()
        {
            while (_serverRunning)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                try
                {
                    var path = ctx.Request.Url.AbsolutePath;
                    if (ctx.Request.HttpMethod == "POST" && path == "/message")
                    {
                        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                            _received.Add(sr.ReadToEnd().Trim());
                        WriteBody(ctx, 200, "{\"ok\":true}");
                    }
                    else if (ctx.Request.HttpMethod == "GET" && path == "/poll")
                    {
                        if (_outbound.TryDequeue(out var msg))
                            WriteBody(ctx, 200, msg);
                        else
                            WriteEmpty(ctx, 204);
                    }
                    else if (ctx.Request.HttpMethod == "GET" && path == "/health")
                    {
                        WriteBody(ctx, 200, "{\"status\":\"ok\"}");
                    }
                    else
                    {
                        WriteEmpty(ctx, 404);
                    }
                }
                catch { /* listener torn down mid-request */ }
            }
        }

        private static void WriteBody(HttpListenerContext ctx, int code, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static void WriteEmpty(HttpListenerContext ctx, int code)
        {
            ctx.Response.StatusCode = code;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.OutputStream.Close();
        }

        private static int FreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        [Test]
        public void Send_PostsMessageToServer()
        {
            var client = new HttpBridgeClient(_baseUrl, pollTimeoutSec: 1);
            client.ConnectAsync();

            client.Send(MessageBuilder.StateUpdate(new { test = true }));

            Assert.IsTrue(_received.TryTake(out var json, 3000), "Server did not receive a POST /message");
            var msg = Message.FromJson(json);
            Assert.AreEqual("state_update", msg.Type);

            client.Disconnect();
        }

        [Test]
        public void Poll_DeliversServerMessageToOnMessage()
        {
            var got = new ManualResetEvent(false);
            Message delivered = null;

            _outbound.Enqueue(MessageBuilder.Order("ord_1", "move_to", new { x = 1f, y = 2f }).ToJson());

            var client = new HttpBridgeClient(_baseUrl, pollTimeoutSec: 1);
            client.OnMessage += m => { delivered = m; got.Set(); };
            client.ConnectAsync();

            Assert.IsTrue(got.WaitOne(4000), "OnMessage was not raised for the polled message");
            Assert.AreEqual("order", delivered.Type);
            Assert.IsTrue(client.Connected, "client should be marked connected after a successful poll");

            client.Disconnect();
        }
    }
}
