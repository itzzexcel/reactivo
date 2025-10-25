using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace reactivo.Classes
{
    public class WebScket
    {
        private int port = 5343;
        private HttpListener? httpListener;
        private List<WebSocket> connectedClients = new List<WebSocket>();
        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

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

        public async Task StartServerAsync()
        {
            httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://localhost:{port}/");
            httpListener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {
                httpListener.Start();
                Console.WriteLine($"WebSocket started @ ws://localhost:{port}/");

                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var context = await httpListener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        ProcessWebSocketRequest(context);
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        private async void ProcessWebSocketRequest(HttpListenerContext context)
        {
            WebSocket webSocket = null!;

            var webSocketContext = await context.AcceptWebSocketAsync(null);
            webSocket = webSocketContext.WebSocket;

            lock (connectedClients)
            {
                connectedClients.Add(webSocket);
            }

            Console.WriteLine($"Cliente conectado.");

            await HandleWebSocketConnection(webSocket);

            if (webSocket != null)
            {
                lock (connectedClients)
                {
                    connectedClients.Remove(webSocket);
                }
            }
        }

        private async Task HandleWebSocketConnection(WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationTokenSource.Token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"Message received: {message}");
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
            }
        }

        public async Task SendMessageToClient(WebSocket webSocket, string message)
        {
            if (webSocket?.State == WebSocketState.Open)
            {
                var buffer = Encoding.UTF8.GetBytes(message);
                await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        public async Task BroadcastMessage(string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);

            List<WebSocket> snapshot;
            lock (connectedClients)
            {
                snapshot = new List<WebSocket>(connectedClients);
            }

            var clientsToRemove = new List<WebSocket>();

            foreach (var client in snapshot)
            {
                if (client.State == WebSocketState.Open)
                {
                    try
                    {
                        await client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch
                    {
                        clientsToRemove.Add(client);
                    }
                }
                else
                {
                    // Not open -> schedule remove.
                    clientsToRemove.Add(client);
                }
            }

            if (clientsToRemove.Count > 0)
            {
                lock (connectedClients)
                {
                    foreach (var client in clientsToRemove)
                    {
                        connectedClients.Remove(client);
                        try
                        {
                            client?.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        public async Task SendMessageToClient(int clientIndex, string message)
        {
            WebSocket client;

            lock (connectedClients)
            {
                if (clientIndex < 0 || clientIndex >= connectedClients.Count)
                    throw new ArgumentOutOfRangeException(nameof(clientIndex));

                client = connectedClients[clientIndex];
            }

            await SendMessageToClient(client, message);
        }

        public void StopServer()
        {
            cancellationTokenSource.Cancel();
            httpListener?.Stop();
            httpListener?.Close();

            lock (connectedClients)
            {
                foreach (var client in connectedClients)
                {
                    client?.Dispose();
                }
                connectedClients.Clear();
            }
        }

        public int GetConnectedClientsCount()
        {
            lock (connectedClients)
            {
                return connectedClients.Count;
            }
        }
    }
}