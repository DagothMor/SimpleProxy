using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace PuppyProxy
{
    public class Tunnel : IDisposable
    {
        public DateTime TimestampUtc { get; set; }

        public string SourceIp { get; set; }

        public int SourcePort { get; set; }

        public string DestIp { get; set; }

        public int DestPort { get; set; }

        public string DestHostname { get; set; }

        public int DestHostPort { get; set; }

        public TcpClient ClientTcpClient { get; set; }

        public TcpClient ServerTcpClient { get; set; }

        public Stream ClientStream { get; set; }

        public Stream ServerStream { get; set; }

        private bool _Active = true;

        public Tunnel()
        {

        }
        public Tunnel(
            string sourceIp,
            int sourcePort,
            string destIp,
            int destPort,
            string destHostname,
            int destHostPort,
            TcpClient client,
            TcpClient server)
        {
            if (String.IsNullOrEmpty(sourceIp)) throw new ArgumentNullException(nameof(sourceIp));
            if (String.IsNullOrEmpty(destIp)) throw new ArgumentNullException(nameof(destIp));
            if (String.IsNullOrEmpty(destHostname)) throw new ArgumentNullException(nameof(destHostname));
            if (sourcePort < 0) throw new ArgumentOutOfRangeException(nameof(sourcePort));
            if (destPort < 0) throw new ArgumentOutOfRangeException(nameof(destPort));
            if (destHostPort < 0) throw new ArgumentOutOfRangeException(nameof(destHostPort));
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (server == null) throw new ArgumentNullException(nameof(server));

            TimestampUtc = DateTime.Now.ToUniversalTime();
            SourceIp = sourceIp;
            SourcePort = sourcePort;
            DestIp = destIp;
            DestPort = destPort;
            DestHostname = destHostname;
            DestHostPort = destHostPort;

            ClientTcpClient = client;
            ClientTcpClient.NoDelay = true;
            ClientTcpClient.Client.NoDelay = true;

            ServerTcpClient = server;
            ServerTcpClient.NoDelay = true;
            ServerTcpClient.Client.NoDelay = true;

            ClientStream = client.GetStream();
            ServerStream = server.GetStream();

            Task.Run(() => ClientReaderAsync());
            Task.Run(() => ServerReaderAsync());

            _Active = true;
        }
        public bool IsActive()
        {
            bool clientActive = false;
            bool serverActive = false;
            bool clientSocketActive = false;
            bool serverSocketActive = false;

            if (ClientTcpClient != null)
            {
                clientActive = ClientTcpClient.Connected;

                if (ClientTcpClient.Client != null)
                {
                    TcpState clientState = GetTcpRemoteState(ClientTcpClient);

                    if (clientState == TcpState.Established
                        || clientState == TcpState.Listen
                        || clientState == TcpState.SynReceived
                        || clientState == TcpState.SynSent
                        || clientState == TcpState.TimeWait)
                    {
                        clientSocketActive = true;
                    }
                }
            }

            if (ServerTcpClient != null)
            {
                serverActive = ServerTcpClient.Connected;

                if (ServerTcpClient.Client != null)
                {
                    // see https://github.com/jchristn/PuppyProxy/compare/master...waldekmastykarz:PuppyProxy:master

                    /*
                    TcpState serverState = GetTcpRemoteState(ServerTcpClient);

                    if (serverState == TcpState.Established
                        || serverState == TcpState.Listen
                        || serverState == TcpState.SynReceived
                        || serverState == TcpState.SynSent
                        || serverState == TcpState.TimeWait)
                    {
                        serverSocketActive = true;
                    }
                    */

                    serverSocketActive = true;
                }
            }

            // Console.WriteLine(" " + Active + " " + clientActive + " " + clientSocketActive + " " + serverActive + " " + serverSocketActive);
            _Active = _Active && clientActive && clientSocketActive && serverActive && serverSocketActive;
            return _Active;
        }
        public void Dispose()
        {
            Dispose(true);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (ClientStream != null)
            {
                ClientStream.Close();
                ClientStream.Dispose();
            }

            if (ServerStream != null)
            {
                ServerStream.Close();
                ServerStream.Dispose();
            }

            if (ClientTcpClient != null)
            {
                ClientTcpClient.Dispose();
            }

            if (ServerTcpClient != null)
            {
                ServerTcpClient.Dispose();
            }
        }
        private async Task<byte[]> StreamReadAsync(TcpClient client)
        {
            try
            {
                Stream stream = client.GetStream();
                byte[] buffer = new byte[65536];

                using (MemoryStream memStream = new MemoryStream())
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        if (read == buffer.Length)
                        {
                            return buffer;
                        }
                        else
                        {
                            byte[] data = new byte[read];
                            Buffer.BlockCopy(buffer, 0, data, 0, read);
                            return data;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                _Active = false;
                return null;
            }
            catch (IOException)
            {
                _Active = false;
                return null;
            }
            catch (Exception e)
            {
                //_Logging.Exception("Tunnel", "StreamReadAsync", e);
                _Active = false;
                return null;
            }
            finally
            {
            }
        }

        private TcpState GetTcpRemoteState(TcpClient tcpClient)
        {
            var state = IPGlobalProperties.GetIPGlobalProperties()
              .GetActiveTcpConnections()
              .FirstOrDefault(x => x.RemoteEndPoint.Equals(tcpClient.Client.RemoteEndPoint));
            return state != null ? state.State : TcpState.Unknown;
        }

        private async void ClientReaderAsync()
        {
            try
            {
                byte[] data = null;
                while (true)
                {
                    data = await StreamReadAsync(ClientTcpClient);
                    if (data != null && data.Length > 0)
                    {
                        ServerTcpClient.Client.Send(data);
                        data = null;
                    }

                    if (!_Active) break;
                }
            }
            catch (Exception e)
            {
                _Active = false;
            }
        }

        private async void ServerReaderAsync()
        {
            try
            {
                byte[] data = null;
                while (true)
                {
                    data = await StreamReadAsync(ServerTcpClient);
                    if (data != null && data.Length > 0)
                    {
                        ClientTcpClient.Client.Send(data);
                        data = null;
                    }

                    if (!_Active) break;
                }
            }
            catch (Exception e)
            {
                _Active = false;
            }
        }
    }
}
