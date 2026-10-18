using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UI;

namespace RuinaNet
{
    // Abnormalities never visit BattleSetting. Keep their native initialization,
    // roster restrictions, stories and completion; coordinate them separately.
    internal static class CreatureSync
    {
        public static bool HooksReady;
        private static CreatureBarrier _gate;
        private static StageClassInfo _stage;
        private static int _floor, _level, _eligible, _patrons, _hosts, _oldTemporary;
        private static bool _replay, _initialized, _joined, _offered, _loading;
        private static bool _endPending, _endAuthorized, _releasingEnd;
        private static bool _faultNoted;
        private static float _refresh;
        private static string _error = "";
        private static UIPhase _returnPhase;
        private static int _returnFloor;
        private static readonly HashSet<string> Retired = new HashSet<string>();
        private static readonly MethodInfo EndMethod = typeof(StageController).GetMethod("EndBattlePhase_creature",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        public static bool Active { get { return _gate != null; } }
        public static bool Offered { get { return Active && _offered; } }
        public static bool Preparing { get { return Active && !_offered && _gate.State == CreatureBarrier.Phase.Preparing; } }
        public static bool CanExchange { get { return Active && _initialized && _joined && _gate.Revision > 0; } }
        public static bool Locked { get { return Active && _gate.State != CreatureBarrier.Phase.Preparing; } }
        public static bool LocalReady { get { return Active && _gate.LocalReady; } }
        public static bool PeerReady { get { return Active && _gate.PeerReady; } }
        public static bool CanStart { get { return Preparing && _joined && BattleSync.IsHost && _gate.MatchingReady; } }
        public static int Floor { get { return _floor; } }
        public static int Eligible { get { return _eligible; } }
        public static int Hosts { get { return _hosts; } }
        public static bool IsPatron(int slot) { return (_patrons & (1 << slot)) != 0; }

        private static bool SafeScreen()
        {
            UI.UIController ui = UI.UIController.Instance;
            if (ui == null || !ui.gameObject.activeInHierarchy) return false;
            UIPhase ph = ui.CurrentUIPhase;
            // Story is also the native archives menu containing creature replay.
            // Real story playback deactivates the main UI root (checked above),
            // so allowing this menu does not permit entering during a story.
            return ph == UIPhase.Sephirah || ph == UIPhase.Librarian || ph == UIPhase.Invitation || ph == UIPhase.Story;
        }

        public static bool First(SephirahType floor)
        {
            if (!Room.Linked) return true;
            try
            {
                LibraryFloorModel library = LibraryModel.Instance.GetFloor(floor);
                Begin((int)floor, library == null ? -1 : library.Level, false);
            }
            catch (Exception e) { Abort("读取异想体入口失败：" + e.Message, true); }
            return false;
        }

        public static bool Replay(object slot, bool confirmed)
        {
            if (!Room.Linked || !confirmed) return true;
            try
            {
                Type type = slot.GetType();
                if (Read<bool>(slot, type, "isFinalBattle") || Read<bool>(slot, type, "isKeterFinal"))
                { Room.Notice("楼层解放战暂未接入联机，请先测试普通异想体战"); return false; }
                Begin((int)Read<SephirahType>(slot, type, "sephirah"), Read<int>(slot, type, "stageNum"), true);
            }
            catch (Exception e) { Report("读取异想体重战入口失败：" + e.Message); }
            return false;
        }
        private static T Read<T>(object obj, Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (field == null) throw new MissingFieldException(type.Name, name);
            return (T)field.GetValue(obj);
        }

        private static StageClassInfo Resolve(int floor, int level, bool replay, int expected)
        {
            if (!Enum.IsDefined(typeof(SephirahType), floor) || level < 0) throw new Exception("楼层或挑战编号无效");
            LibraryFloorModel library = LibraryModel.Instance.GetFloor((SephirahType)floor);
            if (library == null || !LibraryModel.Instance.IsOpenedSephirah((SephirahType)floor) ||
                library.Level < 1 || (!replay && library.Level != level) ||
                (replay && library.Level <= level)) throw new Exception("双方楼层进度不匹配，或尚未解锁这场挑战");
            if (!replay && LibraryModel.Instance.CheckCreatureBossBattle(library)) throw new Exception("楼层解放战暂未接入联机");
            FloorLevelXmlInfo entry = FloorLevelXmlList.Instance.GetData((SephirahType)floor, level);
            if (entry == null || (expected >= 0 && entry.stageId != expected)) throw new Exception("异想体关卡编号不匹配");
            StageClassInfo stage = Singleton<StageClassInfoList>.Instance.GetData(entry.stageId);
            if (stage == null || !string.IsNullOrEmpty(stage.workshopID)) throw new Exception("暂只支持原版普通异想体挑战");
            return stage;
        }

        private static void Begin(int floor, int level, bool replay)
        {
            if (Active) { Room.Notice("已有异想体联机会话，请先完成或取消"); return; }
            if (!HooksReady) { Room.Notice("异想体入口补丁未完整加载，本次已阻止启动；请检查日志"); return; }
            if (!BattleSync.IsHost) { Room.Notice("异想体挑战由房主发起，你接受邀请后分配司书"); return; }
            if (!SafeScreen()) { Room.Notice("请先回到图书馆楼层界面"); return; }
            try
            {
                StageClassInfo stage = Resolve(floor, level, replay, -1);
                Setup(Guid.NewGuid().ToString("N"), floor, level, replay, stage);
                Initialize();
                _hosts = CreatureRoster.DefaultHosts(_eligible, _patrons);
                Send("INVITE", floor + " " + level + " " + (replay ? "1" : "0") + " " + stage._id);
                Report("已邀请对方参加异想体" + (replay ? "重战" : "挑战") + "，等待接受");
            }
            catch (Exception e) { Abort("无法准备异想体战：" + e.Message, true); }
        }

        private static void Setup(string token, int floor, int level, bool replay, StageClassInfo stage)
        {
            Loadout.Reset(); Room.ResetStage();
            _returnPhase = UI.UIController.Instance.CurrentUIPhase;
            _returnFloor = (int)UI.UIController.Instance.CurrentSephirah;
            _gate = new CreatureBarrier(token); _floor = floor; _level = level; _replay = replay; _stage = stage;
            _eligible = _patrons = _hosts = 0; _error = ""; _refresh = 0;
            _initialized = _joined = _offered = _loading = _endPending = _endAuthorized = _releasingEnd = _faultNoted = false;
        }
        private static void Initialize()
        {
            LibraryFloorModel library = LibraryModel.Instance.GetFloor((SephirahType)_floor);
            _oldTemporary = library.TemporaryLevel;
            _initialized = true; // Restore temporary state even when native initialization throws.
            FloorSync.Adopt(_floor, true);
            if (_replay) library.SetTemporaryLevel(_level);
            Singleton<StageController>.Instance.InitStageByCreature(_stage, _replay);
            List<UnitBattleDataModel> units = Units();
            if (units.Count == 0 || units.Count > 5) throw new Exception("原版上场名单异常");
            bool binahLocked = _floor == 8 && LibraryModel.Instance.IsBinahLockedInStage(_stage);
            for (int slot = 0; slot < units.Count; slot++)
            {
                UnitBattleDataModel data = units[slot];
                if (data == null || data.unitData == null) throw new Exception("司书资料缺失");
                bool patron = slot == 0 || data.unitData.isSephirah;
                if (patron) _patrons |= 1 << slot;
                // Exactly the same exclusion as both native creature entry paths.
                data.IsAddedBattle = !(binahLocked && data.unitData.isSephirah);
                if (data.IsAddedBattle && !data.isDead) _eligible |= 1 << slot;
            }
            if (_eligible == 0) throw new Exception("当前没有可上场司书");
            Room.CoopStage = true; Room.CoopStageId = _stage._id;
        }
        public static List<UnitBattleDataModel> Units()
        {
            StageLibraryFloorModel floor = Singleton<StageController>.Instance.GetCurrentStageFloorModel();
            return floor == null ? new List<UnitBattleDataModel>() : floor.GetUnitBattleDataList();
        }
        public static bool TryOwner(UnitBattleDataModel data, out bool local)
        {
            local = false;
            if (!CanExchange || data == null) return false;
            int slot = Units().IndexOf(data);
            if (slot < 0 || (_eligible & (1 << slot)) == 0) return false;
            local = ((_hosts & (1 << slot)) != 0) == BattleSync.IsHost;
            return true;
        }
        public static bool OwnSlot(int slot)
        {
            return CanExchange && (_eligible & (1 << slot)) != 0 &&
                (((_hosts & (1 << slot)) != 0) == BattleSync.IsHost);
        }

        public static void Accept()
        {
            if (!Offered || !Room.Linked) return;
            try
            {
                if (!SafeScreen()) throw new Exception("请先返回图书馆楼层界面再接受");
                // Revalidate progress at the moment of accepting the invitation.
                Resolve(_floor, _level, _replay, _stage._id);
                Initialize(); _offered = false; _joined = true;
                Send("ACCEPT", _eligible + " " + _patrons);
            }
            catch (Exception e) { Abort("无法加入异想体战：" + e.Message, true); }
        }
        public static void Cancel() { if (!Locked) Abort("已取消异想体联机", true); }
        public static void Configure()
        {
            if (!Preparing || !_joined) return;
            Unready(); Room.OpenLibrarianConfig();
        }
        public static void Assign(int slot)
        {
            if (!Preparing || !_joined || !BattleSync.IsHost || slot < 0 || slot >= 5 ||
                (_eligible & (1 << slot)) == 0 || IsPatron(slot)) return;
            int hosts = _hosts ^ (1 << slot);
            if (hosts == 0) { Room.Notice("至少保留一名由房主控制的司书"); return; }
            SetRoster(_gate.Revision + 1, _eligible, hosts, _patrons);
            Send("ROSTER", _eligible + " " + _hosts + " " + _patrons);
            Loadout.SendMine();
        }
        private static void SetRoster(int revision, int eligible, int hosts, int patrons)
        {
            if (eligible != _eligible || patrons != _patrons || !CreatureRoster.Valid(eligible, hosts, patrons))
                throw new Exception("双方原版允许上场的名单或专属司书不一致");
            if (!_gate.Configure(revision)) throw new Exception("司书分工版本过期");
            Loadout.Reset(); _hosts = hosts;
        }

        public static void ToggleReady()
        {
            if (!Preparing || !CanExchange) return;
            if (_gate.LocalReady) { Unready(); return; }
            Loadout.SendMine();
            string digest = Loadout.CreatureDigest();
            if (digest == null || !Loadout.ReadyForBattle()) { Room.Notice("正在等待双方配装同步，请稍后确认"); return; }
            _gate.Ready(true, digest); Send("READY", digest);
        }
        private static void Unready()
        {
            if (!Preparing || !_gate.LocalReady) return;
            _gate.Ready(true, null); Send("UNREADY", "");
        }
        public static void SnapshotChanged() { if (Preparing) Unready(); }
        private static string CurrentDigest()
        {
            Loadout.SendMine();
            if (!Loadout.ReadyForBattle()) return null;
            return Loadout.CreatureDigest();
        }
        public static void Start()
        {
            try { StartCore(); }
            catch (Exception e) { Abort("异想体剧情启动失败：" + e.Message, true); }
        }
        private static void StartCore()
        {
            if (!CanStart) return;
            string digest = CurrentDigest();
            if (digest != _gate.LocalDigest) { Unready(); Room.Notice("配装已变化，请双方重新确认"); return; }
            if (!_gate.StartStory(digest)) return;
            Send("STORY", digest); OpenStory();
        }
        private static void OpenStory()
        {
            string token = _gate.Token;
            int revision = _gate.Revision;
            StageStoryInfo story = _replay ? null : _stage.GetStartStory();
            if (story == null || string.IsNullOrWhiteSpace(story.story)) { StoryDone(token, revision); return; }
            bool opened = UI.UIController.Instance.OpenStory(story.story,
                delegate { StoryDone(token, revision); }, false);
            if (!opened) throw new Exception("原版剧情未能打开，本次启动已停止");
        }
        private static void StoryDone(string token, int revision)
        {
            if (!Active || !_gate.Matches(token, revision) || !_gate.StoryDone(true)) return;
            Send("STORYDONE", "");
        }
        public static bool GateLoad()
        {
            if (!Active) return true;
            // Only our final synchronized call may load. Native story callbacks
            // or a second click cannot bypass the ready/commit handshake.
            return _loading && _gate.State == CreatureBarrier.Phase.Launching && Room.Linked;
        }
        private static void Load()
        {
            Room.GoSeen = true; _loading = true;
            try { GlobalGameManager.Instance.LoadBattleScene(); }
            finally { _loading = false; }
        }
        public static void BattleStarted() { if (Active) _gate.EnterBattle(); }

        public static void PublishLoadout(string body)
        {
            if (CanExchange && Preparing) Send("LOADOUT", body);
        }
        private static void Send(string command, string body)
        {
            if (Active && Room.Linked)
                NetLink.Send("CREATURE " + command + " " + _gate.Token + " " + _gate.Revision +
                    (body.Length == 0 ? "" : " " + body));
        }

        public static bool HandleMessage(string[] p)
        {
            if (p.Length < 4 || p[0] != "CREATURE" || !Room.Linked) return true;
            int revision;
            if (!int.TryParse(p[3], out revision)) return true;
            try
            {
                string command = p[1], token = p[2];
                if (command == "INVITE")
                {
                    if (BattleSync.IsHost || Active || Retired.Contains(token) || p.Length != 8 || !HooksReady) return true;
                    Guid parsed; if (!Guid.TryParseExact(token, "N", out parsed) || revision != 0) return true;
                    if (!SafeScreen()) { NetLink.Send("CREATURE CANCEL " + token + " 0"); Report("对方发起异想体战，请先回图书馆楼层界面"); return true; }
                    int floor = int.Parse(p[4]), level = int.Parse(p[5]), id = int.Parse(p[7]);
                    bool replay = p[6] == "1";
                    StageClassInfo stage = Resolve(floor, level, replay, id);
                    Setup(token, floor, level, replay, stage); _offered = true;
                    return true;
                }
                if (!Active || token != _gate.Token) return true;
                if (command == "CANCEL") { Abort("对方已取消异想体联机", false); return true; }
                if (command == "ACCEPT" && BattleSync.IsHost && revision == 0 && !_joined && p.Length == 6)
                {
                    _joined = true;
                    SetRoster(1, int.Parse(p[4]), _hosts, int.Parse(p[5]));
                    Send("ROSTER", _eligible + " " + _hosts + " " + _patrons); Loadout.SendMine(); return true;
                }
                if (command == "ROSTER" && !BattleSync.IsHost && _initialized && _joined && p.Length == 7)
                {
                    if (revision <= _gate.Revision) return true;
                    SetRoster(revision, int.Parse(p[4]), int.Parse(p[5]), int.Parse(p[6]));
                    Loadout.SendMine(); return true;
                }
                if (!_gate.Matches(token, revision) || !CanExchange) return true;
                if (command == "LOADOUT" && p.Length == 15 && Preparing)
                {
                    string[] body = new string[11]; Array.Copy(p, 4, body, 0, 11);
                    int floor, key;
                    if (body[0] != "LOADOUT2" || !int.TryParse(body[1], out floor) || floor != _floor ||
                        !int.TryParse(body[2], out key)) return true;
                    int slot = key - 100000;
                    if (slot < 0 || slot >= 5 || (_eligible & (1 << slot)) == 0 || OwnSlot(slot)) return true;
                    Loadout.HandleMessage(body); return true;
                }
                if (command == "READY" && p.Length == 5) _gate.Ready(false, p[4]);
                else if (command == "UNREADY") _gate.Ready(false, null);
                else if (command == "STORY" && !BattleSync.IsHost && p.Length == 5 && Preparing)
                {
                    if (CurrentDigest() != p[4] || !_gate.StartStory(p[4])) throw new Exception("开战前双方配装校验不一致");
                    OpenStory();
                }
                else if (command == "STORYDONE") _gate.StoryDone(false);
                else if (command == "COMMIT" && !BattleSync.IsHost && p.Length == 5 && _gate.State == CreatureBarrier.Phase.Story)
                {
                    if (CurrentDigest() != p[4] || !_gate.BeginCommit(p[4]) || !_gate.Ack(p[4]))
                        throw new Exception("剧情结束后的开战校验失败");
                    Send("ACK", p[4]);
                }
                else if (command == "ACK" && BattleSync.IsHost && p.Length == 5) _gate.Ack(p[4]);
                else if (command == "GO" && !BattleSync.IsHost && p.Length == 5 && _gate.State == CreatureBarrier.Phase.Commit)
                {
                    if (CurrentDigest() != p[4] || !_gate.Launch(p[4])) throw new Exception("最终开战确认失败");
                    Load();
                }
                else if (command == "END" && p.Length == 5 && (_gate.State == CreatureBarrier.Phase.Launching ||
                    _gate.State == CreatureBarrier.Phase.Battle || _gate.State == CreatureBarrier.Phase.Ending))
                {
                    if (!_gate.Result(false, int.Parse(p[4]))) Fault("双方异想体战结果不一致，已暂停结算，请保留日志");
                }
                else if (command == "ENDGO" && !BattleSync.IsHost && p.Length == 5)
                {
                    if (_gate.Finish(int.Parse(p[4]))) _endAuthorized = true;
                }
            }
            catch (Exception e)
            {
                if (Active) Abort("异想体联机停止：" + e.Message, true);
                else if (!BattleSync.IsHost && p[1] == "INVITE")
                { NetLink.Send("CREATURE CANCEL " + p[2] + " 0"); Report("无法接受异想体邀请：" + e.Message); }
            }
            return true;
        }

        public static void Tick(float dt)
        {
            try { TickCore(dt); }
            catch (Exception e) { Abort("异想体联机流程失败：" + e.GetBaseException().Message, true); }
        }
        private static void TickCore(float dt)
        {
            if (!Active) { CreaturePanel.Hide(); return; }
            if (!Room.Linked)
            {
                if (!Room.GoSeen) { Abort("连接断开，异想体联机已取消", false); return; }
                Fault("连接断开，已暂停异想体结算；请退出战斗后重试");
            }
            if (_initialized && !Room.GoSeen && FloorSync.Local() != _floor) FloorSync.Adopt(_floor, true);
            _refresh += dt;
            if (_refresh >= 0.3f)
            {
                _refresh = 0;
                if (Preparing && _gate.LocalReady && Loadout.CreatureDigest() != _gate.LocalDigest) Unready();
                CreaturePanel.Refresh();
            }
            if (_gate.State == CreatureBarrier.Phase.Story && BattleSync.IsHost && _gate.LocalStoryDone && _gate.PeerStoryDone)
            {
                string digest = CurrentDigest();
                if (digest != _gate.LocalDigest) { Abort("剧情期间配装发生变化，请重新发起挑战", true); return; }
                if (_gate.BeginCommit(digest)) Send("COMMIT", digest);
            }
            if (_gate.State == CreatureBarrier.Phase.Commit && BattleSync.IsHost && _gate.CommitAck)
            {
                string digest = CurrentDigest();
                if (digest != _gate.LocalDigest) { Abort("最终配装校验失败，请重新准备", true); return; }
                if (_gate.Launch(digest)) { Send("GO", digest); Load(); }
            }
            if (BattleSync.IsHost && _gate.State == CreatureBarrier.Phase.Ending &&
                _gate.LocalResult >= 0 && _gate.LocalResult == _gate.PeerResult)
            {
                int result = _gate.LocalResult;
                if (_gate.Finish(result)) { Send("ENDGO", result.ToString()); _endAuthorized = true; }
            }
            if (_endAuthorized && _endPending)
            {
                _endAuthorized = false; _releasingEnd = true;
                try { EndMethod.Invoke(Singleton<StageController>.Instance, null); }
                catch (Exception e) { Fault("原版结算失败：" + e.GetBaseException().Message); }
                finally { _releasingEnd = false; }
                if (Active && _gate.State == CreatureBarrier.Phase.Finished) Cleanup();
            }
        }

        public static bool GateEnd()
        {
            if (!Active || _releasingEnd) return true;
            if (_gate.State == CreatureBarrier.Phase.Fault || !Room.Linked) return false;
            StageModel stage = Singleton<StageController>.Instance.GetStageModel();
            if (stage.GetFrontAvailableWave() != null && stage.GetFrontAvailableFloor() != null) return true;
            if (!_endPending && _gate.State != CreatureBarrier.Phase.Fault)
            {
                _endPending = true;
                int result = stage.GetFrontAvailableWave() == null ? 1 : 0;
                if (!_gate.Result(true, result)) { Fault("双方战斗结果不一致，已暂停结算，请保留日志"); return false; }
                Send("END", result.ToString());
                Report("异想体战已结束，等待双方结果一致后进行原版结算");
            }
            return false;
        }
        public static void OnGameOver()
        {
            if (!Active || _releasingEnd) return;
            // GameOver is the native give-up/exit path. CloseBattleScene is ALSO
            // called before normal EndBattlePhase_creature, so it must retain
            // the session and its replicated roster until the result barrier.
            Send("CANCEL", ""); Cleanup();
        }
        private static void Abort(string message, bool send)
        {
            Report(message);
            if (!Active) return;
            if (send) Send("CANCEL", "");
            if (Room.GoSeen) { Fault(message + "；请退出本场战斗"); return; }
            Cleanup();
        }
        private static void Fault(string message)
        {
            if (!Active) return;
            _gate.Fail(); _error = message;
            if (!_faultNoted) { _faultNoted = true; Report(message); }
        }
        private static void Cleanup()
        {
            if (!Active) return;
            Retired.Add(_gate.Token);
            bool beforeLoad = !Room.GoSeen;
            try
            {
                Loadout.Reset(); Room.ResetStage();
                if (_initialized)
                    LibraryModel.Instance.GetFloor((SephirahType)_floor).SetTemporaryLevel(_oldTemporary);
                if (beforeLoad)
                {
                    // InitStageByCreature leaves battleState=Setting. Cancelling
                    // must not leave a live, half-prepared creature run behind.
                    Singleton<StageController>.Instance.battleState = StageController.BattleState.None;
                    GameSceneManager.Instance.ActivateUIController(false);
                    FloorSync.Adopt(_returnFloor, true);
                    UI.UIController.Instance.CallUIPhase(_returnPhase);
                }
            }
            finally
            {
                _gate = null; _stage = null; _initialized = _joined = false;
                CreaturePanel.Hide();
            }
        }
        public static string Title { get { return FloorSync.Name(_floor) + " · 异想体" + (_replay ? "重战" : "挑战") + " #" + (_stage == null ? -1 : _stage._id); } }
        public static string Status
        {
            get
            {
                if (!Active) return "";
                if (_error.Length > 0) return _error;
                if (_offered) return ChatWindow.PeerName() + " 邀请你参加同一场异想体战";
                if (!_joined || _gate.Revision < 1) return "等待对方接受邀请并核对上场名单……";
                switch (_gate.State)
                {
                    case CreatureBarrier.Phase.Preparing:
                        return "你：" + (_gate.LocalReady ? "已确认" : "未确认") + "    对方：" + (_gate.PeerReady ? "已确认" : "未确认") +
                            (_gate.LocalReady && _gate.PeerReady && !_gate.MatchingReady ? "\n配装校验不同，请重新确认" :
                            (CreatureRoster.Count(_eligible) == 1 ? "\n本场原版仅允许一名司书；客机观战，仍需确认准备" : "\n确认后由房主开始；专属司书由房主控制"));
                    case CreatureBarrier.Phase.Story: return "等待双方观看完剧情……";
                    case CreatureBarrier.Phase.Commit: return "正在核对最终配装并同步进入战斗……";
                    case CreatureBarrier.Phase.Ending: return "等待双方战斗结果一致后结算……";
                    default: return "";
                }
            }
        }
        public static bool ShowPanel
        {
            get { return Active && !Room.InBookScreen() && !Room.GoSeen &&
                (_gate.State == CreatureBarrier.Phase.Preparing || _gate.State == CreatureBarrier.Phase.Commit ||
                 (_gate.State == CreatureBarrier.Phase.Story && _gate.LocalStoryDone)); }
        }
        private static void Report(string message)
        {
            Debug.Log("[RuinaNet][creature] " + message);
            ChatWindow.AddLine("系统", message);
        }
    }
}
