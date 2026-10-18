using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UI;

namespace RuinaNet
{
    /// <summary>
    /// Stage 3: the co-op room itself.
    ///
    /// Three jobs, all of them about the window between "we are both looking at
    /// the reception screen" and "the battle is running":
    ///
    ///   1. How many librarians each side drives.  The game picks the line-up
    ///      itself (StageController assigns IsAddedBattle to the first
    ///      wave.AvailableUnitNumber librarians of the floor, in list order), so
    ///      the two machines already agree on who is on the field and in what
    ///      order -- all the room has to fix is where the split falls.
    ///      The host owns HostCount/PeerCount and restates them once a second.
    ///
    ///   2. The ready / go handshake.  Nobody enters the battle until the peer
    ///      says READY and the host presses 开始战斗, at which point the host
    ///      sends GO and both machines load the battle scene.
    ///
    ///   3. A door into the official deck screen, so both players can fix their
    ///      books without us reimplementing it.
    ///
    /// Everything is inert unless a link is up AND the reception we are sitting
    /// on is a co-op one (CoopStage), so connecting to a friend and then playing
    /// a solo reception is untouched.
    /// </summary>
    internal static class Room
    {
        // ---------------- how the field is split ----------------
        public static int HostCount = 2;
        public static int PeerCount = 2;

        private const int MinCount = 1;
        private const int HardMax = 5;
        private const int FallbackMax = 4;

        private static int _maxCount = FallbackMax;
        private static float _maxCheckedAt = -60f;

        /// <summary>The host pressed 开始战斗 before the peer was ready.</summary>
        private static bool _startPressed;
        /// <summary>The host already told the peer the fallback split.</summary>
        private static bool _splitSent;
        /// <summary>Which phase 配置司书 was opened from.</summary>
        private static UIPhase _returnPhase = UIPhase.BattleSetting;

        // ---------------- handshake ----------------
        /// <summary>True once the peer has confirmed it joined this reception.</summary>
        public static bool CoopStage;
        /// <summary>Stage id the peer actually joined; -1 when none.</summary>
        public static int CoopStageId = -1;
        public static bool LocalReady;
        public static bool PeerReady;
        /// <summary>The host has pressed 开始战斗; the battle scene may now load.</summary>
        public static bool GoSeen;
        private static float _noticeAt = -60f;
        private const float NoticeGap = 0.6f;

        public static bool Linked { get { return NetLink.Running && NetLink.Linked; } }

        /// <summary>
        /// Librarians this machine drives (display + ownership).
        ///
        /// Once either player has ticked librarians on the 开启舞台 screen the real
        /// numbers come from Lineup -- each side picks its own half, so the split
        /// is whatever was picked.  The host's typed numbers remain only as the
        /// fallback for a room where nobody picked anything.
        /// </summary>
        public static int MyCount
        {
            get
            {
                int picked = BattleSync.IsHost ? Lineup.MyCount : Lineup.PeerCount;
                if (picked > 0) return picked;
                return BattleSync.IsHost ? HostCount : PeerCount;
            }
        }

        /// <summary>Librarians the other machine drives.</summary>
        public static int OtherCount
        {
            get
            {
                int picked = BattleSync.IsHost ? Lineup.PeerCount : Lineup.MyCount;
                if (picked > 0) return picked;
                return BattleSync.IsHost ? PeerCount : HostCount;
            }
        }

        /// <summary>First index in the alive player list this machine owns.</summary>
        public static int MyStart { get { return BattleSync.IsHost ? 0 : HostCount; } }

        public static bool IsHost { get { return BattleSync.IsHost; } }

