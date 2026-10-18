using System;
using System.Runtime.InteropServices;
using System.Text;
using Steamworks;
using Steamworks.Data;
using UnityEngine;
using SteamResult = Steamworks.Result;

namespace RuinaNet
{
    // Steam only supplies discovery and transport. NetLink still owns the
    // versioned handshake, reliable ordering and all gameplay delivery.
    internal static class SteamLink
    {
        public const uint AppId = 1256670u;
        private const int VirtualPort = 27147;
        private const string Protocol = "R48";
        private const int MaxWireBytes = 128 * 1024;
        public static string RoomStatus = "未创建 Steam 房间";
        public static Lobby? Room;
        public static bool Busy { get { return _busy; } }
        private static bool _busy, _hooked, _ownCallbacks, _host, _connecting;
        private static int _generation;
        private static long _started;
        private static ulong _owner, _peer, _pendingInvite;
        private static Connection _connection;
        private static HostSocket _socket;
        private static GuestConnection _client;
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static long _nextInit;
        public static string PendingInvite { get { return _pendingInvite == 0 ? "" : _pendingInvite.ToString(); } }

        private static bool InBattle()
        {
            return BattleSync.InBattleLocal() || CreatureSync.Active || RoomPreparing();
        }
        private static bool RoomPreparing() { return RuinaNet.Room.CoopStage; }

        private static bool EnsureInit(bool requested)
        {
            try
            {
                if (!SteamClient.IsValid)
                {
                    if (!requested) return false;
                    // Reuse the game's client when available; never shut it down.
                    SteamClient.Init(AppId, false);
                    _ownCallbacks = true;
                }
                if (!SteamClient.IsLoggedOn) throw new Exception("请先让 Steam 在线并登录");
                if (!_hooked)
                {
                    SteamFriends.OnGameLobbyJoinRequested += OnInvite;
                    SteamMatchmaking.OnLobbyMemberJoined += OnMemberJoined;
                    SteamMatchmaking.OnLobbyMemberLeave += OnMemberLeft;
                    SteamMatchmaking.OnLobbyMemberDisconnected += OnMemberLeft;
                    SteamMatchmaking.OnLobbyMemberKicked += OnMemberKicked;
                    SteamNetworkingUtils.InitRelayNetworkAccess();
                    _hooked = true;
                }
                return true;
            }
            catch (Exception e)
            {
                RoomStatus = "Steam 不可用：" + e.Message + "；可改用 IP 直连";
                return false;
            }
        }

        public static void Tick()
        {
            if (Clock.ElapsedMilliseconds >= _nextInit)
            {
                _nextInit = Clock.ElapsedMilliseconds + 1000;
                EnsureInit(false); // Subscribe to invitations once the game initializes Steam.
            }
            try
            {
                if (_ownCallbacks && SteamClient.IsValid) SteamClient.RunCallbacks();
                if (_socket != null) _socket.Receive(32);
                if (_client != null) _client.Receive(32);
                if (_busy && Clock.ElapsedMilliseconds - _started > 30000)
                    Fail("Steam 房间请求超时，请重试或使用 IP 直连");
                if (_connecting && Clock.ElapsedMilliseconds - _started > 30000)
                    Fail("Steam 好友连接或版本握手超时，请重试或使用 IP 直连");
                if (NetLink.UsingSteam && NetLink.Linked) _connecting = false;
                if (Room.HasValue && !Room.Value.IsOwnedBy((SteamId)_owner))
                    Fail("房主已离开，房间已关闭；不支持迁移房主或断线续战");
            }
            catch (Exception e) { Fail("Steam 网络错误：" + e.Message); }
        }

        public static void CreateRoom()
        {
            if (_busy) { NetPanel.SetHint("Steam 请求处理中，可点击断开取消"); return; }
            if (InBattle()) { NetPanel.SetHint("请先退出当前接待再创建房间"); return; }
            if (!EnsureInit(true)) { NetPanel.SetHint(RoomStatus); return; }
            NetLink.Stop();
            int generation = _generation;
            _busy = true; _started = Clock.ElapsedMilliseconds;
            RoomStatus = "正在创建双人 Steam 房间…";
            CreateAsync(generation);
        }

