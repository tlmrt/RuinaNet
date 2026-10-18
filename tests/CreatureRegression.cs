using System;
using System.Collections.Generic;
using System.Reflection;
using RuinaNet;

class CreatureRegression
{
    static int checks;
    static string A = new string('a', 64), B = new string('b', 64);
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
    static void Receive(string command, int rev, string body)
    {
        CreatureSync.HandleMessage(("CREATURE " + command + " " + Token() + " " + rev + (body == "" ? "" : " " + body)).Split(' '));
    }
    static string Token()
    {
        return ((CreatureBarrier)typeof(CreatureSync).GetField("_gate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Token;
    }
    static void Reset()
    {
        if (CreatureSync.Active) CreatureSync.OnGameOver();
        Room.Linked = true; Room.GoSeen = Room.CoopStage = false;
        BattleSync.IsHost = true; CreatureSync.HooksReady = true;
        UI.UIController.Instance = new UI.UIController();
        LibraryModel.Instance = new LibraryModel();
        Singleton<StageController>.Instance = new StageController();
        GlobalGameManager.Instance = new GlobalGameManager();
        NetLink.Sent.Clear(); Loadout.Digest = A;
        Singleton<StageClassInfoList>.Instance.Stage = new StageClassInfo();
    }
    static void PrepareHost()
    {
        Reset(); CreatureSync.First(SephirahType.Keter);
        Receive("ACCEPT", 0, "7 1");
    }
    static void LaunchHost()
    {
        PrepareHost(); CreatureSync.ToggleReady(); Receive("READY", 1, A); CreatureSync.Start();
        Receive("STORYDONE", 1, ""); UI.UIController.Instance.EndStory(); CreatureSync.Tick(0.4f);
        Receive("ACK", 1, A); CreatureSync.Tick(0.4f); CreatureSync.BattleStarted();
    }
    static void Main()
    {
        try { Run(); }
        catch (Exception e) { Console.WriteLine("FAIL after " + checks + ": " + e.GetType().Name + " " + e.Message); Environment.ExitCode = 1; }
    }
    static void Run()
    {
        // Pure barriers include reordered, duplicated and obsolete commands.
        CreatureBarrier gate = new CreatureBarrier("run");
        Check(!gate.Launch(A) && !gate.StartStory(A), "no launch/story before configuration");
        Check(gate.Configure(1) && !gate.Configure(1), "roster revisions increase strictly");
        Check(!gate.Matches("other", 1) && !gate.Matches("run", 0), "stale session/revision rejected");
        Check(!gate.Ready(true, "bad"), "malformed digest rejected");
        gate.Ready(true, A); gate.Ready(false, B);
        Check(!gate.MatchingReady && !gate.StartStory(A), "different loadouts block story");
        gate.Ready(false, A); Check(gate.MatchingReady, "both equal digests ready");
        Check(gate.Configure(2) && !gate.LocalReady && !gate.PeerReady, "ownership change clears both confirmations");
        gate.Ready(true, A); gate.Ready(false, A); Check(gate.StartStory(A), "story begins after equal snapshots");
        Check(!gate.StartStory(A) && !gate.Configure(3) && !gate.Ready(false, B), "story locks configuration and duplicate starts");
        gate.StoryDone(true); Check(!gate.BeginCommit(A), "one story completion cannot commit");
        gate.StoryDone(false); Check(gate.BeginCommit(A), "both story completions allow commit");
        Check(!gate.Launch(A) && !gate.Ack(B), "commit requires matching acknowledgement");
        Check(gate.Ack(A) && gate.Launch(A) && !gate.Launch(A), "launch releases once only");
        gate.EnterBattle(); Check(gate.Result(false, 1) && !gate.Finish(1), "peer-only result cannot grant progress");
        Check(gate.Result(true, 1) && gate.Finish(1) && !gate.Finish(1), "matching result finishes once");
        gate = new CreatureBarrier("new"); gate.Configure(1); gate.Ready(true,A); gate.Ready(false,A);
        gate.StartStory(A); gate.StoryDone(true); gate.StoryDone(false); gate.BeginCommit(A); gate.Ack(A); gate.Launch(A);
        gate.Result(true, 1); Check(!gate.Result(false, 0) && gate.State == CreatureBarrier.Phase.Fault, "win/loss disagreement blocks native progress");
        Check(!gate.Finish(1), "fault cannot finish");
        for (int mask = 1; mask < 32; mask++)
        {
            int patrons = mask & 1, hosts = CreatureRoster.DefaultHosts(mask, patrons);
            Check(CreatureRoster.Valid(mask, hosts, patrons) && hosts != 0, "valid native roster split " + mask);
        }
        Check(CreatureRoster.DefaultHosts(1,1) == 1, "one librarian leaves guest spectating");
        Check(!CreatureRoster.Valid(3,2,1) && !CreatureRoster.Valid(3,4,1), "patron theft and extra units rejected");

        // Real production coordinator, with native UI/network boundaries replaced.
        Reset(); Room.Linked = false;
        Check(CreatureSync.First(SephirahType.Keter) && !CreatureSync.Active, "solo entry remains native");
        Room.Linked = true; BattleSync.IsHost = false;
        Check(!CreatureSync.First(SephirahType.Keter) && !CreatureSync.Active, "guest cannot initiate");
        BattleSync.IsHost = true; CreatureSync.HooksReady = false; CreatureSync.First(SephirahType.Keter);
        Check(!CreatureSync.Active, "missing mandatory hooks fail closed");
        CreatureSync.HooksReady = true; CreatureSync.First(SephirahType.Keter);
        Check(CreatureSync.Active && Room.CoopStage && !Room.GoSeen, "native stage initialized behind separate preparation gate");
        Check(Singleton<StageController>.Instance.InitCalls == 1 && CreatureSync.Eligible == 7, "first challenge keeps native eligible roster");
        CreatureSync.Start(); Check(GlobalGameManager.Instance.LoadCalls == 0, "host cannot load before acceptance");
        Receive("ACCEPT", 0, "7 1");
        Check(CreatureSync.CanExchange && CreatureSync.Hosts == 3, "acceptance enables authoritative ownership and snapshots");
        CreatureSync.Assign(0); Check(CreatureSync.Hosts == 3, "patron cannot be reassigned");
        CreatureSync.ToggleReady(); CreatureSync.Assign(1);
        Check(CreatureSync.Hosts == 1 && !CreatureSync.LocalReady, "assistant reassignment resets confirmation");
        Receive("READY", 1, A); Check(!CreatureSync.PeerReady, "ready from prior ownership revision ignored");
        CreatureSync.ToggleReady(); Receive("READY", 2, B);
        Check(!CreatureSync.CanStart, "host refuses unequal peer digest");
        Receive("READY", 2, A); Check(CreatureSync.CanStart, "equal snapshots enable start");
        CreatureSync.Start(); Check(UI.UIController.Instance.StoryCalls == 1 && GlobalGameManager.Instance.LoadCalls == 0, "first challenge opens story before load");
        Receive("STORYDONE", 2, ""); CreatureSync.Tick(0.4f);
        Check(!NetLink.Sent.Exists(delegate(string s) { return s.Contains(" COMMIT "); }), "host waits for local story too");
        UI.UIController.Instance.EndStory(); CreatureSync.Tick(0.4f);
        Check(NetLink.Sent.Exists(delegate(string s) { return s.Contains(" COMMIT "); }), "both story callbacks produce commit");
        Receive("ACK", 2, B); CreatureSync.Tick(0.4f);
        Check(GlobalGameManager.Instance.LoadCalls == 0, "wrong final ack cannot load");
        Receive("ACK", 2, A); CreatureSync.Tick(0.4f);
        Check(GlobalGameManager.Instance.LoadCalls == 1 && Room.GoSeen, "valid final ack loads once without BattleSetting");
        Receive("ACK", 2, A); CreatureSync.Tick(0.4f);
        Check(GlobalGameManager.Instance.LoadCalls == 1 && !CreatureSync.GateLoad(), "duplicate packets and native callbacks cannot reload");
        CreatureSync.BattleStarted(); Singleton<StageController>.Instance.Stage.Won = true;
        Check(!CreatureSync.GateEnd() && Singleton<StageController>.Instance.EndCalls == 0, "terminal native progression waits for peer");
        Receive("END", 2, "1"); CreatureSync.Tick(0.4f);
        Check(Singleton<StageController>.Instance.EndCalls == 1 && LibraryModel.Instance.Floor.Level == 3, "matching results execute native level-up once");
        Check(!CreatureSync.Active && !Room.GoSeen && !Room.CoopStage, "completed session clears room and ownership");

        // Replay initializes temporary native level and cancellation restores it.
        Reset(); LibraryModel.Instance.Floor.Level = 4; LibraryModel.Instance.Floor.TemporaryLevel = 99;
        // Native story archives / creature replay menu uses UIPhase.Story=4.
        // The real story scene, in contrast, deactivates the main UI root.
        UI.UIController.Instance.CurrentUIPhase = UI.UIPhase.Story;
        CreatureSync.Replay(new ReplaySlot(), true);
        Check(CreatureSync.Active, "replay from native Story archives is accepted");
        Check(Singleton<StageController>.Instance.Replay && LibraryModel.Instance.Floor.TemporaryLevel == 1, "replay uses native temporary level and flag");
        CreatureSync.Cancel(); Check(!CreatureSync.Active && LibraryModel.Instance.Floor.TemporaryLevel == 99, "cancel restores previous temporary level");
        Check(UI.UIController.Instance.CurrentUIPhase == UI.UIPhase.Story, "cancel returns to native replay archives");
        Check(Singleton<StageController>.Instance.battleState == StageController.BattleState.None, "cancel clears unstarted native setting state");
        Reset(); UI.UIController.Instance.CurrentUIPhase = UI.UIPhase.Story;
        CreatureSync.Replay(new ReplaySlot(), true); Receive("ACCEPT", 0, "7 1");
        CreatureSync.ToggleReady(); Receive("READY", 1, A); CreatureSync.Start();
        Check(UI.UIController.Instance.StoryCalls == 0 && GlobalGameManager.Instance.LoadCalls == 0,
            "replay from archives skips first-challenge story and waits for peer");
        Receive("STORYDONE", 1, ""); CreatureSync.Tick(0.4f); Receive("ACK", 1, A); CreatureSync.Tick(0.4f);
        Check(GlobalGameManager.Instance.LoadCalls == 1, "archives replay completes handshake and reaches native load");
        CreatureSync.OnGameOver();
        Reset(); UI.UIController.Instance.CurrentUIPhase = UI.UIPhase.Story;
        UI.UIController.Instance.gameObject.activeInHierarchy = false;
        CreatureSync.Replay(new ReplaySlot(), true);
        Check(!CreatureSync.Active && Singleton<StageController>.Instance.InitCalls == 0,
            "real story playback with inactive main UI cannot initiate replay");
        Reset(); CreatureSync.Replay(new ReplaySlot { isFinalBattle = true }, true);
        Check(!CreatureSync.Active, "floor realization replay explicitly excluded");
        Reset(); BattleSync.IsHost = false; UI.UIController.Instance.CurrentUIPhase = UI.UIPhase.Story;
        string archiveToken = Guid.NewGuid().ToString("N");
        CreatureSync.HandleMessage(("CREATURE INVITE " + archiveToken + " 0 10 1 1 123").Split(' '));
        Check(CreatureSync.Offered, "guest sees replay invitation while browsing Story archives");
        CreatureSync.Accept(); Receive("ROSTER", 1, "7 1 1");
        Check(CreatureSync.CanExchange && Singleton<StageController>.Instance.Replay,
            "guest accepts replay from archives and initializes requested stage");
        CreatureSync.Cancel();
        Reset(); BattleSync.IsHost = false; UI.UIController.Instance.CurrentUIPhase = UI.UIPhase.Story;
        UI.UIController.Instance.gameObject.activeInHierarchy = false;
        archiveToken = Guid.NewGuid().ToString("N");
        CreatureSync.HandleMessage(("CREATURE INVITE " + archiveToken + " 0 10 1 1 123").Split(' '));
        Check(!CreatureSync.Active && Singleton<StageController>.Instance.InitCalls == 0,
            "guest playing a real story cannot be pulled into a replay");
        Reset(); LibraryModel.Instance.LockBinah = true; CreatureSync.First(SephirahType.Binah);
        Check(CreatureSync.Eligible == 6 && !CreatureSync.Units()[0].IsAddedBattle, "native Binah exclusion preserved");
        Receive("ACCEPT", 0, "6 1");
        bool own; Check(CreatureSync.TryOwner(CreatureSync.Units()[1], out own) && own, "assistant spawned as compact index zero still uses roster ownership");
        CreatureSync.Cancel();
        Reset(); CreatureSync.First(SephirahType.Keter); Receive("ACCEPT", 0, "3 1");
        Check(!CreatureSync.Active && GlobalGameManager.Instance.LoadCalls == 0, "mismatched native rosters cancel without loading");

        // Guest only accepts exact host metadata and only loads after its own ack.
        Reset(); BattleSync.IsHost = false;
        string token = Guid.NewGuid().ToString("N");
        CreatureSync.HandleMessage(("CREATURE INVITE " + token + " 0 10 2 0 123").Split(' '));
        Check(CreatureSync.Offered && !Room.CoopStage, "guest invite does not initialize until accepted");
        CreatureSync.Accept(); Receive("ROSTER", 1, "7 1 1");
        Check(CreatureSync.OwnSlot(1) && !CreatureSync.OwnSlot(0), "guest uses host-assigned slots");
        CreatureSync.ToggleReady(); Receive("READY", 1, A); Receive("STORY", 1, A);
        Receive("STORYDONE", 1, ""); UI.UIController.Instance.EndStory(); Receive("COMMIT", 1, A);
        Check(GlobalGameManager.Instance.LoadCalls == 0 && NetLink.Sent.Exists(delegate(string s) { return s.Contains(" ACK "); }), "guest acknowledges but cannot self-launch");
        Receive("GO", 1, A); Check(GlobalGameManager.Instance.LoadCalls == 1, "guest final go loads synchronized stage");
        Receive("GO", 1, A); Receive("COMMIT", 1, A);
        Check(GlobalGameManager.Instance.LoadCalls == 1 && CreatureSync.Status == "", "duplicate go and commit leave live session healthy");
        CreatureSync.BattleStarted(); Singleton<StageController>.Instance.Stage.Won = true;
        CreatureSync.GateEnd(); Receive("END",1,"1"); Receive("ENDGO",1,"1"); CreatureSync.Tick(0.4f);
        Check(!CreatureSync.Active && Singleton<StageController>.Instance.EndCalls == 1, "guest normal native completion releases and cleans up");
        Reset(); token = Guid.NewGuid().ToString("N");
        BattleSync.IsHost = false; LibraryModel.Instance.Floor.Level = 3;
        CreatureSync.HandleMessage(("CREATURE INVITE " + token + " 0 10 2 0 123").Split(' '));
        Check(!CreatureSync.Active && NetLink.Sent.Exists(delegate(string s) { return s.Contains(" CANCEL "); }), "different first-clear progress rejects invitation");
        LaunchHost(); Singleton<StageController>.Instance.Stage.Won = true; CreatureSync.GateEnd(); Receive("END",1,"0"); CreatureSync.Tick(0.4f);
        Check(CreatureSync.Active && Singleton<StageController>.Instance.EndCalls == 0, "divergent battle results never grant progress");
        LaunchHost(); Singleton<StageController>.Instance.Stage.Lost = true;
        CreatureSync.GateEnd(); Receive("END",1,"0"); CreatureSync.Tick(0.4f);
        Check(!CreatureSync.Active && Singleton<StageController>.Instance.EndCalls == 1 && LibraryModel.Instance.Floor.Level == 2,
            "matching defeat exits through native completion without level-up");
        PrepareHost(); CreatureSync.ToggleReady(); Receive("READY",1,A); CreatureSync.Start();
        Receive("CANCEL",1,""); UI.UIController.Instance.EndStory(); CreatureSync.Tick(0.4f);
        Check(!CreatureSync.Active && GlobalGameManager.Instance.LoadCalls == 0, "late story callback after cancellation cannot launch");
        PrepareHost(); CreatureSync.ToggleReady(); Receive("READY",1,A); CreatureSync.Start();
        Receive("STORYDONE",1,""); UI.UIController.Instance.EndStory(); Loadout.Digest = B; CreatureSync.Tick(0.4f);
        Check(!CreatureSync.Active && GlobalGameManager.Instance.LoadCalls == 0, "equipment mutation during story aborts before scene loading");
        Reset(); CreatureSync.First(SephirahType.Keter); Room.Linked = false; CreatureSync.Tick(0.4f);
        Check(!CreatureSync.Active && GlobalGameManager.Instance.LoadCalls == 0, "prelaunch disconnect cancels cleanly");
        LaunchHost(); Room.Linked = false; CreatureSync.Tick(0.4f);
        Check(CreatureSync.Active && !CreatureSync.GateEnd() && Singleton<StageController>.Instance.EndCalls == 0, "battle disconnect cannot silently grant solo progress");
        CreatureSync.OnGameOver();
        Console.WriteLine("PASS: " + checks + " abnormality session assertions against production CreatureBarrier + CreatureSync.");
    }
}

enum SephirahType { Binah = 8, Keter = 10 }
class Singleton<T> where T : new() { public static T Instance = new T(); }
class LorId { public int id = 1; }
class UnitDataModel { public bool isSephirah; }
class UnitBattleDataModel { public UnitDataModel unitData = new UnitDataModel(); public bool IsAddedBattle, isDead = false; }
class StageStoryInfo { public string story = "1_2_3"; }
class StageClassInfo
{
    public int _id = 123; public string workshopID = ""; public bool Story = true;
    public StageStoryInfo GetStartStory() { return Story ? new StageStoryInfo() : null; }
}
class StageClassInfoList { public StageClassInfo Stage = new StageClassInfo(); public StageClassInfo GetData(int id) { return id == Stage._id ? Stage : null; } }
class FloorLevelXmlInfo { public int stageId = 123; }
class FloorLevelXmlList { public static FloorLevelXmlList Instance = new FloorLevelXmlList(); public FloorLevelXmlInfo GetData(SephirahType floor, int level) { return new FloorLevelXmlInfo(); } }
class LibraryFloorModel { public int Level = 2, TemporaryLevel; public void SetTemporaryLevel(int level) { TemporaryLevel = level; } }
class LibraryModel
{
    public static LibraryModel Instance = new LibraryModel(); public LibraryFloorModel Floor = new LibraryFloorModel();
    public bool LockBinah;
    public LibraryFloorModel GetFloor(SephirahType floor) { return Floor; }
    public bool IsOpenedSephirah(SephirahType floor) { return true; }
    public bool CheckCreatureBossBattle(LibraryFloorModel floor) { return false; }
    public bool IsBinahLockedInStage(StageClassInfo stage) { return LockBinah; }
}
class StageLibraryFloorModel
{
    public List<UnitBattleDataModel> Units = new List<UnitBattleDataModel> {
        new UnitBattleDataModel { unitData = new UnitDataModel { isSephirah = true } }, new UnitBattleDataModel(), new UnitBattleDataModel() };
    public List<UnitBattleDataModel> GetUnitBattleDataList() { return Units; }
}
class StageModel
{
    public bool Won, Lost;
    public object GetFrontAvailableWave() { return Won ? null : new object(); }
    public object GetFrontAvailableFloor() { return Lost ? null : new object(); }
}
class StageController
{
    public enum BattleState { None, Battle, Setting }
    public BattleState battleState;
    public int InitCalls, EndCalls; public bool Replay;
    public StageLibraryFloorModel Floor = new StageLibraryFloorModel(); public StageModel Stage = new StageModel();
    public StageLibraryFloorModel GetCurrentStageFloorModel() { return Floor; }
    public StageModel GetStageModel() { return Stage; }
    public void InitStageByCreature(StageClassInfo stage, bool replay) { InitCalls++; Replay = replay; battleState = BattleState.Setting; }
    public void EndBattlePhase_creature()
    {
        if (!CreatureSync.GateEnd()) throw new Exception("native finish not authorized");
        EndCalls++; if (!Replay && Stage.Won) LibraryModel.Instance.Floor.Level++;
        battleState = BattleState.None;
    }
}
class GlobalGameManager
{
    public static GlobalGameManager Instance = new GlobalGameManager(); public int LoadCalls;
    public void LoadBattleScene() { if (!CreatureSync.GateLoad()) throw new Exception("native load not authorized"); LoadCalls++; }
}
class GameSceneManager
{
    public static GameSceneManager Instance = new GameSceneManager();
    public void ActivateUIController(bool reset) { UI.UIController.Instance.gameObject.activeInHierarchy = true; }
}
class ReplaySlot { public bool isFinalBattle, isKeterFinal = false; public SephirahType sephirah = SephirahType.Keter; public int stageNum = 1; }
namespace UI
{
    enum UIPhase { Sephirah = 0, Librarian = 1, Invitation = 3, Story = 4, BattleSetting = 5, Librarian_CardList = 10 }
    class UIController
    {
        public static UIController Instance = new UIController(); public UIPhase CurrentUIPhase;
        public SephirahType CurrentSephirah = SephirahType.Keter;
        public UnityEngine.GameObject gameObject = new UnityEngine.GameObject();
        public int StoryCalls; private Action _end;
        public bool OpenStory(string story, Action end, bool flag) { StoryCalls++; _end = end; return true; }
        public void EndStory() { Action end = _end; _end = null; if (end != null) end(); }
        public void CallUIPhase(UIPhase phase) { CurrentUIPhase = phase; }
    }
}
namespace UnityEngine
{
    static class Debug { public static void Log(object message) { } }
    class GameObject { public bool activeInHierarchy = true; }
}
namespace RuinaNet
{
    static class Room
    {
        public static bool Linked = true, GoSeen, CoopStage; public static int CoopStageId;
        public static void Notice(string text) { }
        public static void ResetStage() { GoSeen = CoopStage = false; }
        public static bool InBookScreen() { return UI.UIController.Instance.CurrentUIPhase == UI.UIPhase.Librarian_CardList; }
        public static void OpenLibrarianConfig() { UI.UIController.Instance.CurrentUIPhase = UI.UIPhase.Librarian_CardList; }
    }
    static class BattleSync { public static bool IsHost = true; }
    static class FloorSync { public static int LocalFloor = 10; public static int Local() { return LocalFloor; } public static bool Adopt(int floor, bool force) { LocalFloor = floor; return true; } public static string Name(int floor) { return floor.ToString(); } }
    static class NetLink { public static List<string> Sent = new List<string>(); public static void Send(string text) { Sent.Add(text); } }
    static class ChatWindow { public static void AddLine(string name, string text) { } public static string PeerName() { return "Peer"; } }
    static class Loadout { public static string Digest; public static void Reset() { } public static void SendMine() { } public static bool ReadyForBattle() { return true; } public static string CreatureDigest() { return Digest; } public static bool HandleMessage(string[] p) { return true; } }
    static class CreaturePanel { public static void Refresh() { } public static void Hide() { } }
}
