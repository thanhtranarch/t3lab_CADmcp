using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace T3LabCAD.Core
{
    /// <summary>
    /// Loopback TCP server. Wire format: one JSON object per line in each direction
    /// ({"id", "method", "params"} -> {"id", "result" | "error"}). A first line that is not
    /// JSON (e.g. an HTTP request from a browser) closes the connection.
    /// </summary>
    internal sealed class Server
    {
        readonly Func<string, string> _handler;
        TcpListener _listener;
        Thread _acceptThread;
        volatile bool _running;

        public Server(Func<string, string> handler)
        {
            _handler = handler;
        }

        public int Port { get; private set; }

        public void Start(int portMin, int portMax)
        {
            for (int port = portMin; port <= portMax && _listener == null; port++)
            {
                var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = port;
                }
                catch (SocketException)
                {
                    // taken by another AutoCAD (or something else): try the next port
                }
            }
            if (_listener == null) throw new InvalidOperationException($"No free port in {portMin}-{portMax}.");

            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "T3LabCAD MCP accept" };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        void AcceptLoop()
        {
            var listener = _listener;
            while (_running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch
                {
                    if (!_running) break;
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        void Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
                {
                    client.NoDelay = true;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        string response = _handler(line);
                        if (response == null) break;
                        writer.WriteLine(response);
                    }
                }
            }
            catch (IOException)
            {
                // client went away
            }
            catch (Exception ex)
            {
                Log.Error("connection: " + ex);
            }
        }
    }
}