        private static async void CreateAsync(int generation)
        {
            try
            {
                Lobby? result = await SteamMatchmaking.CreateLobbyAsync(2);
                RuinaNetDriver.Run(delegate
                {
                    if (generation != _generation) { LeaveStaleResult(result); return; }
                    try
                    {
                        if (!result.HasValue) throw new Exception("Steam 返回空房间");
                        Lobby lobby = result.Value;
                        // Keep the room closed until all metadata and socket are ready.
                        Room = lobby; _owner = (ulong)SteamClient.SteamId; _host = true;
                        lobby.SetJoinable(false);
                        if (!lobby.SetFriendsOnly() || !lobby.SetData("ruinanet", "1") ||
                            !lobby.SetData("protocol", Protocol) ||
                            !lobby.SetData("version", RuinaNetInitializer.Version))
                            throw new Exception("无法设置房间信息");
                        lobby.SetData("room", NetLink.RoomName ?? "");
                        _socket = SteamNetworkingSockets.CreateRelaySocket<HostSocket>(VirtualPort);
                        _socket.Generation = generation;
                        if ((uint)_socket.Socket == 0) throw new Exception("无法建立 Steam 监听连接");
                        if (!lobby.SetJoinable(true)) throw new Exception("无法开放房间");
                        _busy = false;
                        RoomStatus = "双人 Steam 房间 " + lobby.Id + "，等待好友";
                        NetPanel.SetHint("点击邀请 Steam 好友，或把房间编号发给好友。双方需启用同版本模组。");
                    }
                    catch (Exception e) { Fail("创建失败：" + e.Message); }
                });
            }
            catch (Exception e) { QueueFailure(generation, "创建失败：" + e.Message); }
        }

        public static void Invite()
        {
            if (!Room.HasValue || !_host) { NetPanel.SetHint("请先创建 Steam 房间"); return; }
            try { SteamFriends.OpenGameInviteOverlay(Room.Value.Id); }
            catch (Exception e) { NetPanel.SetHint("无法打开好友邀请：" + e.Message); }
        }

        private static void OnInvite(Lobby lobby, SteamId inviter)
        {
            ulong id = (ulong)lobby.Id;
            RuinaNetDriver.Run(delegate
            {
                // Accepting a Steam overlay invitation never silently destroys
                // a current battle/link. Joining is explicit in the room panel.
                _pendingInvite = id;
                if (!InBattle()) NetPanel.ShowSteamInvite(id.ToString());
                else RuinaNet.Room.Notice("收到 Steam 好友邀请，可在本场结束后打开联机面板加入");
            });
        }

        public static void JoinRoom(string value)
        {
            ulong id;
            if (!ulong.TryParse((value ?? "").Trim(), out id) || id == 0)
            { NetPanel.SetHint("请填写好友发来的 Steam 房间编号"); return; }
            if (_busy) { NetPanel.SetHint("Steam 请求处理中，可点击断开取消"); return; }
            if (InBattle() || NetLink.Running || Room.HasValue)
            { NetPanel.SetHint("请先结束当前接待并断开连接，再加入 Steam 房间"); return; }
            if (!EnsureInit(true)) { NetPanel.SetHint(RoomStatus); return; }
            NetLink.Stop();
            _busy = true; _started = Clock.ElapsedMilliseconds;
            RoomStatus = "正在加入 Steam 房间…";
            JoinAsync(id, _generation);
        }

