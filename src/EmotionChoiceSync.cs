using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace RuinaNet
{
    /// <summary>
    /// Host-authoritative emotion-level reward selection.
    ///
    /// LevelUpUI builds its candidate list independently on both simulations.
    /// Synchronising the EGO hand therefore cannot make the two abnormality-card
    /// popups agree.  The host sends the choice that actually passed through the
    /// popup; the client substitutes that XML object into one local slot and lets
    /// the vanilla UI finish its own animation and phase transition.
    /// </summary>
    internal static class EmotionChoiceSync
    {
        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo SelectedPassive = typeof(LevelUpUI).GetField("_selectedCard", Hidden);
        private static readonly FieldInfo PassiveCard = typeof(EmotionPassiveCardUI).GetField("_card", Hidden);

        private static int _nextSeq;
        private static int _hostSeq = -1;
        private static string _hostWire = "";
        private static bool _hostAcked;
        private static float _resend;
        private static string _lastAnnouncedKey = "";
        private static float _lastAnnouncedAt = -10f;

        private static int _pendingSeq = -1;
        private static string _pendingKind = "";
        private static int _pendingFloor;
        private static int _pendingId;
        private static int _pendingTarget;
        private static int _pendingRound = -1;
        private static int _lastAppliedSeq = -1;
        private static float _lastBlockedNotice = -10f;
        private static float _pendingAge;
        private static float _retry;

        private const float RetryGap = 0.25f;
        private const float ApplyTimeout = 10f;

        public static bool ApplyingRemote { get; private set; }

        public static void Reset()
        {
            _nextSeq = 0;
            _hostSeq = -1;
            _hostWire = "";
            _hostAcked = false;
            _resend = 0f;
            _lastAnnouncedKey = "";
            _lastAnnouncedAt = -10f;
            _pendingSeq = -1;
            _pendingKind = "";
            _pendingFloor = 0;
            _pendingId = 0;
            _pendingTarget = -1;
            _pendingRound = -1;
            _lastAppliedSeq = -1;
            _pendingAge = 0f;
            _retry = 0f;
            _lastBlockedNotice = -10f;
            ApplyingRemote = false;
        }

        public static bool AllowLocalChoice()
        {
            if (!BattleSync.Active || BattleSync.IsHost || ApplyingRemote) return true;
            if (Time.unscaledTime - _lastBlockedNotice > 1f)
            {
                _lastBlockedNotice = Time.unscaledTime;
                Room.Notice("异想体书页由房主选择，正在等待房主…");
            }
            return false;
        }

        public static EmotionCardXmlInfo Selected(LevelUpUI ui)
        {
            return ui == null || SelectedPassive == null ? null : SelectedPassive.GetValue(ui) as EmotionCardXmlInfo;
        }

        public static void HostPickedPassive(EmotionCardXmlInfo card, BattleUnitModel target)
        {
            if (!BattleSync.Active || !BattleSync.IsHost || ApplyingRemote || card == null) return;
            StageLibraryFloorModel floor = Singleton<StageController>.Instance.GetCurrentStageFloorModel();
            int sephirah = floor == null ? 0 : (int)floor.Sephirah;
            int targetWire = target == null ? -1 : NetState.WireIndex(target);
            Announce("P", sephirah, card.id, targetWire);
        }

        public static void HostPickedEgo(EmotionEgoXmlInfo card)
        {
            if (!BattleSync.Active || !BattleSync.IsHost || ApplyingRemote || card == null) return;
            Announce("E", (int)card.Sephirah, card.id, -1);
        }

        private static void Announce(string kind, int floor, int id, int target)
        {
            string eventKey = kind + ":" + floor.ToString(CultureInfo.InvariantCulture) + ":" +
                              id.ToString(CultureInfo.InvariantCulture) + ":" + target.ToString(CultureInfo.InvariantCulture);
            // The game exposes two EGO selection callbacks and some UI routes
            // invoke both for one click.  Treat that callback pair as one choice.
            if (_lastAnnouncedKey == eventKey && Time.unscaledTime - _lastAnnouncedAt < 0.5f) return;
            _lastAnnouncedKey = eventKey;
            _lastAnnouncedAt = Time.unscaledTime;
            _hostSeq = _nextSeq++;
            _hostAcked = false;
            _hostWire = "EMOTION " + BattleSync.Round.ToString(CultureInfo.InvariantCulture) + " " +
                        _hostSeq.ToString(CultureInfo.InvariantCulture) + " " + kind + " " +
                        floor.ToString(CultureInfo.InvariantCulture) + " " + id.ToString(CultureInfo.InvariantCulture) + " " +
                        target.ToString(CultureInfo.InvariantCulture);
            _resend = 0f;
            NetLink.Send(_hostWire);
            Debug.Log("[RuinaNet][emotion] host chose " + kind + " " + floor + ":" + id + " target=" + target);
        }

        public static bool HandleMessage(string[] p)
        {
            if (p == null || p.Length == 0) return false;
            if (p[0] == "EMOTIONACK")
            {
                int round, seq;
                if (p.Length > 2 && int.TryParse(p[1], out round) && int.TryParse(p[2], out seq) &&
                    BattleSync.IsHost && round == BattleSync.Round && seq == _hostSeq)
                {
                    _hostAcked = true;
                    Debug.Log("[RuinaNet][emotion] client applied choice " + seq);
                }
                return true;
            }
            if (p[0] == "EMOTIONNAK")
            {
                int round, seq;
                if (p.Length > 2 && int.TryParse(p[1], out round) && int.TryParse(p[2], out seq) &&
                    BattleSync.IsHost && round == BattleSync.Round && seq == _hostSeq)
                {
                    _hostAcked = true;
                    Room.Notice("客机未能应用本次异想体书页，已停止重发；本回合可继续进行");
                    Debug.LogWarning("[RuinaNet][emotion] client rejected choice " + seq +
                                     (p.Length > 3 ? " reason=" + p[3] : ""));
                }
                return true;
            }
            if (p[0] != "EMOTION") return false;
            int roundIn, incoming, floor, id, target;
            if (p.Length < 7 || !int.TryParse(p[1], out roundIn) || !int.TryParse(p[2], out incoming) ||
                !int.TryParse(p[4], out floor) || !int.TryParse(p[5], out id) ||
                !int.TryParse(p[6], out target)) return true;
            if (BattleSync.IsHost) return true;
            if (roundIn != BattleSync.Round) return true;
            if (incoming <= _lastAppliedSeq)
            {
                SendAck(roundIn, incoming);
                return true;
            }
            if (incoming < _pendingSeq) return true;
            _pendingRound = roundIn;
            _pendingSeq = incoming;
            _pendingKind = p[3];
            _pendingFloor = floor;
            _pendingId = id;
            _pendingTarget = target;
            _pendingAge = 0f;
            _retry = RetryGap;
            _lastBlockedNotice = -10f;
            Debug.Log("[RuinaNet][emotion] queued host choice " + incoming + " " + p[3] + " " + floor + ":" + id);
            return true;
        }

        public static void Tick(float dt)
        {
            if (!BattleSync.Active) return;
            if (BattleSync.IsHost)
            {
                if (_hostSeq < 0 || _hostAcked || string.IsNullOrEmpty(_hostWire)) return;
                _resend += dt;
                if (_resend >= 0.5f)
                {
                    _resend = 0f;
                    NetLink.Send(_hostWire);
                }
                return;
            }
            if (_pendingSeq < 0) return;
            _pendingAge += dt;
            _retry += dt;
            if (_pendingAge >= ApplyTimeout)
            {
                FailPending("timeout");
                return;
            }
            if (_retry < RetryGap) return;
            _retry = 0f;
            TryApplyPending();
        }

        private static void TryApplyPending()
        {
            try
            {
                SephirahType floor = (SephirahType)_pendingFloor;
                StageController controller = Singleton<StageController>.Instance;
                StageLibraryFloorModel floorModel = controller == null ? null : controller.GetCurrentStageFloorModel();
                if (floorModel == null || floorModel.Sephirah != floor) return;

                BattleManagerUI manager = SingletonBehavior<BattleManagerUI>.Instance;
                LevelUpUI ui = manager == null ? null : manager.ui_levelup;
                bool popupAlive = ui != null && ui.gameObject != null && ui.gameObject.activeInHierarchy;
                // The packet commonly arrives a little before the client's own
                // animation opens the popup.  Give vanilla that chance first;
                // direct model application is the recovery path for a popup that
                // never appeared or was already torn down.
                if (!popupAlive && _pendingAge < 2f) return;
                ApplyingRemote = true;
                if (_pendingKind == "P")
                {
                    EmotionCardXmlInfo info = Singleton<EmotionCardXmlList>.Instance.GetData(_pendingId, floor);
                    if (info == null) return;
                    BattleUnitModel selectedTarget = null;
                    if (info.TargetType == EmotionTargetType.SelectOne)
                    {
                        selectedTarget = NetState.Find(_pendingTarget);
                        // Do not start the vanilla selection routine until its
                        // target exists locally.  Starting it and returning here
                        // would close half the popup and make a retry unsafe.
                        if (selectedTarget == null) return;
                    }
                    bool usedPopup = false;
                    // Targeted abnormality pages are special: OnSelectPassive starts
                    // a coroutine that creates one AbCardSelector per librarian only
                    // after a scaled 0.1 second wait. Calling OnClickTargetUnit in the
                    // same network tick therefore turns off zero selectors, then the
                    // coroutine creates them afterwards and they remain forever.
                    // Apply the already-authoritative host target directly instead.
                    if (info.TargetType == EmotionTargetType.SelectOne)
                    {
                        floorModel.OnPickPassiveCard(info, selectedTarget);
                        ClearTargetSelectors();
                    }
                    else if (popupAlive && ui.candidates != null && ui.candidates.Length > 0)
                    {
                        EmotionPassiveCardUI slot = null;
                        for (int i = 0; i < ui.candidates.Length; i++)
                        {
                            if (ui.candidates[i] != null && ui.candidates[i].gameObject.activeInHierarchy)
                            {
                                slot = ui.candidates[i];
                                break;
                            }
                        }
                        if (slot == null) slot = ui.candidates[0];
                        if (slot != null && PassiveCard != null)
                        {
                            PassiveCard.SetValue(slot, info);
                            ui.OnSelectPassive(slot);
                            usedPopup = true;
                        }
                    }
                    if (info.TargetType != EmotionTargetType.SelectOne && !usedPopup)
                        floorModel.OnPickPassiveCard(info, selectedTarget);
                }
                else if (_pendingKind == "E")
                {
                    EmotionEgoXmlInfo info = Singleton<EmotionEgoXmlList>.Instance.GetData(_pendingId, floor);
                    if (info == null) return;
                    if (popupAlive && ui.egoSlotList != null && ui.egoSlotList.Length > 0 && ui.egoSlotList[0] != null)
                    {
                        ui.egoSlotList[0].SetEgoCardForPopup(info);
                        ui.OnSelectEgoCard(ui.egoSlotList[0]);
                    }
                    else floorModel.OnPickEgoCard(info);
                }
                else return;

                // A replayed choice enters LevelUpUI from the network tick rather
                // than from its normal pointer event. Vanilla starts a scaled-time
                // fade coroutine here; if the battle is paused by the popup that
                // coroutine never reaches SetRootCanvas(false), leaving the EGO
                // layer visible and intercepting every hand click. The model choice
                // above is synchronous, so the client can safely close the canvas
                // immediately and release all UI raycasts.
                if (popupAlive) ClosePopup(ui);

                int applied = _pendingSeq;
                _lastAppliedSeq = applied;
                _pendingSeq = -1;
                _pendingKind = "";
                SendAck(_pendingRound, applied);
                Debug.Log("[RuinaNet][emotion] applied host choice " + applied);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][emotion] apply deferred: " + e.Message);
            }
            finally
            {
                ApplyingRemote = false;
            }
        }

        private static void ClosePopup(LevelUpUI ui)
        {
            if (ui == null) return;
            try
            {
                DisableGroup(ui.cardSelectionGroup);
                DisableGroup(ui.cardHidingGroup);
                DisableGroup(ui.needSelectAbGroup);
                ClearTargetSelectors();
                ui.SetRootCanvas(false);

                if (ui.candidates != null)
                    for (int i = 0; i < ui.candidates.Length; i++)
                        if (ui.candidates[i] != null) ui.candidates[i].gameObject.SetActive(false);
                if (ui.egoSlotList != null)
                    for (int i = 0; i < ui.egoSlotList.Length; i++)
                        if (ui.egoSlotList[i] != null) ui.egoSlotList[i].gameObject.SetActive(false);

                Debug.Log("[RuinaNet][emotion] closed replayed choice popup on client");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][emotion] popup close failed: " + e.Message);
            }
        }

        private static void ClearTargetSelectors()
        {
            try
            {
                BattleObjectManager manager = BattleObjectManager.instance;
                if (manager == null) return;
                System.Collections.Generic.List<BattleUnitModel> units = manager.GetAliveList(Faction.Player);
                if (units == null) return;
                for (int i = 0; i < units.Count; i++)
                {
                    BattleUnitModel unit = units[i];
                    if (unit == null || unit.view == null || unit.view.abCardSelector == null) continue;
                    AbCardSelector selector = unit.view.abCardSelector;
                    if (selector.isInitialized) selector.TurnOffUI();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][emotion] target marker cleanup failed: " + e.Message);
            }
        }

        private static void DisableGroup(UnityEngine.CanvasGroup group)
        {
            if (group == null) return;
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        private static void SendAck(int round, int seq)
        {
            NetLink.Send("EMOTIONACK " + round.ToString(CultureInfo.InvariantCulture) + " " +
                         seq.ToString(CultureInfo.InvariantCulture));
        }

        private static void FailPending(string reason)
        {
            int round = _pendingRound;
            int seq = _pendingSeq;
            _pendingSeq = -1;
            _pendingKind = "";
            NetLink.Send("EMOTIONNAK " + round.ToString(CultureInfo.InvariantCulture) + " " +
                         seq.ToString(CultureInfo.InvariantCulture) + " " + reason);
            Room.Notice("未能应用房主选择的异想体书页，已记录并继续战斗");
            Debug.LogWarning("[RuinaNet][emotion] failed host choice " + seq + " reason=" + reason);
        }
    }
}