        /// <summary>
        /// True while the game is showing 开启舞台 -- the one screen where the
        /// room toolbar means anything.  Anywhere else the buttons would just be
        /// clutter (and 配置司书 used to be reachable from the invitation list,
        /// which only led people into the dead end below).
        /// </summary>
        public static bool OnCoopScreen()
        {
            try
            {
                UI.UIController ui = UI.UIController.Instance;
                return ui != null && ui.CurrentUIPhase == UIPhase.BattleSetting;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Per-side cap = librarians the current floor actually has.  Cached for a
        /// second: it is read every frame by the toolbar and the floor model walks
        /// a list.
        /// </summary>
        public static int MaxCount
        {
            get
            {
                if (Time.unscaledTime - _maxCheckedAt < 1f) return _maxCount;
                _maxCheckedAt = Time.unscaledTime;
                try
                {
                    StageController sc = Singleton<StageController>.Instance;
                    if (sc != null)
                    {
                        StageLibraryFloorModel floor = sc.GetCurrentStageFloorModel();
                        if (floor != null)
                        {
                            List<UnitBattleDataModel> list = floor.GetUnitBattleDataList();
                            if (list != null && list.Count > 0)
                            {
                                int n = list.Count;
                                if (n < MinCount) n = MinCount;
                                if (n > HardMax) n = HardMax;
                                _maxCount = n;
                                return _maxCount;
                            }
                        }
                    }
                }
                catch (Exception) { }
                return _maxCount;
            }
        }

        public static int Clamp(int v)
        {
            if (v < MinCount) return MinCount;
            int max = MaxCount;
            if (v > max) return max;
            return v;
        }

        /// <summary>Forget everything about the reception we were on.</summary>
        public static void ResetStage()
        {
            CoopStage = false;
            CoopStageId = -1;
            LocalReady = false;
            PeerReady = false;
            GoSeen = false;
            _startPressed = false;
            _splitSent = false;
            Lineup.Clear();
        }

        /// <summary>
        /// A reception is being opened (or joined).  The ready/go handshake always
        /// restarts; the "this one is co-op" flag and the line-up only reset when the
        /// stage is a *different* one.  The game calls this every time the player
        /// walks back onto 开启舞台, so wiping on every visit would erase the picks
        /// the moment somebody went to look at their books and came back.
        /// </summary>
        public static void OnNewReception(int stageId)
        {
            LocalReady = false;
            PeerReady = false;
            GoSeen = false;
            _startPressed = false;
            _splitSent = false;
            if (CoopStageId != stageId)
            {
                Loadout.Reset();
                CoopStage = false;
                Lineup.Clear();
                CoopStageId = stageId;
            }
        }

        // ================= user actions =================

        /// <summary>
        /// Host typed a new number into the 我方 box.  Only the host may do this;
        /// the peer's boxes are disabled anyway, but a stale keystroke could still
        /// land here.
        /// </summary>
        public static void OnMineEdited(string value)
        {
            int v;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { RefreshBar(); return; }
            ApplySplit(Clamp(v), PeerCount, true);
        }

        /// <summary>Host typed a new number into the 对方 box.</summary>
        public static void OnPeerEdited(string value)
        {
            int v;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { RefreshBar(); return; }
            ApplySplit(HostCount, Clamp(v), false);
        }

        private static void ApplySplit(int mine, int other, bool fromMine)
        {
            if (!IsHost) { Notice("人数由房主设定"); RefreshBar(); return; }

            int before = HostCount * 100 + PeerCount;
            HostCount = mine;
            PeerCount = other;
            // Zero on one side with the other untouched still has to be legal, so
            // lift whichever side was NOT typed into back to the minimum.
            if (HostCount + PeerCount < MinCount * 2)
            {
                if (fromMine) PeerCount = MinCount;
                else HostCount = MinCount;
            }
            if (before != HostCount * 100 + PeerCount)
            {
                SendSplit();
                Debug.Log("[RuinaNet][room] split is now " + HostCount + " / " + PeerCount);
                Notice("人数：" + (IsHost ? "你" : "对方") + " " + HostCount + " 位（排在前 " +
                       HostCount + " 名）/ " + (IsHost ? "对方" : "你") + " " + PeerCount + " 位");
            }
            RefreshBar();
        }

        /// <summary>
        /// True while the game's own book/deck screen is up.  The screen is not one
        /// phase: 配置司书 opens Librarian_CardList, and from there the player walks
        /// into the item list, the deck list and the customise screens, each of
        /// which is its own UIPhase.  Matching only Librarian_CardList is what made
        /// the toolbar vanish "sometimes" -- it depended on which page they were on.
        /// </summary>
        public static bool InBookScreen()
        {
            try
            {
                UI.UIController ui = UI.UIController.Instance;
                if (ui == null) return false;
                UIPhase ph = ui.CurrentUIPhase;
                if (ph == UIPhase.Librarian_CardList ||
                    ph == UIPhase.Main_ItemList ||
                    ph == UIPhase.Custom_CoreBook ||
                    ph == UIPhase.Customize_Librarian) return true;
                return false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Leave the book screen.  The game's own 卡组 screen sends ESC to the
        /// floor list (UICardPanel.OnCancel hard-codes UIPhase.Sephirah), so we
        /// give it a button that goes back where the player came from instead.
        /// </summary>
        public static void ReturnToReception()
        {
            try
            {
                if (UI.UIController.Instance == null) { Notice("界面还没准备好"); return; }
                UI.UIController.Instance.CallUIPhase(_returnPhase);
                Debug.Log("[RuinaNet][room] back to " + _returnPhase);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet][room] back to reception failed: " + e);
                Notice("回不去接待界面");
            }
        }

        /// <summary>Toggle "I have finished setting my books".</summary>
        public static void ToggleReady()
        {
            if (CreatureSync.Active) { CreatureSync.ToggleReady(); return; }
            if (!Linked) { Notice("还没连上对方"); return; }
            LocalReady = !LocalReady;
            if (LocalReady) Loadout.SendMine();
            NetLink.Send("READY " + (LocalReady ? "1" : "0"));
            Debug.Log("[RuinaNet][room] local ready = " + LocalReady);
            Notice(LocalReady ? "你已准备完成，等房主开始战斗" : "你取消了准备");
            RefreshBar();
        }

        /// <summary>Open the game's own librarian/book screen.</summary>
        public static void OpenLibrarianConfig()
        {
            try
            {
                if (UI.UIController.Instance == null) { Notice("界面还没准备好"); return; }
                _returnPhase = UI.UIController.Instance.CurrentUIPhase;
                // Never remember the book screen itself as the way back.
                if (_returnPhase == UIPhase.Librarian_CardList) _returnPhase = UIPhase.BattleSetting;
                UI.UIController.Instance.CallUIPhase(UIPhase.Librarian_CardList);
                Debug.Log("[RuinaNet][room] opened the official book screen from " + _returnPhase);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet][room] open book screen failed: " + e);
                Notice("打不开书页界面");
            }
        }

        public static void Notice(string text)
        {
            if (Time.unscaledTime - _noticeAt < NoticeGap) return;
            _noticeAt = Time.unscaledTime;
            Debug.Log("[RuinaNet][room] " + text);
            try { ChatWindow.AddLine("系统", text); } catch (Exception) { }
        }

        // ================= the start gate =================

        /// <summary>
        /// Called from a prefix on GlobalGameManager.LoadBattleScene -- the one
        /// method every "开始战斗" button ends up in.  Returning false means the
        /// press is swallowed, so the host is the only one who can start and only
        /// once the peer has said READY.
        ///
        /// A swallowed press is *remembered*: pressing early used to just show a
        /// notice and do nothing, which looked exactly like a hang.  Now the
        /// press is queued and Tick fires it the moment the peer reports ready.
        /// </summary>
        public static bool GateBattleStart()
        {
            if (CreatureSync.Active) return CreatureSync.GateLoad();
            if (!CoopStage) return true;      // a solo reception behaves normally
            if (GoSeen) return Loadout.ReadyForBattle();
            if (!Linked) return true;

            if (!IsHost)
            {
                if (!_startPressed)
                {
                    _startPressed = true;
                    Notice("这一场由房主按「开始战斗」，你点「准备完成」就行；房主一按就一起进");
                }
                return false;
            }

            if (!PeerReady)
            {
                if (!_startPressed)
                {
                    _startPressed = true;
                    Notice("对方还没点「准备完成」；我记着这一下，他一准备好就自动开打");
                }
                return false;
            }

            return BeginBattle();
        }

        /// <summary>Host only: arm GO, tell the peer, let the load go ahead.</summary>
        private static bool BeginBattle()
        {
            if (!Loadout.ReadyForBattle())
            {
                _startPressed = true;
                return false;
            }
            // Reliable delivery keeps final equipment snapshots ahead of GO.
            // A core-page change immediately followed by Start cannot race it.
            Loadout.SendMine();
            _startPressed = false;
            GoSeen = true;
            NetLink.Send("GO");
            Debug.Log("[RuinaNet][room] peer was ready -- starting the battle and telling it to come along");
            return true;
        }

        /// <summary>Peer received GO: load the battle scene the same way the host did.</summary>
        private static void EnterBattle()
        {
            try
            {
                if (UI.UIController.Instance == null) { Notice("收到开打指令，但界面还没准备好"); return; }
                if (UI.UIController.Instance.CurrentUIPhase != UIPhase.BattleSetting)
                {
                    Notice("房主已经开始了，请回到「开启舞台」再按「开始战斗」");
                    return;
                }
                GlobalGameManager manager = GlobalGameManager.Instance;
                if (manager == null) { Notice("收到开打指令，但游戏还没准备好"); return; }
                manager.LoadBattleScene();
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet][room] enter battle failed: " + e);
            }
        }

        // ================= plumbing =================

        public static void Tick(float dt)
        {
            RefreshBar();
            if (CreatureSync.Active) return;

            if (!Linked)
            {
                if (CoopStage || LocalReady || PeerReady || GoSeen) ResetStage();
                return;
            }

            // The host states the split once, when the peer has actually joined.
            // Restating it every second used to fill the client's log with one
            // identical line per second, and the line-up itself is now carried by
            // Lineup's own heartbeat.
            if (IsHost)
            {
                if (CoopStage && !_splitSent)
                {
                    _splitSent = true;
                    SendSplit();
                }
            }
            else
            {
            }

            // The queued 开始战斗 press: the peer has just reported ready, so the
            // host does now what it could not do then.
            if (IsHost && _startPressed && PeerReady && CoopStage && !GoSeen)
            {
                try
                {
                    GlobalGameManager manager = GlobalGameManager.Instance;
                    if (manager != null && BeginBattle()) manager.LoadBattleScene();
                }
                catch (Exception e)
                {
                    Debug.LogError("[RuinaNet][room] auto start failed: " + e);
                    _startPressed = false;
                }
            }
        }

        private static void SendSplit()
        {
            NetLink.Send("ROOM " + HostCount.ToString(CultureInfo.InvariantCulture) +
                         " " + PeerCount.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Tell the host we took the invitation.</summary>
        public static void SendJoined(int stageId)
        {
            CoopStage = true;
            CoopStageId = stageId;
            NetLink.Send("JOINED " + stageId.ToString(CultureInfo.InvariantCulture));
        }

        private static void RefreshBar()
        {
            try { ChatWindow.RefreshRoomBar(); } catch (Exception) { }
        }

        public static bool HandleMessage(string[] p)
        {
            if (CreatureSync.Active) return true;
            if (p == null || p.Length == 0) return false;

            switch (p[0])
            {
                case "ROOM":
                {
                    if (p.Length < 3) return true;
                    int a;
                    int b;
                    if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out a)) return true;
                    if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) return true;
                    int na = Clamp(a);
                    int nb = Clamp(b);
                    // The host restates this every second -- only speak on change,
                    // otherwise the log is nothing but this one line.
                    bool changed = (na != HostCount || nb != PeerCount);
                    HostCount = na;
                    PeerCount = nb;
                    if (changed)
                    {
                        Debug.Log("[RuinaNet][room] host split the library " + HostCount + " / " + PeerCount);
                        Notice("房主定了人数：他 " + HostCount + " 位（前 " + HostCount + " 名）/ 你 " +
                               PeerCount + " 位 —— 谁是谁看头像上的角标");
                    }
                    RefreshBar();
                    return true;
                }
                case "READY":
                {
                    bool was = PeerReady;
                    PeerReady = p.Length > 1 ? p[1] == "1" : true;
                    if (PeerReady != was) Debug.Log("[RuinaNet][room] peer ready = " + PeerReady);
                    Notice(PeerReady ? "对方已准备完成" : "对方取消了准备");
                    RefreshBar();
                    return true;
                }
                case "JOINED":
                {
                    CoopStage = true;
                    if (p.Length > 1)
                    {
                        int id;
                        if (int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                        {
                            CoopStageId = id;
                        }
                    }
                    Debug.Log("[RuinaNet][room] peer joined the reception -- co-op reception armed");
                    Notice("对方已加入接待，配好书页点「准备完成」");
                    return true;
                }
                case "GO":
                {
                    CoopStage = true;
                    PeerReady = true;
                    GoSeen = true;
                    Debug.Log("[RuinaNet][room] host said GO -- entering the battle");
                    EnterBattle();
                    return true;
                }
            }
            return false;
        }
    }
}
