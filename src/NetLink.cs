// Shared session protocol over UDP or an authenticated Steam relay connection.
// UDP is available independently of Steam.
//
// Wire format is one UTF-8 datagram per line: "RUINANET1 <CMD> [payload]".
//   HELLO          client -> host, repeated until WELCOME arrives
//   WELCOME        host -> client, handshake done
//   ACK            client -> host, confirms reverse path works
//   PING <ms>      sender's own Stopwatch reading
//   PONG <ms>      echoed back, sender computes RTT (clocks never compared)
//   MSG <text>     game payload

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RuinaNet
{
    internal static class NetLink
    {
        public const int DefaultPort = 27015;
        private const string Magic = "RUINANET1";

        public static bool IsHost;
        public static bool Running;
        public static bool Linked;
        public static string Status = "未连接";
        public static string Peer = "-";
        public static int PingMs = -1;

        // Player identity, exchanged with the handshake so both ends can show
        // a real name instead of "me / the other guy". Empty means "not named
        // yet"; the display layer substitutes a default.
        public static string LocalName = "";
        public static string PeerName = "";
        public static string RoomName = "";
        public static string PeerRoomName = "";

        // Fired on the main thread for every MSG datagram.
        public static Action<string> OnPeerMessage;

        private static UdpClient _udp;
        private static IPEndPoint _peer;
        private static Thread _rx;
        private static volatile bool _alive;
        private static Action<string> _steamSend;
        public static bool UsingSteam { get { return _steamSend != null; } }
        public static string TransportName { get { return UsingSteam ? "Steam" : "直连"; } }

        // SteamLink validates lobby membership and Steam identity first.
        // This reuses the same session, ACK and reliable delivery protocol.
        internal static void BeginSteam(bool host, string peer, Action<string> send)
        {
            ResetTransport();
            _steamSend = send;
            IsHost = host;
            Peer = peer;
            Running = true;
            Status = host ? "等待 Steam 握手" : "正在与 Steam 房主握手";
            if (!host) SendRaw(HelloLine());
        }

        internal static void ReceiveSteam(string text)
        {
            if (Running && UsingSteam) Handle(text, null);
        }

        // System.Diagnostics is not imported on purpose: it would make an
        // unqualified Debug ambiguous with UnityEngine.Debug.
        private static readonly System.Diagnostics.Stopwatch Clock =
            System.Diagnostics.Stopwatch.StartNew();

        private static readonly ReliableChannel Reliable = new ReliableChannel();
        private static string _session = Guid.NewGuid().ToString("N");
        private static string _remoteSession;
        private static bool _versionRejected;

        private static bool AcceptSession(string protocol, string session)
        {
            if (protocol != "R48" || string.IsNullOrEmpty(session))
            {
                _versionRejected = true;
                Linked = false;
                Status = "版本不一致：双方都需要 RuinaNet v1.0";
                return false;
            }
            if (_remoteSession != null && _remoteSession != session)
            {
                Linked = false;
                _versionRejected = true;
                Status = "对方已重启，请退出接待后重新连接";
                return false;
            }
            if (_remoteSession == null)
            {
                _remoteSession = session;
                Reliable.Reset(_session, session);
                Reliable.SendWire = delegate(string body) { SendRaw(Magic + " " + body); };
                Reliable.Deliver = delegate(string payload) { if (OnPeerMessage != null) OnPeerMessage(payload); };
            }
            return true;
        }

        private static long _lastPingSent;
        private static long _lastPongAt;
        private static long _lastHelloAt;

        // ------------------------------------------------------------------
        // Start / stop
        // ------------------------------------------------------------------

        public static string Host(int port)
        {
            Stop();
            try
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                IsHost = true;
                Running = true;
                Linked = false;
                PingMs = -1;
                Peer = "-";
                Status = "等待对方连接（UDP " + port + "）";
                StartReceiver();
                Debug.Log("[RuinaNet] listening on UDP " + port);
            }
            catch (Exception e)
            {
                Status = "开房失败: " + e.Message;
                Running = false;
                _udp = null;
                Debug.LogError("[RuinaNet] host failed: " + e);
            }
            return Status;
        }

        public static string Join(string host, int port)
        {
            Stop();
            try
            {
                IPAddress addr = Resolve(host);
                if (addr == null)
                {
                    Status = "无法解析地址 " + host;
                    return Status;
                }

                _peer = new IPEndPoint(addr, port);
                _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                IsHost = false;
                Running = true;
                Linked = false;
                PingMs = -1;
                Peer = addr + ":" + port;
                Status = "正在连接 " + Peer + " …";
                StartReceiver();
                SendRaw(HelloLine());
                Debug.Log("[RuinaNet] joining " + Peer);
            }
            catch (Exception e)
            {
                Status = "连接失败: " + e.Message;
                Running = false;
                _udp = null;
                Debug.LogError("[RuinaNet] join failed: " + e);
            }
            return Status;
        }

        public static void Stop()
        {
            ResetTransport();
            SteamLink.Leave();
        }

        private static void ResetTransport()
        {
            _alive = false;
            Running = false;
            Linked = false;
            PingMs = -1;

            try
            {
                if (_udp != null)
                {
                    _udp.Close();
                }
            }
            catch (Exception)
            {
            }

            _udp = null;
            _steamSend = null;
            _peer = null;
            _rx = null;
            Peer = "-";
            PeerName = "";
            PeerRoomName = "";
            Status = "未连接";
            _session = Guid.NewGuid().ToString("N");
            _remoteSession = null;
            _versionRejected = false;
            Reliable.Reset(_session, null);
            _lastPingSent = _lastHelloAt = _lastPongAt = 0;
        }

        // ------------------------------------------------------------------
        // Main thread, every frame
        // ------------------------------------------------------------------

        public static void Tick()
        {
            if (!Running || (_udp == null && !UsingSteam))
            {
                return;
            }

            long now = Clock.ElapsedMilliseconds;
            Reliable.Tick(now);
            long interval = Linked ? 1000L : 300L;
            if (now - _lastPingSent < interval)
            {
                return;
            }
            _lastPingSent = now;

            if (IsHost)
            {
                if (_peer != null || UsingSteam)
                {
                    SendRaw(Magic + " PING " + now);
                }
            }
            else
            {
                SendRaw(Magic + " PING " + now);
                // A UDP "connection" is just a remembered endpoint, so keep
                // repeating HELLO until the host answers. Cheap and it makes
                // the link self-healing after a tunnel restart.
                if (!Linked && now - _lastHelloAt > 900L)
                {
                    _lastHelloAt = now;
                    SendRaw(HelloLine());
                }
            }

            // Peer went quiet for a while -> mark the link down but keep the
            // socket open so it can come back without a manual reconnect.
            if (Linked && _lastPongAt > 0L && now - _lastPongAt > 10000L)
            {
                Linked = false;
                PingMs = -1;
                Status = IsHost ? "对方无响应" : "主机无响应";
            }
        }

        // ------------------------------------------------------------------
        // Send
        // ------------------------------------------------------------------

        public static void Send(string payload)
        {
            if (payload == null || !Running)
            {
                return;
            }
            if (!Linked || _versionRejected) return;
            if (payload.StartsWith("MOUSE ") || payload == "BATTLE")
                SendRaw(Magic + " MSG " + _session + " " + payload);
            else
                Reliable.Send(payload, Clock.ElapsedMilliseconds);
        }

        private static void SendRaw(string text)
        {
            if (_steamSend != null) { _steamSend(text); return; }
            if (_udp == null || _peer == null)
            {
                return;
            }
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(text);
                _udp.Send(data, data.Length, _peer);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet] send failed: " + e.Message);
            }
        }

        private static void SendTo(string text, IPEndPoint to)
        {
            if (_steamSend != null) { _steamSend(text); return; }
            if (_udp == null || to == null)
            {
                return;
            }
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(text);
                _udp.Send(data, data.Length, to);
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // Receive (background thread)
        // ------------------------------------------------------------------

        private static void StartReceiver()
        {
            _alive = true;
            UdpClient socket = _udp;
            _rx = new Thread(delegate() { ReceiveLoop(socket); });
            _rx.IsBackground = true;
            _rx.Start();
        }

        private static void ReceiveLoop(UdpClient socket)
        {
            while (_alive && socket == _udp)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = socket.Receive(ref from);
                    if (data == null || data.Length == 0)
                    {
                        continue;
                    }
                    string message = Encoding.UTF8.GetString(data);
                    IPEndPoint sender = from;
                    RuinaNetDriver.Run(delegate { if (socket == _udp) Handle(message, sender); });
                }
                catch (Exception)
                {
                    if (!_alive || socket != _udp)
                    {
                        break;
                    }
                    // A closed socket or a transient error: back off a little
                    // instead of spinning.
                    Thread.Sleep(30);
                }
            }
        }

        private static void Handle(string text, IPEndPoint from)
        {
            if (text == null || !text.StartsWith(Magic))
            {
                return;
            }

            string body = text.Substring(Magic.Length).Trim();
            if (body.Length == 0)
            {
                return;
            }

            // Any well-formed RuinaNet datagram proves the tunnel is alive, so the
            // liveness clock is refreshed here and not only on PONG.  During a
            // 30 s battle load the main thread stalls and the heartbeat stops for
            // a while; a PONG-only clock used to declare the link dead right
            // after that and tear the co-op session down.
            _lastPongAt = Clock.ElapsedMilliseconds;

            if (body.StartsWith("HELLO"))
            {
                if (IsHost)
                {
                    if (!AcceptSession(Arg(body, 2), Arg(body, 3))) return;
                    string hello = Arg(body, 1);
                    if (hello.Length > 0)
                    {
                        PeerName = Dec(hello);
                    }

                    // First datagram defines the peer; a changed endpoint means
                    // the tunnel gave us a new public port, so rebind.
                    if (!UsingSteam && (_peer == null || !_peer.Equals(from)))
                    {
                        _peer = from;
                        Peer = from.ToString();
                        Debug.Log("[RuinaNet] peer endpoint = " + Peer);
                    }
                    SendTo(Magic + " WELCOME " + Enc(LocalName) + " " + Enc(RoomName) + " R48 " + _session, from);
                    MarkLinked();
                }
                return;
            }

            if (body.StartsWith("WELCOME"))
            {
                if (IsHost || (!UsingSteam && (_peer == null || !_peer.Equals(from)))) return;
                if (!AcceptSession(Arg(body, 3), Arg(body, 4))) return;
                string who = Arg(body, 1);
                if (who.Length > 0)
                {
                    PeerName = Dec(who);
                }
                string room = Arg(body, 2);
                if (room.Length > 0)
                {
                    PeerRoomName = Dec(room);
                }
                if (!IsHost && (_peer != null || UsingSteam))
                {
                    SendTo(Magic + " ACK", from);
                }
                MarkLinked();
                return;
            }

            if ((!UsingSteam && (_peer == null || !_peer.Equals(from))) || _remoteSession == null || _versionRejected) return;

            if (body.StartsWith("REL ") || body.StartsWith("RACK "))
            {
                Reliable.Receive(body);
                return;
            }

            if (body == "ACK")
            {
                MarkLinked();
                return;
            }

            if (body.StartsWith("PING "))
            {
                if (!UsingSteam && IsHost && (_peer == null || !_peer.Equals(from)))
                {
                    _peer = from;
                    Peer = from.ToString();
                }
                SendTo(Magic + " PONG " + body.Substring(5), from);
                return;
            }

            if (body.StartsWith("PONG "))
            {
                long sent;
                if (long.TryParse(body.Substring(5), out sent))
                {
                    int rtt = (int)(Clock.ElapsedMilliseconds - sent);
                    if (rtt < 0)
                    {
                        rtt = 0;
                    }
                    _lastPongAt = Clock.ElapsedMilliseconds;
                    RuinaNetDriver.Run(delegate
                    {
                        PingMs = rtt;
                        MarkLinked();
                    });
                }
                return;
            }

            if (body.StartsWith("MSG "))
            {
                string prefix = "MSG " + _remoteSession + " ";
                if (!body.StartsWith(prefix)) return;
                string payload = body.Substring(prefix.Length);
                RuinaNetDriver.Run(delegate
                {
                    if (OnPeerMessage != null)
                    {
                        OnPeerMessage(payload);
                    }
                });
            }
        }

        // ------------------------------------------------------------------
        // Handshake name helpers. Names travel percent-encoded so a space in
        // "Old Man Wang" cannot split the datagram into the wrong fields.
        // ------------------------------------------------------------------

        private static string Enc(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "-";
            }
            try
            {
                return Uri.EscapeDataString(s);
            }
            catch (Exception)
            {
                return "-";
            }
        }

        private static string Dec(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "-")
            {
                return "";
            }
            try
            {
                return Uri.UnescapeDataString(s);
            }
            catch (Exception)
            {
                return s;
            }
        }

        private static string Arg(string body, int index)
        {
            string[] parts = body.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= index)
            {
                return "";
            }
            return parts[index];
        }

        private static string HelloLine()
        {
            return Magic + " HELLO " + Enc(LocalName) + " R48 " + _session;
        }

        private static void MarkLinked()
        {
            RuinaNetDriver.Run(delegate
            {
                if (_versionRejected || _remoteSession == null) return;
                Linked = true;
                _lastPongAt = Clock.ElapsedMilliseconds;
                if (IsHost)
                {
                    Status = (_peer != null || UsingSteam) ? ("已连接 " + Peer) : "等待对方连接";
                }
                else
                {
                    Status = "已连接 " + Peer;
                }
            });
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static IPAddress Resolve(string host)
        {
            if (host == null)
            {
                return null;
            }

            IPAddress parsed;
            if (IPAddress.TryParse(host.Trim(), out parsed))
            {
                return parsed;
            }

            // Not a literal, so it may be a SakuraFrp tunnel hostname.
            try
            {
                IPAddress[] all = Dns.GetHostAddresses(host.Trim());
                if (all == null)
                {
                    return null;
                }
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].AddressFamily == AddressFamily.InterNetwork)
                    {
                        return all[i];
                    }
                }
                if (all.Length > 0)
                {
                    return all[0];
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet] dns failed for " + host + ": " + e.Message);
            }
            return null;
        }

        // Best-effort list of this machine's LAN addresses, so the host can
        // read them out loud to the other player.
        public static string LocalAddressList()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                IPAddress[] all = Dns.GetHostAddresses(Dns.GetHostName());
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }
                    string s = all[i].ToString();
                    if (s.StartsWith("127."))
                    {
                        continue;
                    }
                    if (sb.Length > 0)
                    {
                        sb.Append(" / ");
                    }
                    sb.Append(s);
                }
                if (sb.Length == 0)
                {
                    return "未找到局域网地址";
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "读取失败 " + e.Message;
            }
        }
    }
}
