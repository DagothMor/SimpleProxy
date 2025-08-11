using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using WatsonWebserver;
using WatsonWebserver.Core;
using RestWrapper;
using SimpleProxy;

namespace PuppyProxy
{
    class Program
    {
        private static string _SettingsFile = null;
        private static Settings _Settings;

        private static TunnelManager _Tunnels;
        private static SecurityModule _SecurityModule;

        private static TcpListener _TcpListener;

        private static CancellationTokenSource _CancelTokenSource;
        private static CancellationToken _CancelToken;
        private static int _ActiveThreads = 0;

        private static readonly EventWaitHandle Terminator = new EventWaitHandle(false, EventResetMode.ManualReset);

        public static void Main(string[] args)
        {
            LoadConfiguration(args);

            _Tunnels = new TunnelManager();
            _SecurityModule = new SecurityModule();

            _CancelTokenSource = new CancellationTokenSource();
            _CancelToken = _CancelTokenSource.Token;

            Task.Run(() => AcceptConnections(), _CancelToken);

            Terminator.WaitOne();
        }

        private static void LoadConfiguration(string[] args)
        {
            bool display = false;
            _SettingsFile = null;

            if (args != null && args.Length > 0)
            {
                foreach (string curr in args)
                {
                    if (curr.StartsWith("--cfg="))
                    {
                        _SettingsFile = curr.Substring(6);
                    }
                    else if (curr.Equals("--display-cfg"))
                    {
                        display = true;
                    }
                }
            }

            if (!String.IsNullOrEmpty(_SettingsFile))
            {
                _Settings = Settings.FromFile(_SettingsFile);
            }
            else
            {
                _Settings = new Settings();
            }

            if (display)
            {
                Console.WriteLine("--- Configuration ---");
                Console.WriteLine(Common.SerializeJson(_Settings, true));
                Console.WriteLine("");
            }
        }

        private static byte[] ConnectResponse()
        {
            string resp = "HTTP/1.1 200 Connection Established\r\nConnection: close\r\n\r\n";
            return Encoding.UTF8.GetBytes(resp);
        }

        private static void ConnectRequest(int connectionId, TcpClient client, HttpRequest req)
        {
            Tunnel currTunnel = null;
            TcpClient server = null;

            try
            {
                client.NoDelay = true;
                client.Client.NoDelay = true;

                server = new TcpClient();

                try
                {
                    server.Connect(req.DestHostname, req.DestHostPort);
                }
                catch (Exception)
                {
                    return;
                }

                server.NoDelay = true;
                server.Client.NoDelay = true;

                byte[] connectResponse = ConnectResponse();
                client.Client.Send(connectResponse);

                currTunnel = new Tunnel(
                    req.SourceIp,
                    req.SourcePort,
                    req.DestIp,
                    req.DestPort,
                    req.DestHostname,
                    req.DestHostPort,
                    client,
                    server);
                _Tunnels.Add(connectionId, currTunnel);

                while (currTunnel.IsActive())
                {
                    Task.Delay(100).Wait();
                }
            }
            catch (Exception e)
            {
            }
            finally
            {
                _Tunnels.Remove(connectionId);

                if (client != null)
                {
                    client.Dispose();
                }

                if (server != null)
                {
                    server.Dispose();
                }
            }
        }

        private static void AcceptConnections()
        {
            try
            {
                _TcpListener = new TcpListener(IPAddress.Any, _Settings.Proxy.ListenerPort);
                _TcpListener.Start();
                while (!_CancelToken.IsCancellationRequested)
                {
                    TcpClient client = _TcpListener.AcceptTcpClient();
                    Task.Run(() => ProcessConnection(client), _CancelToken);
                }
            }
            catch (Exception eOuter)
            {
                // debug
                var test = 1;
            }
        }

