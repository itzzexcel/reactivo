using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace reactivo.Classes
{
    public class WebScket
    {
        private sealed class ClientConnection : IDisposable
        {
            public readonly WebSocket Socket;
            public readonly SemaphoreSlim SendLock = new(1, 1);
            private int _disposed;

            public ClientConnection(WebSocket socket)
            {
                Socket = socket;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { Socket.Dispose(); } catch { }
                try { SendLock.Dispose(); } catch { }
            }
        }

        private readonly int port = 5343;
        private readonly object _clientsLock = new();
        private readonly object _listenerLock = new();
        private readonly List<ClientConnection> connectedClients = new();
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private HttpListener? httpListener;

        public WebScket()
        {
        }

        public WebScket(int port)
        {
            this.port = port;
        }

        public static void Init()
        {
            var websocket = new WebScket();
            websocket.StartServerAsync().GetAwaiter().GetResult();
        }

        public async Task StartServerAsync(CancellationToken externalToken = default)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationTokenSource.Token,
                externalToken
            );
            CancellationToken ct = linkedCts.Token;

            int backoffMs = 1000;

            while (!ct.IsCancellationRequested)
            {
                HttpListener? listener = null;
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();

                    lock (_listenerLock)
                    {
                        httpListener = listener;
                    }

                    backoffMs = 1000;
                    ConsoleManager.LogEvent($"WebSocket started @ ws://localhost:{port}/");

                    while (!ct.IsCancellationRequested)
                    {
                        HttpListenerContext context = await listener.GetContextAsync().WaitAsync(ct);
                        if (context.Request.IsWebSocketRequest)
                        {
                            _ = Task.Run(() => ProcessWebSocketRequestAsync(context, ct), ct);
                        }
                        else
                        {
                            try
                            {
                                context.Response.StatusCode = 400;
                                context.Response.Close();
                            }
                            catch { }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ConsoleManager.LogError($"WebSocket listener faulted (restarting in {backoffMs}ms): {ex.Message}");
                }
                finally
                {
                    lock (_listenerLock)
                    {
                        if (ReferenceEquals(httpListener, listener))
                        {
                            httpListener = null;
                        }
                    }

                    if (listener != null)
                    {
                        try { listener.Stop(); } catch { }
                        try { listener.Abort(); } catch { }
                        try { listener.Close(); } catch { }
                    }
                }

                if (ct.IsCancellationRequested) break;

                try
                {
                    await Task.Delay(backoffMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoffMs = backoffMs switch
                {
                    < 2000 => 2000,
                    < 5000 => 5000,
                    _ => 10000
                };
            }
        }

        private async Task ProcessWebSocketRequestAsync(HttpListenerContext context, CancellationToken ct)
        {
            ClientConnection? conn = null;
            try
            {
                var webSocketContext = await context.AcceptWebSocketAsync(null);
                conn = new ClientConnection(webSocketContext.WebSocket);

                lock (_clientsLock)
                {
                    connectedClients.Add(conn);
                }

                ConsoleManager.LogEvent("WebSocket client connected.");
                await HandleWebSocketConnection(conn.Socket, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ConsoleManager.LogError($"WebSocket accept/session error: {ex.Message}");
                if (conn == null)
                {
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch { }
                }
            }
            finally
            {
                if (conn != null)
                {
                    RemoveAndDisposeClient(conn);
                }
            }
        }

        private async Task HandleWebSocketConnection(WebSocket webSocket, CancellationToken ct)
        {
            var buffer = new byte[1024 * 4];

            try
            {
                while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        ct
                    );

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (!string.IsNullOrEmpty(message))
                        {
                            ConsoleManager.LogEvent($"Got ID from WebSocket. Device: {message}");

                            string previousDevice = Globals.tidalReceivedDevice;
                            Globals.tidalReceivedDevice = message;

                            if (previousDevice != message)
                            {
                                Program.DeviceChanged();
                            }
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ConsoleManager.LogEvent("Client terminated ws connection.");
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None
                        );
                        break;
                    }
                }
            }
            catch (WebSocketException wsEx)
            {
                ConsoleManager.LogEvent($"WebSocket disconnected: {wsEx.Message}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ConsoleManager.LogError($"WebSocket receive exception: {ex.Message}");
            }
            finally
            {
                if (webSocket.State == WebSocketState.Open ||
                    webSocket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        using var closeTimeout = new CancellationTokenSource(500);
                        await webSocket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "STATUS_CLOSING",
                            closeTimeout.Token
                        );
                    }
                    catch { }
                }
            }
        }

        private void RemoveAndDisposeClient(ClientConnection conn)
        {
            bool removed;
            lock (_clientsLock)
            {
                removed = connectedClients.Remove(conn);
            }
            if (removed)
            {
                conn.Dispose();
            }
        }

        public bool HasConnectedClients
        {
            get
            {
                lock (_clientsLock)
                {
                    return connectedClients.Count > 0;
                }
            }
        }

        public async Task SendMessageToClient(WebSocket webSocket, string message)
        {
            if (webSocket?.State != WebSocketState.Open) return;

            ClientConnection? match = null;
            lock (_clientsLock)
            {
                match = connectedClients.Find(c => ReferenceEquals(c.Socket, webSocket));
            }

            var buffer = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(buffer);

            if (match != null)
            {
                if (!match.SendLock.Wait(0)) return;
                try
                {
                    using var sendTimeout = new CancellationTokenSource(1500);
                    await webSocket.SendAsync(segment, WebSocketMessageType.Text, true, sendTimeout.Token);
                }
                catch (Exception ex)
                {
                    ConsoleManager.LogError($"SendMessageToClient failed: {ex.Message}");
                    RemoveAndDisposeClient(match);
                }
                finally
                {
                    try { match.SendLock.Release(); } catch (ObjectDisposedException) { }
                }
            }
            else
            {
                try
                {
                    using var sendTimeout = new CancellationTokenSource(1500);
                    await webSocket.SendAsync(segment, WebSocketMessageType.Text, true, sendTimeout.Token);
                }
                catch { }
            }
        }

        public async Task BroadcastBytesAsync(byte[] buffer)
        {
            List<ClientConnection> snapshot;
            lock (_clientsLock)
            {
                if (connectedClients.Count == 0) return;
                snapshot = new List<ClientConnection>(connectedClients);
            }

            var segment = new ArraySegment<byte>(buffer);
            List<ClientConnection>? deadClients = null;

            foreach (var conn in snapshot)
            {
                if (conn.Socket.State != WebSocketState.Open)
                {
                    deadClients ??= new List<ClientConnection>();
                    deadClients.Add(conn);
                    continue;
                }

                // Non-blocking: drop stale analysis frame if previous send is still in flight
                bool acquired = false;
                try
                {
                    acquired = conn.SendLock.Wait(0);
                }
                catch (ObjectDisposedException)
                {
                    deadClients ??= new List<ClientConnection>();
                    deadClients.Add(conn);
                    continue;
                }

                if (!acquired) continue;

                try
                {
                    using var timeoutCts = new CancellationTokenSource(1500);
                    await conn.Socket.SendAsync(segment, WebSocketMessageType.Text, true, timeoutCts.Token);
                }
                catch
                {
                    deadClients ??= new List<ClientConnection>();
                    deadClients.Add(conn);
                }
                finally
                {
                    try { conn.SendLock.Release(); } catch (ObjectDisposedException) { }
                }
            }

            if (deadClients != null)
            {
                foreach (var dead in deadClients)
                {
                    RemoveAndDisposeClient(dead);
                }
            }
        }

        public async Task BroadcastMessage(string message)
        {
            if (!HasConnectedClients) return;
            var buffer = Encoding.UTF8.GetBytes(message);
            await BroadcastBytesAsync(buffer);
        }

        public async Task SendMessageToClient(int clientIndex, string message)
        {
            ClientConnection client;

            lock (_clientsLock)
            {
                if (clientIndex < 0 || clientIndex >= connectedClients.Count)
                    throw new ArgumentOutOfRangeException(nameof(clientIndex));

                client = connectedClients[clientIndex];
            }

            await SendMessageToClient(client.Socket, message);
        }

        public void StopServer()
        {
            try { cancellationTokenSource.Cancel(); } catch { }

            lock (_listenerLock)
            {
                if (httpListener != null)
                {
                    try { httpListener.Stop(); } catch { }
                    try { httpListener.Abort(); } catch { }
                    try { httpListener.Close(); } catch { }
                    httpListener = null;
                }
            }

            List<ClientConnection> toDispose;
            lock (_clientsLock)
            {
                toDispose = new List<ClientConnection>(connectedClients);
                connectedClients.Clear();
            }

            foreach (var conn in toDispose)
            {
                conn.Dispose();
            }
        }

        public int GetConnectedClientsCount()
        {
            lock (_clientsLock)
            {
                return connectedClients.Count;
            }
        }
    }
}
