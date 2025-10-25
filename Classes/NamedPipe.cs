using System;
using System.IO.Pipes;
using System.Text;

namespace reactivo.Classes
{
    public class NamedPipe
    {
        private readonly string _pipeName = "ghouljaboy";
        private NamedPipeServerStream? _server;

        public NamedPipe()
        {
        }

        /// <summary>
        /// Starts the named pipe server and waits for a client to connect.
        /// </summary>
        public void Start()
        {
            if (_server == null)
            {
                _server = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
            }

            Console.WriteLine($"Waiting for client to connect on pipe '{_pipeName}'...");
            _server.WaitForConnection();
            Console.WriteLine("Client connected!");
        }

        /// <summary>
        /// Sends a message to the connected client.
        /// </summary>
        /// <param name="message">The message to send</param>
        public void Send(string message)
        {
            if (_server == null || !_server.IsConnected)
            {
                throw new InvalidOperationException("No client connected. Call Start() first.");
            }

            byte[] buffer = Encoding.UTF8.GetBytes(message + "\n"); // add newline
            _server.Write(buffer, 0, buffer.Length);
            _server.Flush();
        }

        /// <summary>
        /// Closes the named pipe server.
        /// </summary>
        public void Close()
        {
            if (_server != null)
            {
                _server.Flush();
                _server.Disconnect();
                _server.Close();
                _server.Dispose();
                _server = null;
                Console.WriteLine("Pipe server closed.");
            }
        }
    }
}
