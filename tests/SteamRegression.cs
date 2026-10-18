using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using RuinaNet;
using Steamworks;
using Steamworks.Data;

// Run the production SteamLink, NetLink and ReliableChannel with a controlled
// Steam API boundary. No Steam login, network account or game assets required.
class SteamRegression
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    static Lobby Lobby(ulong id, ulong owner, params ulong[] users)
    {
        Fake.LobbyData data = new Fake.LobbyData { Owner = owner };
        foreach (ulong user in users) data.Users.Add(user);
        data.Data["ruinanet"] = "1"; data.Data["protocol"] = "R48"; data.Data["version"] = "1.0";
        Fake.Lobbies[id] = data;
        return new Lobby { Id = id };
    }
    static ConnectionInfo Info(ulong user, ConnectionState state)
    { return new ConnectionInfo { Identity = new NetIdentity { SteamId = user, IsSteamId = true }, State = state }; }
    static void Message(SteamLink.HostSocket socket, Connection connection, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text); IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, memory, bytes.Length); socket.OnMessage(connection, default(NetIdentity), memory, bytes.Length, 1, 1, 0); }
        finally { Marshal.FreeHGlobal(memory); }
        RuinaNetDriver.Drain();
    }
    static void Main()
    {
        SteamClient.IsValid = SteamClient.IsLoggedOn = true; SteamClient.SteamId = 1;
        Lobby room = Lobby(100, 1, 1, 2); Fake.NextLobby = room;
        NetLink.LocalName = "host"; NetLink.RoomName = "library";
        SteamLink.CreateRoom(); RuinaNetDriver.Drain();
        Check(Fake.RequestedCapacity == 2, "Steam lobby capacity is two");
        Check(SteamLink.Room.HasValue && Fake.Lobbies[100].FriendsOnly, "created lobby friends only");
        Check(Fake.Lobbies[100].Joinable && Fake.Lobbies[100].Data["version"] == "1.0", "room opens after metadata");
        Check(!NetLink.Running, "lobby alone does not activate gameplay link");
        SteamLink.HostSocket socket = (SteamLink.HostSocket)Fake.Socket;
        Connection c = new Connection { Id = 10 }, stranger = new Connection { Id = 20 };
        socket.OnConnectionChanged(stranger, Info(3, ConnectionState.Connecting));
        Check(Fake.Accepted.Count == 0, "native callbacks queued to main thread");
        RuinaNetDriver.Drain();
        Check(Fake.Closed.Contains(20) && Fake.Accepted.Count == 0, "nonmember rejected");
        socket.OnConnectionChanged(c, Info(2, ConnectionState.Connecting)); RuinaNetDriver.Drain();
        Check(Fake.Accepted.Contains(10), "member accepted");
        socket.OnConnectionChanged(stranger, Info(2, ConnectionState.Connecting)); RuinaNetDriver.Drain();
        Check(!Fake.Accepted.Contains(20), "second connection from same member rejected");
        socket.OnConnectionChanged(c, Info(2, ConnectionState.Connected)); RuinaNetDriver.Drain();
        Check(NetLink.Running && NetLink.UsingSteam && NetLink.IsHost && !NetLink.Linked, "socket awaits protocol handshake");
        Message(socket, c, "RUINANET1 HELLO guest R48 remote-session");
        Check(NetLink.Linked && NetLink.PeerName == "guest", "Steam uses production host handshake");
        Check(Fake.Sent.Exists(delegate(string s) { return s.Contains(" WELCOME host library R48 "); }), "host sends versioned welcome through Steam");
        socket.OnConnectionChanged(c, Info(2,ConnectionState.Connected)); RuinaNetDriver.Drain();
        Check(NetLink.Linked, "duplicate connected callback preserves protocol session");
        List<string> delivered = new List<string>(); NetLink.OnPeerMessage = delivered.Add;
        Message(socket, c, "RUINANET1 REL remote-session 1 CHAT copied-before-release");
        Check(delivered.Count == 1 && delivered[0] == "CHAT copied-before-release", "native memory copied before release; reliable delivery");
        Message(socket, c, "RUINANET1 REL remote-session 1 CHAT duplicate");
        Check(delivered.Count == 1, "duplicate reliable messages delivered once");
        Message(socket, stranger, "RUINANET1 REL remote-session 2 CHAT intruder");
        Check(delivered.Count == 1, "wrong connection cannot inject messages");
        socket.OnMessage(c,default(NetIdentity),IntPtr.Zero,0,1,1,0);
        socket.OnMessage(c,default(NetIdentity),IntPtr.Zero,128*1024+1,1,1,0);
        RuinaNetDriver.Drain(); Check(delivered.Count == 1,"empty and oversized native messages rejected before memory access");
        NetLink.Send("CHAT outgoing");
        Check(Fake.Sent.Exists(delegate(string s) { return s.EndsWith(" CHAT outgoing"); }), "gameplay sends through Steam");
        // Queue an otherwise valid old packet, disconnect, then process it.
        byte[] late = Encoding.UTF8.GetBytes("RUINANET1 REL remote-session 2 CHAT late");
        IntPtr p = Marshal.AllocHGlobal(late.Length);
        try { Marshal.Copy(late,0,p,late.Length); socket.OnMessage(c,default(NetIdentity),p,late.Length,2,2,0); }
        finally { Marshal.FreeHGlobal(p); }
        NetLink.Stop(); RuinaNetDriver.Drain();
        Check(delivered.Count == 1 && !NetLink.Running && !SteamLink.Room.HasValue, "late packet dropped after disconnect");
        Check(Fake.SocketClosed && Fake.Lobbies[100].Left, "disconnect releases socket and lobby");
        // A canceled asynchronous room creation must leave its late result.
        Fake.CreatePending = new TaskCompletionSource<Lobby?>();
        SteamLink.CreateRoom(); Check(SteamLink.Busy, "asynchronous creation pending");
        NetLink.Stop(); Lobby canceled = Lobby(101,1,1); Fake.CreatePending.SetResult(canceled); RuinaNetDriver.Drain();
        Check(!SteamLink.Room.HasValue && Fake.Lobbies[101].Left, "late create completion cannot resurrect room");
        Fake.CreatePending = null;
        // Join as guest, receive owner identity, then exercise welcome/ACK.
        SteamClient.SteamId = 2;
        Lobby guestRoom = Lobby(102,1,1,2); Fake.NextLobby = guestRoom;
        SteamLink.JoinRoom("102"); RuinaNetDriver.Drain();
        Check(Fake.ConnectTarget == 1 && Fake.VirtualPort != 0, "guest connects to lobby owner via relay");
        SteamLink.GuestConnection client = (SteamLink.GuestConnection)Fake.Client;
        client.OnConnectionChanged(Info(1,ConnectionState.Connected)); RuinaNetDriver.Drain();
        Check(NetLink.UsingSteam && !NetLink.IsHost, "guest activates shared protocol");
        Check(Fake.Sent.Exists(delegate(string s) { return s.StartsWith("RUINANET1 HELLO "); }), "guest sends hello");
        NetLink.ReceiveSteam("RUINANET1 WELCOME owner named-room R48 owner-session"); RuinaNetDriver.Drain();
        Check(NetLink.Linked && NetLink.PeerName == "owner" && NetLink.PeerRoomName == "named-room", "guest welcome sets identities");
        Check(Fake.Sent.Contains("RUINANET1 ACK"), "guest acknowledges via Steam");
        // An overlay invitation during a battle only saves it for later.
        BattleSync.Battle = true; SteamFriends.Invite(Lobby(999,3,2,3)); RuinaNetDriver.Drain();
        Check(NetPanel.InviteShown == 0 && SteamLink.PendingInvite == "999" && NetLink.Linked, "battle invite cannot replace link or cover battle");
        BattleSync.Battle = false;
        SteamFriends.Invite(guestRoom); RuinaNetDriver.Drain();
        Check(NetPanel.InviteShown == 1, "invitation displayed outside battle");
        // A migrated owner cannot silently inherit the authoritative host role.
        Fake.Lobbies[102].Owner = 2; SteamLink.Tick();
        Check(!NetLink.Running && !SteamLink.Room.HasValue, "host migration closes session");
        Fake.NextLobby=Lobby(106,1,1,2); SteamLink.JoinRoom("106"); RuinaNetDriver.Drain();
        client=(SteamLink.GuestConnection)Fake.Client; client.OnConnectionChanged(Info(1,ConnectionState.Connected)); RuinaNetDriver.Drain();
        SteamMatchmaking.MemberLeft(Fake.NextLobby.Value,1); RuinaNetDriver.Drain();
        Check(!NetLink.Running && !SteamLink.Room.HasValue,"member leave closes active connection");
        // Different versions, a full room and a stranger masquerading as owner.
        Fake.NextLobby = Lobby(103,1,1,2); Fake.Lobbies[103].Data["version"] = "old";
        SteamLink.JoinRoom("103"); RuinaNetDriver.Drain();
        Check(!SteamLink.Room.HasValue && Fake.Lobbies[103].Left, "incompatible version leaves lobby");
        Fake.NextLobby = Lobby(104,1,1,2,3);
        SteamLink.JoinRoom("104"); RuinaNetDriver.Drain();
        Check(!SteamLink.Room.HasValue && Fake.Lobbies[104].Left, "three-member lobby rejected");
        Fake.NextLobby=Lobby(107,1,1,2); Fake.JoinPending=new TaskCompletionSource<Lobby?>();
        TaskCompletionSource<Lobby?> lateJoin=Fake.JoinPending;
        SteamLink.JoinRoom("107"); Check(SteamLink.Busy,"join can be canceled while pending");
        NetLink.Stop(); Fake.JoinPending=null;
        SteamLink.JoinRoom("107"); RuinaNetDriver.Drain();
        lateJoin.SetResult(Fake.NextLobby); RuinaNetDriver.Drain();
        Check(SteamLink.Room.HasValue && !Fake.Lobbies[107].Left,"late join completion does not leave a retried current lobby");
        NetLink.Stop();
        Fake.NextLobby = Lobby(105,1,1,2);
        SteamLink.JoinRoom("105"); RuinaNetDriver.Drain(); client = (SteamLink.GuestConnection)Fake.Client;
        client.OnConnectionChanged(Info(3,ConnectionState.Connected)); RuinaNetDriver.Drain();
        Check(!NetLink.Running, "guest rejects nonowner connection");
        NetLink.Stop();
        // Switching back to UDP must completely retire old Steam callbacks.
        string udp = NetLink.Host(0);
        Check(NetLink.Running && !NetLink.UsingSteam && !SteamLink.Room.HasValue, "UDP works independently after Steam");
        client.OnConnectionChanged(Info(1,ConnectionState.Connected)); RuinaNetDriver.Drain();
        Check(NetLink.Running && !NetLink.UsingSteam, "late Steam callback cannot replace UDP transport");
        NetLink.Stop();
        BattleSync.Battle=true; SteamLink.CreateRoom();
        Check(!SteamLink.Room.HasValue && !SteamLink.Busy,"cannot create room during local battle");
        BattleSync.Battle=false; CreatureSync.Active=true; SteamLink.JoinRoom("105");
        Check(!SteamLink.Room.HasValue && !SteamLink.Busy,"cannot join during abnormality preparation");
        CreatureSync.Active=false; Room.CoopStage=true; SteamLink.CreateRoom();
        Check(!SteamLink.Room.HasValue && !SteamLink.Busy,"cannot replace reception preparation");
        Room.CoopStage=false;
        SteamLink.JoinRoom("not-a-number"); Check(!SteamLink.Busy,"invalid room id never begins request");
        SteamClient.IsLoggedOn=false; SteamLink.CreateRoom();
        Check(!SteamLink.Busy && !SteamLink.Room.HasValue,"Steam offline leaves UDP available");
        Console.WriteLine("PASS: " + checks + " Steam transport/lifecycle assertions against production SteamLink + NetLink + ReliableChannel.");
    }
}