        private async static Task ProcessConnection(TcpClient client)
        {
            string clientIp = "";
            int clientPort = 0;
            int connectionId = Thread.CurrentThread.ManagedThreadId;
            _ActiveThreads++;

            try
            {
                if (_ActiveThreads >= _Settings.Proxy.MaxThreads)
                {
                    while (_ActiveThreads >= _Settings.Proxy.MaxThreads)
                    {
                        Task.Delay(100).Wait();
                    }
                }

                IPEndPoint clientIpEndpoint = client.Client.RemoteEndPoint as IPEndPoint;
                IPEndPoint serverIpEndpoint = client.Client.LocalEndPoint as IPEndPoint;

                string clientEndpoint = clientIpEndpoint.ToString();
                string serverEndpoint = serverIpEndpoint.ToString();

                clientIp = clientIpEndpoint.Address.ToString();
                clientPort = clientIpEndpoint.Port;

                string serverIp = serverIpEndpoint.Address.ToString();
                int serverPort = serverIpEndpoint.Port;

                HttpRequest req = HttpRequest.FromTcpClient(client);

                if (req == null)
                {
                    _ActiveThreads--;
                    return;
                }

                req.SourceIp = clientIp;
                req.SourcePort = clientPort;
                req.DestIp = serverIp;
                req.DestPort = serverPort;



                string denyReason = null;
                bool isPermitted = _SecurityModule.IsPermitted(req, out denyReason);
                if (!isPermitted)
                {
                    // debug
                    var test = 1;
                }
                if (req.Method == WatsonWebserver.HttpMethod.CONNECT)
                {
                    ConnectRequest(connectionId, client, req);
                }
                else
                {

                    RestResponse resp = ProxyRequest(req).Result;
                    if (resp != null)
                    {
                        NetworkStream ns = client.GetStream();
                        await SendRestResponse(resp, ns);
                        await ns.FlushAsync();
                        ns.Close();
                    }
                }


                client.Close();
                _ActiveThreads--;

            }
            catch (Exception eInner)
            {
            }
        }
        private async static Task SendRestResponse(RestResponse resp, NetworkStream ns)
        {
            try
            {
                byte[] ret = null;
                string statusLine = resp.ProtocolVersion + " " + resp.StatusCode + " " + resp.StatusDescription + "\r\n";
                ret = Common.AppendBytes(ret, Encoding.UTF8.GetBytes(statusLine));

                if (!String.IsNullOrEmpty(resp.ContentType))
                {
                    string contentTypeLine = "Content-Type: " + resp.ContentType + "\r\n";
                    ret = Common.AppendBytes(ret, Encoding.UTF8.GetBytes(contentTypeLine));
                }

                if (resp.ContentLength > 0)
                {
                    string contentLenLine = "Content-Length: " + resp.ContentLength + "\r\n";
                    ret = Common.AppendBytes(ret, Encoding.UTF8.GetBytes(contentLenLine));
                }

                if (resp.Headers != null && resp.Headers.Count > 0)
                {
                    foreach (KeyValuePair<string, string> currHeader in resp.Headers)
                    {
                        if (String.IsNullOrEmpty(currHeader.Key)) continue;
                        if (currHeader.Key.ToLower().Trim().Equals("content-type")) continue;
                        if (currHeader.Key.ToLower().Trim().Equals("content-length")) continue;

                        string headerLine = currHeader.Key + ": " + currHeader.Value + "\r\n";
                        ret = Common.AppendBytes(ret, Encoding.UTF8.GetBytes(headerLine));
                    }
                }

                ret = Common.AppendBytes(ret, Encoding.UTF8.GetBytes("\r\n"));

                await ns.WriteAsync(ret, 0, ret.Length);
                await ns.FlushAsync();

                if (resp.Data != null && resp.ContentLength > 0)
                {
                    long bytesRemaining = resp.ContentLength;
                    byte[] buffer = new byte[65536];

                    while (bytesRemaining > 0)
                    {
                        int bytesRead = await resp.Data.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            bytesRemaining -= bytesRead;
                            await ns.WriteAsync(buffer, 0, bytesRead);
                            await ns.FlushAsync();
                        }
                    }
                }

                return;
            }
            catch (Exception e)
            {
                //debug
                var error = e as Exception;
                return;
            }
        }

        private async static Task<RestResponse> ProxyRequest(HttpRequest request)
        {
            try
            {
                if (request.Headers != null)
                {
                    string foundVal = null;

                    foreach (KeyValuePair<string, string> currKvp in request.Headers)
                    {
                        if (String.IsNullOrEmpty(currKvp.Key)) continue;
                        if (currKvp.Key.ToLower().Equals("expect"))
                        {
                            foundVal = currKvp.Key;
                            break;
                        }
                    }

                    if (!String.IsNullOrEmpty(foundVal)) request.Headers.Remove(foundVal);
                }

                RestRequest req = new RestRequest(
                    request.FullUrl,
                    (RestWrapper.HttpMethod)(Enum.Parse(typeof(RestWrapper.HttpMethod), request.Method.ToString())),
                    request.Headers,
                    request.ContentType);

                if (request.ContentLength > 0)
                {
                    return await req.SendAsync(request.ContentLength, request.Data);
                }
                else
                {
                    return await req.SendAsync();
                }
            }
            catch (Exception e)
            {
                //debug
                var error = e as Exception;
                return null;
            }
        }


    }

    internal class TunnelManager
    {
        private Dictionary<int, Tunnel> _Tunnels = new Dictionary<int, Tunnel>();
        private readonly object _TunnelsLock = new object();

        internal void Add(int threadId, Tunnel curr)
        {
            lock (_TunnelsLock)
            {
                if (_Tunnels.ContainsKey(threadId)) _Tunnels.Remove(threadId);
                _Tunnels.Add(threadId, curr);
            }
        }

        internal void Remove(int threadId)
        {
            lock (_TunnelsLock)
            {
                if (_Tunnels.ContainsKey(threadId))
                {
                    Tunnel curr = _Tunnels[threadId];
                    _Tunnels.Remove(threadId);
                    curr.Dispose();
                }
            }
        }
    }

    internal class SecurityModule
    {
        public bool IsPermitted(HttpRequest req, out string denyReason)
        {
            denyReason = null;

            if (req == null) throw new ArgumentNullException(nameof(req));

            return true;
        }
    }

}