        private static async void JoinAsync(ulong id, int generation)
        {
            try
            {
                Lobby? result = await SteamMatchmaking.JoinLobbyAsync((SteamId)id);
                RuinaNetDriver.Run(delegate
                {
                    if (generation != _generation) { LeaveStaleResult(result); return; }
                    try
                    {
                        if (!result.HasValue) throw new Exception("房间不存在、已满或不可加入");
                        Lobby lobby = result.Value;
                        Room = lobby;
                        if (lobby.GetData("ruinanet") != "1" || lobby.GetData("protocol") != Protocol ||
                            lobby.GetData("version") != RuinaNetInitializer.Version)
                            throw new Exception("双方需启用相同的 RuinaNet v" + RuinaNetInitializer.Version);
                        _owner = (ulong)lobby.Owner.Id;
                        if (_owner == (ulong)SteamClient.SteamId || lobby.MemberCount > 2 || lobby.MemberCount < 2)
                            throw new Exception("只支持一名房主与一名客机");
                        _host = false; _peer = _owner;
                        _client = SteamNetworkingSockets.ConnectRelay<GuestConnection>((SteamId)_owner, VirtualPort);
                        _client.Generation = generation;
                        if (_client.Connection.Id == 0) throw new Exception("无法连接 Steam 房主");
                        _busy = false; _pendingInvite = 0;
                        _connecting = true;
                        RoomStatus = "正在连接 Steam 房主…";
                        _started = Clock.ElapsedMilliseconds;
                    }
                    catch (Exception e) { Fail("加入失败：" + e.Message); }
                });
            }
            catch (Exception e) { QueueFailure(generation, "加入失败：" + e.Message); }
        }

        private static void QueueFailure(int generation, string message)
        { RuinaNetDriver.Run(delegate { if (generation == _generation) Fail(message); }); }

        private static void LeaveStaleResult(Lobby? result)
        {
            // A retry may have joined the same lobby. The older completion
            // must not leave the current session's membership in that case.
            if (result.HasValue && (!Room.HasValue || (ulong)Room.Value.Id != (ulong)result.Value.Id))
                result.Value.Leave();
        }

        public static void Leave()
        {
            ++_generation; _busy = _connecting = false;
            Lobby? old = Room; Room = null;
            HostSocket socket = _socket; _socket = null;
            GuestConnection client = _client; _client = null;
            Connection connection = _connection; _connection = default(Connection);
            _owner = _peer = 0; _host = false;
            // Clearing handles first makes late callbacks harmless.
            try { if (old.HasValue) { if (old.Value.IsOwnedBy(SteamClient.SteamId)) old.Value.SetJoinable(false); old.Value.Leave(); } }
            catch (Exception e) { Debug.LogWarning("[RuinaNet] Steam leave: " + e.Message); }
            try { if (connection.Id != 0) connection.Close(false, 0, "RuinaNet disconnected"); }
            catch (Exception e) { Debug.LogWarning("[RuinaNet] Steam close: " + e.Message); }
            try { if (client != null) client.Close(); if (socket != null) socket.Close(); }
            catch (Exception e) { Debug.LogWarning("[RuinaNet] Steam socket close: " + e.Message); }
            RoomStatus = "未创建 Steam 房间";
        }

        private static void Fail(string message)
        {
            NetLink.Stop();
            RoomStatus = message;
            NetPanel.SetHint(message);
            Debug.LogWarning("[RuinaNet] " + message);
        }

        private static bool IsMember(ulong user)
        {
            if (!Room.HasValue || user == 0 || user == (ulong)SteamClient.SteamId ||
                !Room.Value.IsOwnedBy((SteamId)_owner) || Room.Value.MemberCount > 2) return false;
            foreach (Friend member in Room.Value.Members) if ((ulong)member.Id == user) return true;
            return false;
        }

        private static void OnMemberJoined(Lobby lobby, Friend friend)
        {
            ulong id = (ulong)lobby.Id;
            RuinaNetDriver.Run(delegate
            {
                if (!Room.HasValue || (ulong)Room.Value.Id != id) return;
                if (Room.Value.MemberCount > 2) Fail("房间人数异常，只支持双人");
            });
        }

        private static void OnMemberLeft(Lobby lobby, Friend friend)
        {
            ulong id = (ulong)lobby.Id, user = (ulong)friend.Id;
            int generation = _generation;
            RuinaNetDriver.Run(delegate
            {
                if (generation != _generation || !Room.HasValue || (ulong)Room.Value.Id != id) return;
                if (user == _owner || user == _peer || user == (ulong)SteamClient.SteamId)
                    Fail("好友已离开，Steam 房间已关闭；请结束该场后重新连接");
            });
        }