namespace RuinaNet
{
    static class RuinaNetInitializer { public const string Version = "1.0"; }
    static class BattleSync { public static bool Battle; public static bool InBattleLocal() { return Battle; } }
    static class CreatureSync { public static bool Active; }
    static class Room { public static bool CoopStage; public static void Notice(string s) { } }
    static class NetPanel { public static int InviteShown; public static void SetHint(string s) { } public static void ShowSteamInvite(string s) { InviteShown++; } }
    static class RuinaNetDriver
    {
        static Queue<Action> queue = new Queue<Action>();
        public static void Run(Action a) { queue.Enqueue(a); }
        public static void Drain() { int n=0; while(queue.Count>0) { if (++n>1000) throw new Exception("pump loop"); queue.Dequeue()(); } }
    }
}
namespace UnityEngine { static class Debug { public static void Log(string s) { } public static void LogWarning(string s) { } public static void LogError(string s) { } } }
static class Fake
{
    public class LobbyData { public ulong Owner; public List<ulong> Users=new List<ulong>(); public Dictionary<string,string> Data=new Dictionary<string,string>(); public bool FriendsOnly, Joinable, Left; }
    public static Dictionary<ulong,LobbyData> Lobbies=new Dictionary<ulong,LobbyData>();
    public static Lobby? NextLobby; public static TaskCompletionSource<Lobby?> CreatePending, JoinPending;
    public static int RequestedCapacity, VirtualPort; public static ulong ConnectTarget;
    public static bool SocketClosed; public static SocketManager Socket; public static ConnectionManager Client;
    public static HashSet<uint> Accepted=new HashSet<uint>(), Closed=new HashSet<uint>();
    public static List<string> Sent=new List<string>();
}
namespace Steamworks
{
    public struct SteamId { public ulong Value; public static implicit operator SteamId(ulong n) { return new SteamId {Value=n}; } public static implicit operator ulong(SteamId id) { return id.Value; } public override string ToString() { return Value.ToString(); } }
    public enum Result { OK, Fail }
    public enum ConnectionState { Connecting, Connected, Closed }
    public struct Friend { public SteamId Id; }
    public static class SteamClient { public static bool IsValid, IsLoggedOn; public static SteamId SteamId; public static void Init(uint app,bool async) { IsValid=true; } public static void RunCallbacks() { } }
    public static class SteamFriends { public static event Action<Lobby,SteamId> OnGameLobbyJoinRequested; public static void OpenGameInviteOverlay(SteamId id) { } public static void Invite(Lobby l) { if(OnGameLobbyJoinRequested!=null) OnGameLobbyJoinRequested(l,1); } }
    public static class SteamMatchmaking
    {
        public static event Action<Lobby,Friend> OnLobbyMemberJoined,OnLobbyMemberLeave,OnLobbyMemberDisconnected;
        public static event Action<Lobby,Friend,Friend> OnLobbyMemberKicked;
        public static Task<Lobby?> CreateLobbyAsync(int capacity) { Fake.RequestedCapacity=capacity; return Fake.CreatePending==null?Task.FromResult(Fake.NextLobby):Fake.CreatePending.Task; }
        public static Task<Lobby?> JoinLobbyAsync(SteamId id) { return Fake.JoinPending==null?Task.FromResult(Fake.NextLobby):Fake.JoinPending.Task; }
        public static void MemberLeft(Lobby lobby,SteamId user) { if(OnLobbyMemberLeave!=null)OnLobbyMemberLeave(lobby,new Friend{Id=user}); }
    }
    public static class SteamNetworkingUtils { public static void InitRelayNetworkAccess() { } }
    public static class SteamNetworkingSockets
    {
        public static T CreateRelaySocket<T>(int port) where T:SocketManager,new() { T t=new T(); t.Socket=1; Fake.VirtualPort=port; Fake.Socket=t; return t; }
        public static T ConnectRelay<T>(SteamId owner,int port) where T:ConnectionManager,new() { T t=new T(); t.Connection=new Connection {Id=42}; Fake.VirtualPort=port; Fake.ConnectTarget=owner; Fake.Client=t; return t; }
    }
    public class SocketManager
    {
        public uint Socket;
        public virtual void OnConnectionChanged(Connection c,ConnectionInfo i) { if(i.State==ConnectionState.Connecting)OnConnecting(c,i);else if(i.State==ConnectionState.Connected)OnConnected(c,i);else OnDisconnected(c,i); }
        public virtual void OnConnecting(Connection c,ConnectionInfo i) { } public virtual void OnConnected(Connection c,ConnectionInfo i) { } public virtual void OnDisconnected(Connection c,ConnectionInfo i) { }
        public virtual void OnMessage(Connection c,NetIdentity i,IntPtr d,int size,long number,long time,int channel) { }
        public void Receive(int max) { } public bool Close() { Fake.SocketClosed=true; return true; }
    }
    public class ConnectionManager
    {
        public Connection Connection;
        public virtual void OnConnectionChanged(ConnectionInfo i) { if(i.State==ConnectionState.Connected)OnConnected(i);else if(i.State==ConnectionState.Closed)OnDisconnected(i); }
        public virtual void OnConnected(ConnectionInfo i) { } public virtual void OnDisconnected(ConnectionInfo i) { }
        public virtual void OnMessage(IntPtr d,int size,long number,long time,int channel) { }
        public void Receive(int max) { } public void Close() { Fake.Closed.Add(Connection.Id); }
    }
}
namespace Steamworks.Data
{
    public enum SendType { Reliable }
    public struct Connection
    {
        public uint Id;
        public Steamworks.Result Accept() { Fake.Accepted.Add(Id); return Steamworks.Result.OK; }
        public bool Close(bool linger,int reason,string message) { Fake.Closed.Add(Id); return true; }
        public Steamworks.Result SendMessage(byte[] data,SendType type) { Fake.Sent.Add(Encoding.UTF8.GetString(data)); return Steamworks.Result.OK; }
    }
    public struct ConnectionInfo { public NetIdentity Identity; public Steamworks.ConnectionState State; }
    public struct NetIdentity { public Steamworks.SteamId SteamId; public bool IsSteamId; }
    public struct Lobby
    {
        public Steamworks.SteamId Id;
        Fake.LobbyData Data { get { return Fake.Lobbies[(ulong)Id]; } }
        public int MemberCount { get { return Data.Users.Count; } }
        public Steamworks.Friend Owner { get { return new Steamworks.Friend { Id=Data.Owner }; } }
        public IEnumerable<Steamworks.Friend> Members { get { foreach(ulong user in Data.Users) yield return new Steamworks.Friend {Id=user}; } }
        public bool IsOwnedBy(Steamworks.SteamId user) { return Data.Owner==(ulong)user; }
        public bool SetFriendsOnly() { Data.FriendsOnly=true; return true; }
        public bool SetJoinable(bool value) { Data.Joinable=value; return true; }
        public bool SetData(string key,string value) { Data.Data[key]=value; return true; }
        public string GetData(string key) { string value; return Data.Data.TryGetValue(key,out value)?value:""; }
        public void Leave() { Data.Left=true; }
    }
}