        private static void OnMemberKicked(Lobby lobby, Friend member, Friend actor)
        { OnMemberLeft(lobby, member); }

        private static void Connected(Connection connection, ulong user, int generation, bool host)
        {
            if (generation != _generation || host != _host || !IsMember(user) ||
                (_connection.Id != 0 && _connection.Id != connection.Id) || (_peer != 0 && _peer != user))
            { connection.Close(false, 0, "RuinaNet rejected"); return; }
            if (NetLink.Running && NetLink.UsingSteam && _connection.Id == connection.Id) return;
            _connection = connection; _peer = user;
            _connecting = true; _started = Clock.ElapsedMilliseconds;
            RoomStatus = "Steam 已连接，等待模组握手";
            NetLink.BeginSteam(host, user.ToString(), delegate(string text)
            {
                if (generation != _generation || _connection.Id != connection.Id) return;
                SteamResult result = connection.SendMessage(Encoding.UTF8.GetBytes(text), SendType.Reliable);
                if (result != SteamResult.OK) Fail("Steam 发送失败：" + result);
            });
        }

        private static void Message(Connection connection, IntPtr data, int size, int generation)
        {
            if (generation != _generation || connection.Id != _connection.Id || size <= 0 || size > MaxWireBytes) return;
            // Facepunch releases the native message after this callback returns.
            byte[] bytes = new byte[size]; Marshal.Copy(data, bytes, 0, size);
            string text = Encoding.UTF8.GetString(bytes);
            RuinaNetDriver.Run(delegate
            {
                if (generation == _generation && connection.Id == _connection.Id) NetLink.ReceiveSteam(text);
            });
        }

        // Native callbacks may arrive off-thread. Marshal the whole connection
        // transition so base-class collections and poll groups stay on the pump.
        public sealed class HostSocket : SocketManager
        {
            public int Generation;
            public override void OnConnectionChanged(Connection connection, ConnectionInfo info)
            { RuinaNetDriver.Run(delegate { if (Generation == _generation && this == _socket) base.OnConnectionChanged(connection, info); }); }
            public override void OnConnecting(Connection connection, ConnectionInfo info)
            {
                ulong user = (ulong)info.Identity.SteamId;
                if (!info.Identity.IsSteamId || !IsMember(user) || (_connection.Id != 0 && _connection.Id != connection.Id))
                { connection.Close(false, 0, "Not in this RuinaNet lobby"); return; }
                _connection = connection; _peer = user;
                _connecting = true; _started = Clock.ElapsedMilliseconds;
                if (connection.Accept() != SteamResult.OK) Fail("Steam 无法接受好友连接");
            }
            public override void OnConnected(Connection connection, ConnectionInfo info)
            { base.OnConnected(connection, info); SteamLink.Connected(connection, (ulong)info.Identity.SteamId, Generation, true); }
            public override void OnDisconnected(Connection connection, ConnectionInfo info)
            { base.OnDisconnected(connection, info); connection.Close(false, 0, "RuinaNet closed"); if (connection.Id == _connection.Id) Fail("Steam 好友连接已断开，请结束该场后重连"); }
            public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long number, long received, int channel)
            { Message(connection, data, size, Generation); }
        }

        public sealed class GuestConnection : ConnectionManager
        {
            public int Generation;
            public override void OnConnectionChanged(ConnectionInfo info)
            { RuinaNetDriver.Run(delegate { if (Generation == _generation && this == _client) base.OnConnectionChanged(info); }); }
            public override void OnConnected(ConnectionInfo info)
            { base.OnConnected(info); SteamLink.Connected(Connection, (ulong)info.Identity.SteamId, Generation, false); }
            public override void OnDisconnected(ConnectionInfo info)
            { base.OnDisconnected(info); Fail("Steam 房主连接已断开，请结束该场后重连"); }
            public override void OnMessage(IntPtr data, int size, long number, long received, int channel)
            { Message(Connection, data, size, Generation); }
        }
    }
}
