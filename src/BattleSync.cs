using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace RuinaNet
{
    /// <summary>
    /// Stage 2: battle synchronisation.
    ///
    /// Two independent halves:
    ///   1. Card phase gate  -- nobody may advance the round until both peers
    ///      have locked in their plays.  Implemented as a Harmony prefix on
    ///      StageController.CompleteApplyingLibrarianCardPhase.
    ///   2. Authoritative dice -- the host rolls, everyone else replays.  The
    ///      roll site is DiceStatCalculator.MakeDiceResult, reached from
    ///      BattleDiceBehavior.RollDice.
    ///
    /// Every entry point returns "act normally" when Active is false, so the
    /// mod is inert in single player.
    /// </summary>
    internal static class BattleSync
    {
        // ---------------- session state ----------------
        // A peer link is live (SetLink) ...
        private static bool _linked;
        // ... when we last heard the peer say "I am in a battle too" ...
        private static float _peerBattleAt = -1f;
        // ... and whether we are inside one ourselves.
        public static bool InThisBattle;
        private static float _battlePingWait;
        private static bool _activeLogged;
        // unscaledTime of the last OnBattleStart, used only as a short grace
        // window before the player-side unit list is populated.
        private static float _battleStartedAt = -1f;
        // Round number of a DONE that arrived before we armed this round; -1 when
        // there is none.  A peer READY never arms our gate: each human has to press
        // start locally.  DONE is the authoritative "peer really submitted" signal.
        private static int _pendingDoneRound = -1;
        private static int _peerReadyRound = -1;

        // The peer restates "I am in a battle" once a second, so its presence is
        // a decaying heartbeat rather than a one-shot flag.  A dropped datagram,
        // a 30 s battle load that starves the heartbeat, or a link that flickers
        // down and straight back up can no longer leave one side permanently
        // invisible to the other -- which is exactly what a one-shot flag did.
        private const float PeerBattleWindow = 4f;

        public static bool PeerInBattle
        {
            get { return _peerBattleAt >= 0f && Time.unscaledTime - _peerBattleAt < PeerBattleWindow; }
        }

        /// <summary>
        /// True only while both machines are actually fighting.  Connecting to a
        /// friend and then walking into a solo reception must change nothing
        /// about that reception, so every co-op path keys off this and not off
        /// the raw link state.
        /// </summary>
        public static bool Active { get { return _linked && InThisBattle && PeerInBattle; } }

        /// <summary>"I am in a battle right now", read straight off the game state.</summary>
        /// <remarks>
        /// Never cached and never stored in a flag the link layer can clear.  The
        /// player-side unit list is the authority; the short grace window after
        /// OnBattleStart only covers the frame or two before those units exist.
        /// </remarks>
        public static bool InBattleLocal()
        {
            if (CreatureSync.Active && !Room.GoSeen) return false;
            if (AliveUnitCount() > 0)
            {
                return true;
            }
            return _battleStartedAt >= 0f && Time.unscaledTime - _battleStartedAt < 60f;
        }

        public static bool IsHost;          // this process owns the authoritative rolls
        public static int MyPlayerIndex;    // 0 or 1: which half of the player units we control

        // ---------------- per-round card gate ----------------
        private static readonly RoundGate Gate = new RoundGate();
        public static int Round { get { return Gate.Round; } }
        public static bool LocalSubmitted;      // we pressed start / submitted our plays
        public static bool PeerSubmitted;       // the other side finished submitting
        public static bool GateArmed { get { return Gate.Armed; } }           // we are blocking the local phase advance
        public static bool PeerCardsArrived;    // the peer's card payload reached us
        public static bool RoundHandled { get { return Gate.Handled; } }        // the gate already fired for this round

        // how long the gate has been blocking, so a dead peer cannot wedge the game
        private static float _gateWait;
        private const float GateTimeout = 5f;
        // Host sends one final authoritative snapshot, then waits until the client
        // confirms it was fully applied before either simulation may advance.
        private static readonly RoundCommitBarrier Commit = new RoundCommitBarrier();
        private static string _commitDigest = "";

        // host side: how often the living field is re-broadcast between two gates
        private static float _vitalsWait;
        private const float VitalsGap = 0.7f;

        // both sides: how often my own card placements are re-published, and what
        // was last sent per unit so an unchanged board costs nothing
        private static float _cardsWait;
        private const float CardsGap = 0.5f;
        private static readonly Dictionary<int, string> _sentCards = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _planningCards = new Dictionary<int, string>();

        // stage-phase edge detection (pure polling, cannot break the screen)
        private static int _lastPhaseInt = -1;
        private static int _lastPeerCardUnit = -1;

        // stages of a battle we care about are driven by the gate below, so no
        // phase polling is needed here.

        // unitIndex -> serialised card slot ("slotOrder|cardId|targetIdx|targetSlot")
        public static readonly Dictionary<int, string> PeerCards = new Dictionary<int, string>();

        // ---------------- dice table ----------------
        // client side: "unitIdx:slotOrder:behaviorIdx" -> FIFO of values handed down
        // by the host.  A clash re-rolls the same behaviour, so one key can hold
        // several values; consumption order matches broadcast order because the
        // reliable channel delivers in order.
        public static readonly Dictionary<string, Queue<int>> HostRollQueues = new Dictionary<string, Queue<int>>();
        // client side: rolls made locally before the host value arrived, per key,
        // awaiting correction while the die is still spinning.
        private static readonly Dictionary<string, Queue<BattleDiceBehavior>> _awaitingDice = new Dictionary<string, Queue<BattleDiceBehavior>>();
        // client side: behaviours whose MakeDiceResult already took the host value
        private static readonly HashSet<BattleDiceBehavior> _consumedHostValue = new HashSet<BattleDiceBehavior>();
        // host side: how many rolls we have announced this battle (diagnostics only)
        public static int HostRollCount;
        // how many rolls we had to improvise locally because the host value had not arrived
        public static int FallbackRollCount;
        // rolls that were corrected after the fact, once the host value landed
        public static int LateCorrectionCount;

        /// <summary>Stable key for one dice roll: unit index + card slot + behaviour position.</summary>
        public static string DiceKey(int unitIdx, int slotOrder, int behaviorIdx)
        {
            return unitIdx.ToString(CultureInfo.InvariantCulture) + ":" +
                   slotOrder.ToString(CultureInfo.InvariantCulture) + ":" +
                   behaviorIdx.ToString(CultureInfo.InvariantCulture);
        }

        private static void ClearDiceTables()
        {
            HostRollQueues.Clear();
            _awaitingDice.Clear();
            _consumedHostValue.Clear();
            HostRollCount = 0;
            FallbackRollCount = 0;
            LateCorrectionCount = 0;
        }

        public static void AwaitDiceCorrection(string key, BattleDiceBehavior b)
        {
            if (key == null || b == null) return;
            Queue<BattleDiceBehavior> q;
            if (!_awaitingDice.TryGetValue(key, out q))
            {
                q = new Queue<BattleDiceBehavior>();
                _awaitingDice[key] = q;
            }
            q.Enqueue(b);
        }

        public static BattleDiceBehavior NextAwaitingDice(string key)
        {
            Queue<BattleDiceBehavior> q;
            if (key == null || !_awaitingDice.TryGetValue(key, out q) || q.Count == 0) return null;
            BattleDiceBehavior b = q.Dequeue();
            if (q.Count == 0) _awaitingDice.Remove(key);
            return b;
        }

        public static void MarkHostValueConsumed(BattleDiceBehavior b)
        {
            if (b != null) _consumedHostValue.Add(b);
        }

        public static bool ConsumeHostValueMark(BattleDiceBehavior b)
        {
            return b != null && _consumedHostValue.Remove(b);
        }

        // ================= lifecycle =================

        /// <summary>Called when a peer link is established (or torn down).</summary>
        public static void SetLink(bool active, bool isHost)
        {
            _linked = active;
            IsHost = isHost;
            MyPlayerIndex = isHost ? 0 : 1;
            if (!active)
            {
                _peerBattleAt = -1f;
                InThisBattle = false;
                _battlePingWait = 0f;
                // Keep the current battle gate on a transient timeout.
                ClearDiceTables();
                EmotionChoiceSync.Reset();
                EgoReservationSync.Reset();
            }
            Debug.Log("[RuinaNet][battle] link " + (active ? "up" : "down") +
                      " role=" + (isHost ? "host" : "client") +
                      " localPlayerIndex=" + MyPlayerIndex);
        }

        /// <summary>Called at the start of every battle (StageController.StartBattle).</summary>
        public static void OnBattleStart()
        {
            Gate.Reset();
            Commit.Reset();
            _peerReadyRound = -1;
            _lastPhaseInt = -1;
            _speedLines.Clear();
            _speedRound = -2;
            Dice.ResetSeed();
            EmotionChoiceSync.Reset();
            EgoReservationSync.Reset();
            InThisBattle = true;
            _battleStartedAt = Time.unscaledTime;
            _peerBattleAt = -1f;       // re-announced every second from Tick, so a lost packet self-heals
            _pendingDoneRound = -1;
            _battlePingWait = 1f;
            ClearDiceTables();
            ResetRound();
        }

        /// <summary>
        /// Round counter.  Bumped exactly once per "start battle" press, on both
        /// machines, in the same order -- the gate guarantees neither side can
        /// advance twice while the other waits, so the two counters stay in
        /// lockstep without either side having to trust the peer's numbering.
        /// </summary>
        private static void ResetRound()
        {
            LocalSubmitted = false;
            PeerSubmitted = false;
            PeerCardsArrived = false;
            Commit.Reset();
            _gateWait = 0f;
            _vitalsWait = 0f;
            _cardsWait = 0f;
            _sentCards.Clear();
            _planningCards.Clear();
            _lastPeerCardUnit = -1;
            PeerCards.Clear();
            NetState.Reset();
            Dice.ResetSeq();
            BufSync.Reset();
            HandSync.Reset();
            EgoSync.Reset();
            EgoReservationSync.Reset();
            if (Active) Debug.Log("[RuinaNet][battle] per-round state cleared");
        }

        // ================= unit ownership =================

        /// <summary>Player-side units this process is allowed to give orders to.</summary>
        public static List<BattleUnitModel> MyUnits()
        {
            List<BattleUnitModel> list = BattleObjectManager.instance.GetAliveList(Faction.Player);
            if (list == null) return new List<BattleUnitModel>();
            if (!Active) return list;

            // The room decides the split, not a hard half.  The two machines share
            // the same save and the same floor, so the game itself hands them the
            // same line-up in the same order -- position is a stable key.
            int start = Room.MyStart;
            int count = Room.MyCount;
            if (start < 0) start = 0;
            if (count < 0) count = 0;

            // Once either player has ticked librarians on the 开启舞台 screen, the
            // agreed rule is "whoever picked it owns it": the Lineup module knows
            // both sides' picks.  Librarians nobody touched still fall back to the
            // positional split, which is what a room with no picks at all uses.
            List<BattleUnitModel> result = new List<BattleUnitModel>();
            for (int i = 0; i < list.Count; i++)
            {
                Ownership.Side owner = Ownership.Of(list[i]);
                if (owner == Ownership.Side.Local)
                {
                    result.Add(list[i]);
                    continue;
                }
                if (owner == Ownership.Side.Peer) continue;
                bool positional = (i >= start && i < start + count);
                if (Lineup.OwnsBattleUnit(list[i], positional)) result.Add(list[i]);
            }
            return result;
        }

        /// <summary>How many player-side units are alive right now</summary>
        /// <remarks>0 outside a battle. Polled by the optional flow trace.</remarks>
        public static int AliveUnitCount()
        {
            try
            {
                List<BattleUnitModel> list = BattleObjectManager.instance.GetAliveList(Faction.Player);
                return list == null ? 0 : list.Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>True when the local player may act for this unit.</summary>
        public static bool IsMine(BattleUnitModel unit)
        {
            if (!Active || unit == null) return true;
            if (unit.faction != Faction.Player) return IsHost;   // enemy units are never user driven
            List<BattleUnitModel> mine = MyUnits();
            for (int i = 0; i < mine.Count; i++)
            {
                if (mine[i] == unit) return true;
            }
            return false;
        }

        // ================= card submission =================

        /// <summary>
        /// Serialise every card the local player placed this round and ship it to
        /// the peer.  One line per unit so a missing unit is obvious in the log.
        /// </summary>
        public static void SubmitLocalCards()
        {
            if (!Active) return;
            List<BattleUnitModel> mine = MyUnits();
            int sent = 0;
            for (int i = 0; i < mine.Count; i++)
            {
                BattleUnitModel unit = mine[i];
                if (unit == null) continue;
                List<BattlePlayingCardDataInUnitModel> slots = unit.cardSlotDetail.cardAry;
                if (slots == null) continue;
                for (int s = 0; s < slots.Count; s++)
                {
                    BattlePlayingCardDataInUnitModel p = slots[s];
                    string line = "CARD " + Round +
                                  " " + NetState.WireIndex(unit) +
                                  " " + s +
                                  " " + CardTag(p) +
                                  " " + (p == null || p.target == null ? -1 : NetState.WireIndex(p.target)) +
                                  " " + (p == null ? -1 : p.targetSlotOrder);
                    NetLink.Send(line);
                    sent++;
                }
            }
            Debug.Log("[RuinaNet][battle] submitted " + sent + " card slot(s) for round " + Round);
        }

        /// <summary>Wire form of a card identity: "packageId:id", or "-" for an empty slot.</summary>
        private static string CardTag(BattlePlayingCardDataInUnitModel p)
        {
            if (p == null || p.card == null) return "-";
            LorId lid = p.card.GetID();
            string pid = lid.packageId;
            if (string.IsNullOrEmpty(pid)) pid = "base";
            return pid + ":" + lid.id;
        }

        /// <summary>Announce that our side is done placing cards.</summary>
        public static void SendDone()
        {
            if (!Active) return;
            NetLink.Send("DONE " + Round);
            Debug.Log("[RuinaNet][battle] DONE sent for round " + Round);
        }

        /// <summary>Tell the peer it may advance the round.</summary>
        public static void SendExecute()
        {
            if (!Active) return;
            NetLink.Send("EXECUTE " + Round);
        }

        // ================= message handling =================

        /// <summary>Feed one line from the link. Returns true when it was a battle message.</summary>
        public static bool HandleMessage(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string[] p = line.Split(' ');
            if (p.Length == 0) return false;

            switch (p[0])
            {
                case "CREATURE":
                    return CreatureSync.HandleMessage(p);
                case "CARD":
                    HandleCard(p);
                    return true;

                case "DONE":
                    {
                        int doneRound = -1;
                        if (p.Length > 1)
                        {
                            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out doneRound);
                        }
                        if (doneRound > Round)
                        {
                            // The peer pressed start before we did.  Hold it until
                            // ArmGate bumps us onto that round.
                            _pendingDoneRound = doneRound;
                            Debug.Log("[RuinaNet][battle] peer DONE for round " + doneRound +
                                      " buffered (we are on " + Round + ")");
                            return true;
                        }
                        // PeerSubmitted is only half the story: the host also needs a
                        // round-matched STATEEND before it will broadcast, so a stale
                        // DONE can never open the gate on its own.
                        if (!GateArmed || doneRound != Round) return true;
                        PeerSubmitted = true;
                        Debug.Log("[RuinaNet][battle] peer DONE for round " + (p.Length > 1 ? p[1] : "?") +
                                  " (local " + Round + ") localSubmitted=" + LocalSubmitted);
                        MaybeReleaseHostGate();
                        return true;
                    }

                case "EXECUTE":
                    // legacy from v0.2 peers; the state broadcast is the real signal now
                    Debug.Log("[RuinaNet][battle] EXECUTE received for round " + (p.Length > 1 ? p[1] : "?"));
                    return true;

                case "PUSHREQ":
                    {
                        // Legacy v3.5 name.  It now means only "the peer is ready";
                        // it must never submit on behalf of the local player.
                        int want = -1;
                        if (p.Length > 1)
                        {
                            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out want);
                        }
                        if (!Active || want < Round || want > Round + 1) return true;
                        _peerReadyRound = want;
                        Room.Notice("对方已经准备完成，等待你按【空格】确认…");
                        Debug.Log("[RuinaNet][battle] peer ready for round " + want +
                                  " -- local confirmation still required");
                        return true;
                    }

                case "ARM":
                    {
                        // The other side pressed start.  Record it, but do not call
                        // ArmGate: local readiness is a human action on this machine.
                        int want = -1;
                        if (p.Length > 1)
                        {
                            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out want);
                        }
                        if (!Active) return true;
                        if (want < Round || want > Round + 1) return true;
                        _peerReadyRound = want;
                        Room.Notice("对方已经准备完成，等待你按【空格】确认…");
                        Debug.Log("[RuinaNet][battle] peer ready for round " + want +
                                  " -- local confirmation still required");
                        return true;
                    }

                case "STATEACK":
                    {
                        int ackRound = -1;
                        if (p.Length > 1)
                            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ackRound);
                        string ackDigest = p.Length > 2 ? p[2] : "";
                        if (IsHost && GateArmed && ackRound == Round && ackDigest == _commitDigest)
                        {
                            if (Commit.NoteAck(ackRound))
                            {
                                Debug.Log("[RuinaNet][battle] client applied final snapshot for round " + ackRound);
                                MaybeReleaseHostGate();
                            }
                        }
                        return true;
                    }

                case "STATENAK":
                    {
                        int nakRound = -1;
                        if (p.Length > 1) int.TryParse(p[1], out nakRound);
                        if (IsHost && GateArmed && nakRound == Round)
                        {
                            Debug.LogWarning("[RuinaNet][battle] client digest mismatch for round " + Round +
                                             " local=" + (p.Length > 2 ? p[2] : "?") +
                                             " host=" + (p.Length > 3 ? p[3] : "?"));
                            Commit.Begin(Round);
                            MaybeReleaseHostGate();
                        }
                        return true;
                    }

                case "PUSH":
                    {
                        if (IsHost) return true;
                        Debug.Log("[RuinaNet][battle] host pushed round " +
                                  (p.Length > 1 ? p[1] : "?") + " -- running it here too");
                        int pushed;
                        if (p.Length > 1 && int.TryParse(p[1], out pushed) && pushed == Round && NetState.StateAllDone && GateArmed) ReleaseGate();
                        return true;
                    }

                case "DICE":
                    HandleDice(p);
                    return true;

                case "MOUSE":
                    HandleMouse(p);
                    return true;

                case "STAGE":
                    if (CreatureSync.Active) return true;
                    return StageSync.HandleMessage(p);

                case "STAGENO":
                    StageSync.NoteDeclined(p);
                    return true;

                case "FLOOR":
                    return FloorSync.HandleMessage(p);

                case "BATTLE":
                    {
                        bool wasInBattle = PeerInBattle;
                        _peerBattleAt = Time.unscaledTime;   // heartbeats every second
                        if (!wasInBattle)
                        {
                            Debug.Log("[RuinaNet][battle] peer is in a battle -- co-op mode on");
                        }
                        return true;
                    }

                case "ROOM":
                case "READY":
                case "JOINED":
                case "GO":
                case "LINEUP":
                    return Room.HandleMessage(p) || Lineup.HandleMessage(p);

                case "HAND2":
                case "VITALS":
                case "VITALSEND":
                case "STATEBEGIN":
                case "USTATE":
                case "STATEEND":
                case "STATEALL":
                    return NetState.HandleMessage(p);

                case "SPEED":
                    return HandleSpeedLine(p);

                case "CARDS":
                    return HandleCards(p);

                case "BUFS":
                    return BufSync.HandleMessage(p);

                case "HANDNAK":
                    HandSync.HandleNack(p);
                    return true;

                case "EGO2":
                case "EGOEND":
                case "EGOLIVE":
                    return EgoSync.HandleMessage(p);

                case "EGOREQ":
                case "EGOGRANT":
                case "EGODENY":
                case "EGOSTATE":
                case "EGOREL":
                case "EGOCOMMIT":
                    return EgoReservationSync.HandleMessage(p);

                case "EMOTION":
                case "EMOTIONACK":
                case "EMOTIONNAK":
                    return EmotionChoiceSync.HandleMessage(p);

                case "LOADOUT2":
                    if (CreatureSync.Active) return true;
                    return Loadout.HandleMessage(p);

                default:
                    return false;
            }
        }

        // ================= live card placements =================
        //
        // Placing cards and starting the round are two different things, and the
        // first version only ever sent the placements along with the round gate.
        // The gate stays shut until BOTH players press "start battle", so while the
        // two of you are arranging, nothing at all crosses the wire -- which reads
        // as "the game is not synced".  Each side therefore publishes its own
        // units' placements continuously; the peer drops them straight into its
        // copy of those units (which it cannot touch itself, see IsMine).

        private static void SendMyCards()
        {
            if (!Active) return;

            // The arranging window is per machine, and the two machines do NOT enter
            // it together: the client was measured entering ApplyLibrarianCardPhase a
            // full 3.1 s after the host.  Everything the host published in that gap
            // was dropped by the receiver ("phase is RoundStartPhase_System, not the
            // arranging phase") and never re-sent, because the window closes on the
            // first 空格 -- so the enemy's page and target were left to each machine
            // separately, which is exactly what the two windows then disagreed about.
            //
            // Enemy units are the host's to decide and the client never arranges them,
            // so their placements travel outside the window too.  The players' own
            // pages stay windowed: those are made locally and must not be pushed
            // around while somebody is still choosing.
            bool window = InCardPhase() && !GateArmed && !RoundHandled;
            if (!window) return;

            List<BattleUnitModel> mine = MyUnits();
            if (IsHost) mine.AddRange(BattleObjectManager.instance.GetAliveList(Faction.Enemy));
            if (mine == null || mine.Count == 0) return;

            for (int i = 0; i < mine.Count; i++)
            {
                BattleUnitModel u = mine[i];
                if (u == null) continue;
                PublishUnitCards(u, false);
            }
        }

        /// <summary>Publish one owned unit immediately after an explicit UI edit.</summary>
        public static void PublishUnitCards(BattleUnitModel u, bool force)
        {
            if (!Active || u == null) return;
            if (u.faction == Faction.Enemy)
            {
                if (!IsHost) return;
            }
            else if (!IsMine(u)) return;

            string csv = NetState.CardsLine(u);
            int wire = NetState.WireIndex(u);
            string was;
            if (!force && _sentCards.TryGetValue(wire, out was) && was == csv) return;
            _sentCards[wire] = csv;

            NetLink.Send("CARDS " + Round.ToString(CultureInfo.InvariantCulture) +
                         " " + wire.ToString(CultureInfo.InvariantCulture) + " " + csv);
            Debug.Log("[RuinaNet][battle] published placements for unit " + wire +
                      (force ? " (forced)" : ""));
        }

        private static bool HandleCards(string[] p)
        {
            if (p.Length < 4) return false;
            // Each of these used to be a silent drop, which is how the enemy's
            // placements could go a whole session without ever landing.
            if (!Active) { NoteDrop("cards", "not Active"); return true; }
            int packetRound;
            if (!int.TryParse(p[1], out packetRound) || !AcceptPlanningRound(packetRound))
            {
                NoteDrop("cards", "packet round " + p[1] + " != local round " + Round);
                return true;
            }
            int idx;
            if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) return false;

            // The peer may enter planning several seconds before this machine.  Do
            // not mutate cardAry while the preceding action is still executing;
            // retain the newest complete line and apply it at our planning edge.
            BattleUnitModel target = NetState.Find(idx);
            if (!InCardPhase())
            {
                _planningCards[idx] = p[3];
                Debug.Log("[RuinaNet][battle] buffered placements for unit " + idx +
                          " until arranging phase");
                return true;
            }
            if (target == null || target.faction != Faction.Enemy)
            {
                // Local ready locks local input only. The peer can still finish
                // arranging until its own DONE, including redirecting enemy pages.
                if (GateArmed && PeerSubmitted) { NoteDrop("cards", "peer already submitted"); return true; }
                if (RoundHandled && !GateArmed) { NoteDrop("cards", "round already handled"); return true; }
            }

            // Round is ignored on purpose: placing happens between rounds, and the
            // peer's counter may legitimately still be one behind.
            if (idx != _lastPeerCardUnit)
            {
                _lastPeerCardUnit = idx;
                Debug.Log("[RuinaNet][battle] peer placements for unit " + idx);
            }
            bool ok = NetState.ApplyCardsLine(idx, p[3]);
            RefreshBattleUi();
            return ok;
        }

        private static void FlushPlanningCards()
        {
            if (_planningCards.Count == 0 || !InCardPhase()) return;
            List<KeyValuePair<int, string>> pending =
                new List<KeyValuePair<int, string>>(_planningCards);
            _planningCards.Clear();
            for (int i = 0; i < pending.Count; i++)
                NetState.ApplyCardsLine(pending[i].Key, pending[i].Value);
            RefreshBattleUi();
            Debug.Log("[RuinaNet][battle] applied " + pending.Count +
                      " phase-buffered placement snapshot(s)");
        }

        /// <summary>
        /// Redraw the row under one unit: hp, break, emotion pips and every buff
        /// icon.  The game only repaints it off its own stat-changed events, so a
        /// buff list written from the wire stays invisible until this runs.
        /// </summary>
        /// <remarks>
        /// The buff data list is passed explicitly on purpose.  With a null list
        /// BattleUnitBottomStatUI recomputes the break ceiling from
        /// GetDefaultBreakGauge() and clamps the bar, dropping the bonus a buff
        /// like BreakProtection is supposed to add.
        /// </remarks>
        public static void RefreshUnitUi(BattleUnitModel u)
        {
            if (u == null) return;
            try
            {
                BattleManagerUI ui = SingletonBehavior<BattleManagerUI>.Instance;
                if (ui == null) return;
                if (ui.ui_unitListInfoSummary == null) return;
                ui.ui_unitListInfoSummary.UpdateCharacterProfile(
                    u, u.faction, u.hp, u.breakDetail.breakGauge, u.bufListDetail.GetBufUIDataList());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] unit ui refresh failed: " + e.Message);
            }
        }

        /// <summary>
        /// Redraw the two things that read cardAry.  The original has no per-frame
        /// card-on-die drawing -- the played card is only reachable through the die's
        /// hover preview and the attack arrows -- and both of those are built on
        /// demand, so a placement that arrives from the wire stays invisible until
        /// something asks for it again.  The hand itself is redrawn too: a deck
        /// installed after the hand was first dealt stays invisible without it.
        /// </summary>
        public static void RefreshHandUi()
        {
            BattleManagerUI ui = SingletonBehavior<BattleManagerUI>.Instance;
            if (ui == null) return;
            try
            {
                if (ui.ui_TargetArrow != null)
                {
                    // UpdateTargetList depends on the toggle panel. During UI
                    // transitions rebuild pairings without dereferencing that panel.
                    if (ui.ui_emotionInfoBar != null && ui.ui_emotionInfoBar.targetingToggle != null)
                        ui.ui_TargetArrow.UpdateTargetList();
                    else
                        ui.ui_TargetArrow.UpdateTargetListData();
                    // A card placed through the local UI explicitly redraws the
                    // parrying category.  A peer card is written directly into
                    // cardAry, so UpdateTargetList only follows this machine's
                    // toggle state and can leave the yellow clash line stale even
                    // though both reciprocal targets are present.  Re-run the
                    // original parrying renderer after rebuilding TargetListData.
                    ui.ui_TargetArrow.ShowParryingArrow(true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] arrow refresh failed: " + e.Message);
            }
            try
            {
                if (ui.ui_unitCardsInHand != null) ui.ui_unitCardsInHand.UpdateCardList();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] hand ui refresh failed: " + e.Message);
            }
        }

        public static void RefreshBattleUi()
        {
            RefreshHandUi();
        }

        // ================= stage phase watch =================

        /// <summary>
        /// Poll the stage phase once a frame.  The card-placement phase is the only
        /// place the round gate can arm, so its edges are worth writing down: when
        /// "nothing is synced" comes back, the log now says whether the machines
        /// ever got there at all.
        /// </summary>
        /// <summary>
        /// One log line per call: every unit's speed-dice slots as
        /// "pageId&gt;targetWire@targetSlot".  Both machines print the same tag, so
        /// comparing the two lines says exactly which unit's page or target drifted,
        /// and when -- the whole point of a sync bug report is that guesswork here is
        /// unreliable.
        /// </summary>
        // Two instances write the same Unity Player.log, and the second one to
        // start renames the first one's file out from under it -- so only ever one
        // side of a dual-open shows up there.  The trace therefore gets its own
        // file per role, which both sides can be read from afterwards.
        private static string _diagFile;
        private static bool _diagTried;

        private static string DiagFile()
        {
            if (_diagTried) return _diagFile;
            _diagTried = true;
            try
            {
                _diagFile = System.IO.Path.Combine(Application.dataPath, "Mods", "RuinaNet",
                                                   "RuinaNet-diag-" + (IsHost ? "HOST" : "CLIENT") + ".txt");
            }
            catch (Exception) { _diagFile = ""; }
            return _diagFile;
        }

        /// <summary>Append one trace line to this role's own file, and to the log.</summary>
        public static void Diag(string tag, string body)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                          " " + tag + " " + (IsHost ? "HOST" : "CLIENT") + " r" + Round + " " + body;
            Debug.Log("[RuinaNet][diag] " + line);
            try
            {
                string path = DiagFile();
                if (path.Length > 0) System.IO.File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception) { }
        }

        public static void DumpPlacements(string tag)
        {
            try
            {
                if (!NetLink.Running || !NetLink.Linked) return;
                List<BattleUnitModel> all = BattleObjectManager.instance.GetAliveList(true);
                if (all == null) return;

                StringBuilder sb = new StringBuilder(320);
                sb.Append(tag).Append(' ').Append("phase=").Append(PhaseName()).Append(" : ");
                for (int i = 0; i < all.Count; i++)
                {
                    BattleUnitModel u = all[i];
                    if (u == null) continue;
                    if (i > 0) sb.Append(" | ");
                    sb.Append(NetState.WireIndex(u));
                    sb.Append(u.faction == Faction.Enemy ? "E" : "P");
                    sb.Append('[');

                    List<BattlePlayingCardDataInUnitModel> ary =
                        u.cardSlotDetail == null ? null : u.cardSlotDetail.cardAry;
                    if (ary == null || ary.Count == 0)
                    {
                        sb.Append('-');
                    }
                    else
                    {
                        for (int s = 0; s < ary.Count; s++)
                        {
                            if (s > 0) sb.Append(',');
                            BattlePlayingCardDataInUnitModel c = ary[s];
                            if (c == null || c.card == null) { sb.Append('.'); continue; }
                            try
                            {
                                var lid = c.card.GetID();
                                sb.Append(lid == null ? "?" : lid.id.ToString(CultureInfo.InvariantCulture));
                            }
                            catch (Exception) { sb.Append('?'); }
                            sb.Append('>');
                            sb.Append(c.target == null
                                ? "-"
                                : NetState.WireIndex(c.target).ToString(CultureInfo.InvariantCulture));
                            sb.Append('@').Append(c.targetSlotOrder.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                    sb.Append(']');
                }
                Diag("place", sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][diag] placement dump failed: " + e.Message);
            }
        }

        /// <summary>Ring the phase name without the round suffix, for the trace line.</summary>
        private static string PhaseName()
        {
            try
            {
                StageController sc = Singleton<StageController>.Instance;
                return sc == null ? "?" : sc.Phase.ToString();
            }
            catch (Exception) { return "?"; }
        }

        // Which guard has already been reported this round, so a dropped placement
        // packet is written down once instead of flooding the trace.
        private static readonly HashSet<string> _dropNoted = new HashSet<string>();
        private static int _dropRound = int.MinValue;

        /// <summary>
        /// Record that a placement packet was refused, and why.  A silent guard was
        /// the reason the enemy's page/target could drift for a whole session without
        /// anything in the log saying so.
        /// </summary>
        public static void NoteDrop(string tag, string why)
        {
            try
            {
                if (_dropRound != Round) { _dropRound = Round; _dropNoted.Clear(); }
                if (!_dropNoted.Add(why)) return;
                Diag(tag, "dropped: " + why);
            }
            catch (Exception) { }
        }

        public static void WatchPhase()
        {
            try
            {
                StageController sc = Singleton<StageController>.Instance;
                if (sc == null) return;

                int now = (int)sc.Phase;
                if (now == _lastPhaseInt) return;
                _lastPhaseInt = now;
                if (!NetLink.Running || !NetLink.Linked) return;

                string name = sc.Phase.ToString();
                Debug.Log("[RuinaNet][battle] phase -> " + name + " (round " + Round + ")");

                // Placement trace: one line per unit at each phase where the enemy's
                // page/target can be decided, so the two machines' pictures can be
                // compared line by line.  Both sides write the same tag, so a
                // divergence shows up as two lines with the same tag and different
                // contents.
                if (name == "ApplyEnemyCardPhase" || name == "ApplyLibrarianCardPhase" ||
                    name == "SetCurrentDiceAction" || name == "RoundEndPhase")
                {
                    DumpPlacements(name);
                }

                if (name == "ApplyLibrarianCardPhase")
                {
                    // A fresh placing phase: a new round is about to be gated, so let
                    // the next press arm again and forget the previous placements.
                    Gate.BeginPlanning(sc.RoundTurn);
                    EgoReservationSync.BeginPlanning(sc.RoundTurn);
                    _sentCards.Clear();
                    BufSync.Reset();
                    HandSync.Reset();
                    EgoSync.BeginPlanning();
                    NetState.BeginPlanning();
                    FlushPlanningCards();
                    NetState.FlushPlanningHands();
                    if (IsHost) OnRoundSorted();
                    else ApplyCachedSpeeds();
                    // Round-start hygiene: drop dangling target references left by
                    // units that died last round (targeting arrows at corpses).
                    int deadRefs = NetState.ClearDeadTargets();
                    if (deadRefs > 0)
                        Debug.Log("[RuinaNet][battle] cleared " + deadRefs + " dead target reference(s)");
                    _sentCards.Clear();
                    _lastPeerCardUnit = -1;
                    Room.Notice("配好牌后按【空格】确认；双方都确认后本回合才会开始");
                }
            }
            catch (Exception)
            {
            }
        }

        // ================= round-start speed dice =================
        //
        // Every machine rolls its own speed dice when the round is sorted, and
        // UnityEngine.Random lives in global state, so the two rolls diverge --
        // the client would open the round with different numbers on every unit.
        // The host is authoritative for these: it broadcasts the whole table and
        // the client overwrites its own roll with it.  The lines are also cached,
        // because our own SortUnitPhase may run after the broadcast arrived.

        private static readonly List<string> _speedLines = new List<string>();
        private static int _speedRound = -1;

        /// <summary>Called from the SortUnitPhase postfix on both machines.</summary>
        public static void OnRoundSorted()
        {
            if (!Active) return;
            try
            {
                if (IsHost)
                {
                    _speedLines.Clear();
                    _speedRound = Round;
                    List<BattleUnitModel> all = AllAliveUnits();
                    for (int i = 0; i < all.Count; i++)
                    {
                        BattleUnitModel u = all[i];
                        if (u == null) continue;
                        string line = NetState.SpeedLine(u);
                        if (string.IsNullOrEmpty(line) || line == "-") continue;
                        int wire = NetState.WireIndex(u);
                        _speedLines.Add(wire + " " + line);
                        NetLink.Send("SPEED " + Round + " " + wire + " " + line);
                    }
                    Debug.Log("[RuinaNet][battle] round " + Round + " speed dice broadcast (" +
                              _speedLines.Count + " unit(s))");
                }
                else
                {
                    ApplyCachedSpeeds();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] round-sorted speed sync failed: " + e.Message);
            }
        }

        private static bool HandleSpeedLine(string[] p)
        {
            if (p == null || p.Length < 4) return false;
            int round;
            if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out round)) return false;
            if (!Active) return true;
            int idx;
            if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) return false;
            if (IsHost || round < Round || round > Round + 1) return true;

            if (_speedRound != round) { _speedLines.Clear(); _speedRound = round; }
            _speedLines.Add(idx + " " + p[3]);
            // Applied even while the gate is armed: the round has not actually
            // started yet, and the old !GateArmed guard made the client open the
            // round on its own roll whenever it pressed start before the host
            // had sorted (the "different action order" window).
            if (round == Round && InCardPhase()) NetState.ApplySpeedLine(idx, p[3]);
            return true;
        }

        private static void ApplyCachedSpeeds()
        {
            if (_speedRound != Round) return;
            for (int i = 0; i < _speedLines.Count; i++)
            {
                string s = _speedLines[i];
                int sp = s.IndexOf(' ');
                if (sp <= 0) continue;
                int idx;
                if (!int.TryParse(s.Substring(0, sp), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out idx)) continue;
                NetState.ApplySpeedLine(idx, s.Substring(sp + 1));
            }
            if (_speedLines.Count > 0)
                Debug.Log("[RuinaNet][battle] round " + Round + " speed dice taken from the host (" +
                          _speedLines.Count + " unit(s))");
        }

        private static List<BattleUnitModel> AllAliveUnits()
        {
            try
            {
                BattleObjectManager mgr = BattleObjectManager.instance;
                if (mgr == null) return new List<BattleUnitModel>();
                return mgr.GetAliveList();
            }
            catch (Exception) { return new List<BattleUnitModel>(); }
        }

        // Peer pointer position, normalised 0..1 against their screen. Cheap and
        // lossy on purpose: a dropped sample just means the marker lags a frame.
        private static void HandleMouse(string[] p)
        {
            if (p.Length < 3) return;
            float mx;
            float my;
            if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out mx)) return;
            if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out my)) return;
            BattleOverlay.SetPeerMouse(mx, my);
        }

        private static void HandleCard(string[] p)
        {
            if (p.Length < 7) return;
            int round = int.Parse(p[1], CultureInfo.InvariantCulture);
            if (round != Round)
            {
                // stale payload from a previous round
                return;
            }
            int unitIdx = int.Parse(p[2], CultureInfo.InvariantCulture);
            int slot = int.Parse(p[3], CultureInfo.InvariantCulture);
            PeerCards[unitIdx * 100 + slot] =
                p[4] + "|" + p[5] + "|" + p[6];
            PeerCardsArrived = true;
        }

        private static void HandleDice(string[] p)
        {
            if (p.Length < 6) return;
            int round;
            if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out round) || round != Round) return;
            int unitIdx, slot, behavior, value;
            if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out unitIdx)) return;
            if (!int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)) return;
            if (!int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out behavior)) return;
            if (!int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return;
            string key = DiceKey(unitIdx, slot, behavior);
            Queue<int> q;
            if (!HostRollQueues.TryGetValue(key, out q))
            {
                q = new Queue<int>();
                HostRollQueues[key] = q;
            }
            q.Enqueue(value);
            // The client may have already made this roll locally while the value
            // was in flight.  Correct that behaviour's result now: the die is
            // usually still spinning, so the clash reads the host's number.
            BattleSyncPatches.CorrectAwaitingDice(key, value);
        }

        // ================= gate =================

        /// <summary>
        /// Runs from the StageController.CompleteApplyingLibrarianCardPhase prefix
        /// when the local player pressed "start battle".  We always arm: the actual
        /// hand-off is decided in Tick, one frame later, so we never re-enter the
        /// patched method from inside its own prefix.
        /// </summary>
        public static void ArmGate(int targetRound = -1, string src = null)
        {
            // The host may name the round it wants (PUSHREQ).  Line up on it
            // first so the ++ below lands exactly there and both sides number
            // the round the same way.
            WatchPhase();
            if (!Active || !Gate.TryArm(InCardPhase(), targetRound)) return;
            NetState.BeginRound();
            EgoSync.Reset();
            Dice.ResetSeq();
            ClearDiceTables();
            LocalSubmitted = true;
            // The peer may have pressed "start battle" first, in which case its
            // DONE and its unit state already arrived while we were still on the
            // previous round number.  Do not throw those away: the gate would sit
            // here for the full timeout and the host would broadcast a snapshot
            // with the peer's card placements missing.
            PeerSubmitted = _pendingDoneRound == Round;
            _pendingDoneRound = -1;
            Commit.Begin(Round);
            _commitDigest = "";
            _gateWait = 0f;
            PeerCardsArrived = false;

            SubmitLocalCards();
            // Immediate-use key-page effects never enter cardAry.  Flush their
            // personal EGO container and buffs before DONE so the host commits
            // the same trigrams and other key-page actions the owner selected.
            EgoSync.SendOwnedPersonalLive(true);
            BufSync.SendOwnedSnapshot();
            int units = NetState.SendMine(Round);
            NetState.SendStateEnd(Round, units);
            SendDone();

            // Now that Round is current, the buffered datagrams are valid.
            NetState.FlushPending();

            // Gate is armed before flushing early packets.
            Room.Notice(PeerSubmitted || _peerReadyRound == Round
                ? "双方都已确认，正在校验本回合数据…"
                : "你已经准备完成，正在等待对方按【空格】…");

            // READY is informational only.  DONE plus the complete state payload is
            // what lets the host commit the round after both people press locally.
            NetLink.Send("ARM " + Round);

            Debug.Log("[RuinaNet][battle] gate armed, round " + Round + " role=" +
                      (IsHost ? "host" : "client") + " myUnits=" + units +
                      " peerAlreadySubmitted=" + PeerSubmitted +
                      " from=" + (src == null ? "press" : src));
        }

        /// <summary>True while the game is waiting for both players to place their pages.</summary>
        public static bool InCardPhase()
        {
            try
            {
                StageController sc = Singleton<StageController>.Instance;
                return sc != null && sc.Phase == StageController.StagePhase.ApplyLibrarianCardPhase;
            }
            catch (Exception) { return false; }
        }

        // Pressing ready increments Round on only that machine. Both peers are
        // still in the same planning phase until the shared gate releases.
        public static bool AcceptPlanningRound(int packetRound)
        {
            if (packetRound == Round) return true;
            if (!InCardPhase()) return false;
            return SyncAuthority.ReceivePlanningRound(Round, packetRound, GateArmed,
                                                     PeerSubmitted, _peerReadyRound);
        }

        /// <summary>
        /// The player pressed start -- space, the menu key, or the big centre button.
        /// Whatever the game's own input guards decided about that press, the round gate
        /// opens from here: a press that got eaten by a popup looked exactly like a hang.
        /// </summary>
        public static void ArmFromInput(string src)
        {
            if (!Active) return;
            if (GateArmed || RoundHandled) return;
            if (!InCardPhase()) return;
            ArmGate(-1, src);
        }

        private static float _inputNoteAt = -1f;
        private static int _inputNotes;

        /// <summary>
        /// True when this frame's input means "start the round".  The first few times it
        /// also writes down the guard states, so one log can tell "nobody pressed" apart
        /// from "the press was eaten by a popup / the story / a returning camera".
        /// </summary>
        public static bool InputWanted(bool force)
        {
            if (!Active) return false;
            if (GateArmed || RoundHandled) return false;
            if (!InCardPhase()) return false;

            bool space = false;
            bool menu = false;
            try
            {
                space = Input.GetKeyDown(KeyCode.Space);
                menu = Input.GetButtonDown("Button_LeftMenu");
            }
            catch (Exception) { }

            if (!force && !space && !menu) return false;

            if (_inputNotes < 10 && Time.unscaledTime - _inputNoteAt > 0.5f)
            {
                _inputNoteAt = Time.unscaledTime;
                _inputNotes++;
                Debug.Log("[RuinaNet][gate] start-round input #" + _inputNotes +
                          " button=" + force + " space=" + space + " menu=" + menu +
                          " active=" + Active + " linked=" + _linked +
                          " peerInBattle=" + PeerInBattle +
                          " armed=" + GateArmed + " handled=" + RoundHandled +
                          " guards[popup=" + PopupOpen() + " story=" + StoryRunning() +
                          " cam=" + CamReturning() + " tutorial=" + TutorialRunning() + "]");
            }
            return true;
        }

        private static BattleEmotionInfo_CenterBtn _centerBtn;
        private static float _centerBtnSearchAfter;

        /// <summary>
        /// True when this frame's left click landed inside the game's own "start the
        /// round" button.  That button only reacts while its own _bInteractable and
        /// _bEntered flags are set, and when it does not react the round simply never
        /// opens, leaving no trace anywhere.  Reading the click off the button's own
        /// rectangle is the fallback for that case: a click there means "start the
        /// round" and nothing else, so arming on it is safe.
        /// </summary>
        private static bool ClickedStartButton()
        {
            if (!Input.GetMouseButtonDown(0)) return false;

            if (_centerBtn == null)
            {
                if (Time.unscaledTime < _centerBtnSearchAfter) return false;
                try
                {
                    UnityEngine.Object[] found =
                        Resources.FindObjectsOfTypeAll(typeof(BattleEmotionInfo_CenterBtn));
                    for (int i = 0; i < found.Length; i++)
                    {
                        BattleEmotionInfo_CenterBtn b = found[i] as BattleEmotionInfo_CenterBtn;
                        if (b != null && b.gameObject != null && b.gameObject.activeInHierarchy)
                        {
                            _centerBtn = b;
                            break;
                        }
                    }
                }
                catch (Exception) { }
                if (_centerBtn == null)
                {
                    _centerBtnSearchAfter = Time.unscaledTime + 5f;   // not on screen yet
                    return false;
                }
            }

            try
            {
                RectTransform rt = _centerBtn.transform as RectTransform;
                if (rt == null) return false;
                Canvas c = _centerBtn.GetComponentInParent<Canvas>();
                Camera cam = (c != null && c.renderMode != RenderMode.ScreenSpaceOverlay)
                             ? c.worldCamera : null;
                return RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, cam);
            }
            catch (Exception) { return false; }
        }

        private static string PopupOpen()
        {
            try { return UI.UIPopupWindowManager.Instance.AllCheckOpen().ToString(); }
            catch (Exception) { return "?"; }
        }

        private static string StoryRunning()
        {
            try { return SingletonBehavior<BattleManagerUI>.Instance.ui_battleStory.isRunning.ToString(); }
            catch (Exception) { return "?"; }
        }

        private static string CamReturning()
        {
            try { return SingletonBehavior<BattleCamManager>.Instance.IsCamIsReturning.ToString(); }
            catch (Exception) { return "?"; }
        }

        private static string TutorialRunning()
        {
            try
            {
                BattleTutorialManagerUI t = SingletonBehavior<BattleTutorialManagerUI>.Instance;
                return t.IsRunningTutorial + "/" + t.IsRunningCreatureTutorial;
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>Called by NetState when the peer said "that is my whole round".</summary>
        public static void OnPeerStateReady()
        {
            MaybeReleaseHostGate();
        }

        /// <summary>
        /// Per-frame driver.  Uniform for both roles: whoever still owes us
        /// something keeps the gate shut.
        /// </summary>
        public static void Tick(float dt)
        {
            WatchPhase();

            // The 开启舞台 line-up is not a battle concern, but it has to keep
            // running while both players are still on the reception screen.
            Lineup.Tick(dt);

            // Same story for the loadout: the book pages of my librarians are
            // published from the room screen, before either side loads the battle.
            Loadout.Tick(dt);

            // And for the floor: two machines that open the "same" reception on
            // different floors never had the same line-up to begin with, so the
            // whole battle (and every opening buff) differs from round 1.
            FloorSync.Tick(dt);

            // Our own battle presence is re-read from the game state every frame,
            // never trusted from a cached flag: a link that flickered used to
            // clear that flag and leave this side announcing nothing for the rest
            // of the reception, i.e. one machine saw co-op and the other did not.
            InThisBattle = InBattleLocal();

            // "I am in a battle" goes out once a second for as long as we are in
            // one, so a single dropped datagram cannot silently downgrade the
            // session to solo.
            if (_linked && InThisBattle)
            {
                _battlePingWait += dt;
                if (_battlePingWait >= 1f)
                {
                    _battlePingWait = 0f;
                    NetLink.Send("BATTLE");
                }
            }
            else
            {
                _battlePingWait = 0f;
            }

            if (Active != _activeLogged)
            {
                _activeLogged = Active;
                Debug.Log("[RuinaNet][battle] co-op " + (Active ? "ON" : "OFF") +
                          " (linked=" + _linked + " meInBattle=" + InThisBattle +
                          " peerInBattle=" + PeerInBattle + ")");
            }

            if (!_linked) return;

            if (!Active) return;

            // Keep the other side's field honest, a few times a second.  This is the
            // only channel that repairs hit points, the stagger gauge and the speed
            // dice between two round gates.  Both directions matter: while only the
            // host reported, the host kept running its own simulation of the client's
            // librarians and the two battles drifted apart for the whole reception.
            // The host reports everything except the client's own units, the client
            // reports the units it owns.
            _vitalsWait += dt;
            // Sent through the WHOLE battle, not just the arranging phase: during
            // combat execution there is no other channel, and that vacuum was
            // exactly why the two sides showed different enemy HP all round long.
            if (IsHost && _vitalsWait >= VitalsGap)
            {
                _vitalsWait = 0f;
                NetState.SendVitals(Round, false);
            }

            // HP and break agreeing is not enough: the buffs decide how much damage
            // each hit does, so the host publishes the whole buff picture as well.
            BufSync.Tick(dt);
            HandSync.Tick(dt);
            EgoSync.Tick(dt);
            EmotionChoiceSync.Tick(dt);
            EgoReservationSync.Tick(dt);

            // The centre button is the one input path the game itself reads through
            // the event system: BattleEmotionInfo_CenterBtn.OnPointerUp only fires
            // when that object happens to be entered and interactable, which is not
            // something we control.  Watching the click ourselves, inside the
            // button's own rectangle, closes that last hole -- and a click there
            // means "start the round" and nothing else, so it is safe to arm on.
            try
            {
                if (!GateArmed && !RoundHandled && InCardPhase() && ClickedStartButton())
                {
                    if (InputWanted(true)) ArmFromInput("click-rect");
                }
            }
            catch (Exception) { }

            // Card placements are phase work, not round work.  Both sides arrange at
            // the same time, and until now neither screen could see the other's
            // plays -- the placements only travelled with the round gate, which does
            // not open until both players have pressed "start battle".  Publish mine
            // as they change so the other window draws them right away.
            _cardsWait += dt;
            if (_cardsWait >= CardsGap)
            {
                _cardsWait = 0f;
                SendMyCards();
            }

            if (!GateArmed) { _gateWait = 0f; return; }

            _gateWait += dt;
            if (MaybeReleaseHostGate()) return;

            if (_gateWait > GateTimeout)
            {
                _gateWait = 0f;
                Room.Notice("正在等待对方的完整回合数据，请保持连接…");
                Debug.LogWarning("[RuinaNet][battle] still waiting for complete round " + Round + "; gate stays closed");
            }
        }

        /// <summary>
        /// Host side: once the client has both locked its plays in and shipped its
        /// unit state, broadcast the authoritative snapshot and let everyone go.
        /// </summary>
        private static bool MaybeReleaseHostGate()
        {
            if (!IsHost || !GateArmed) return false;
            if (!LocalSubmitted || !PeerSubmitted) return false;
            if (!NetState.PeerStateComplete) return false;

            if (!Commit.SnapshotSent)
            {
                BufSync.SendSnapshot();
                int n = NetState.SendAll(Round);
                EgoSync.SendAll(Round);
                NetState.SendStateEnd(Round, n);
                _commitDigest = NetState.CommitDigest();
                NetState.SendStateAll(Round, n, _commitDigest);
                Commit.MarkSnapshotSent(Round);
                Room.Notice("双方都已准备，正在等待对方应用最终数据…");
                Debug.Log("[RuinaNet][battle] final snapshot sent for round " + Round);
                return false;
            }
            if (!Commit.CanRelease(Round)) return false;

            // The client has applied the complete snapshot.  PUSH is the only
            // client-side release signal; STATEALL by itself no longer advances it.
            NetLink.Send("PUSH " + Round);
            ReleaseGate();
            return true;
        }

        /// <summary>Client confirms that every USTATE and HAND in STATEALL landed.</summary>
        public static void OnHostSnapshotReady(int round, string digest)
        {
            if (!Active || IsHost || !GateArmed || round != Round) return;
            NetLink.Send("STATEACK " + round.ToString(CultureInfo.InvariantCulture) + " " + digest);
            Room.Notice("数据校验完成，等待房主开始本回合…");
            Debug.Log("[RuinaNet][battle] STATEACK sent for round " + round);
        }

        /// <summary>
        /// Push the local round forward by calling the original method with
        /// auto = true (which skips the input debounce and the tutorial branches).
        /// </summary>
        private static void ReleaseGate()
        {
            if (!Gate.TryRelease(InCardPhase())) return;
            _gateWait = 0f;
            StageController sc = Singleton<StageController>.Instance;
            if (sc == null) return;
            Debug.Log("[RuinaNet][battle] releasing gate, advancing round " + Round);
            // The last moment both machines agree on the board before the dice run:
            // print what each of them is about to execute with.
            DumpPlacements("gate-release");
            try
            {
                sc.CompleteApplyingLibrarianCardPhase(true);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet][battle] gate release failed: " + e);
            }
            // The round is under way: let the NEXT "start battle" press arm again.
            // (Leaving this true would make the next press slip through ungated.)
            // Handled remains set until the next real planning phase.
        }

        // ================= host-side dice =================

        /// <summary>
        /// The value one dice roll should show, called from the MakeDiceResult prefix.
        /// Host: deterministic (identical on both machines by construction, and the
        /// authoritative broadcast in Roll_Post carries the fully corrected value).
        /// Client: the host's broadcast value when it has arrived (FIFO per roll
        /// key), the deterministic value otherwise -- a fallback never stalls the
        /// battle; it is repaired by the late correction in Roll_Post / HandleDice
        /// and bounded by the continuous vitals channel.
        /// </summary>
        public static int NextDiceValue(int diceMin, int diceFace)
        {
            if (IsHost) return Dice.Pips(diceMin, diceFace);
            string key = CurrentDiceKey();
            Queue<int> q;
            if (key != null && HostRollQueues.TryGetValue(key, out q) && q.Count > 0)
            {
                MarkHostValueConsumed(GetCurrentRoll());
                return q.Dequeue();
            }
            FallbackRollCount++;
            if (FallbackRollCount <= 8)
            {
                Debug.LogWarning("[RuinaNet][battle] no host value for " + (key == null ? "?" : key) +
                                 " -- deterministic fallback (#" + FallbackRollCount + ")");
            }
            return Dice.Pips(diceMin, diceFace);
        }

        // The roll site does not receive the owner/card identity, so we remember
        // the behaviour that is currently being rolled.  CurRoll is set from the
        // BattleDiceBehavior.RollDice prefix and cleared in its postfix.
        [ThreadStatic] private static BattleDiceBehavior _curRoll;

        public static void SetCurrentRoll(BattleDiceBehavior behavior)
        {
            _curRoll = behavior;
        }

        public static BattleDiceBehavior GetCurrentRoll()
        {
            return _curRoll;
        }

        private static string CurrentDiceKey()
        {
            BattleDiceBehavior b = _curRoll;
            if (b == null || b.owner == null) return null;
            int behaviorIdx = 0;
            BattlePlayingCardDataInUnitModel p = CurrentPlayingCard(b);
            if (p != null)
            {
                List<BattleDiceBehavior> list = p.GetDiceBehaviorList();
                if (list != null)
                {
                    int found = list.IndexOf(b);
                    if (found >= 0) behaviorIdx = found;
                }
            }
            int slotOrder = p == null ? 0 : p.slotOrder;
            return DiceKey(NetState.WireIndex(b.owner), slotOrder, behaviorIdx);
        }

        private static BattlePlayingCardDataInUnitModel CurrentPlayingCard(BattleDiceBehavior b)
        {
            if (b.owner == null || b.owner.cardSlotDetail == null) return null;
            List<BattlePlayingCardDataInUnitModel> slots = b.owner.cardSlotDetail.cardAry;
            if (slots == null) return null;
            for (int i = 0; i < slots.Count; i++)
            {
                BattlePlayingCardDataInUnitModel p = slots[i];
                if (p == null) continue;
                if (p.currentBehavior == b || p.currentBehaviorUI == b) return p;
                if (p.cardBehaviorQueue != null && p.cardBehaviorQueue.Contains(b)) return p;
            }
            return null;
        }

        /// <summary>Host side: announce one finished roll to the peer.</summary>
        public static void BroadcastRoll(BattleDiceBehavior behavior, int value)
        {
            if (!Active || !IsHost || behavior == null || behavior.owner == null) return;
            string key = CurrentDiceKey();
            if (key == null) return;
            int slotOrder = 0;
            BattlePlayingCardDataInUnitModel p = CurrentPlayingCard(behavior);
            if (p != null) slotOrder = p.slotOrder;
            int behaviorIdx = 0;
            if (p != null)
            {
                List<BattleDiceBehavior> list = p.GetDiceBehaviorList();
                if (list != null)
                {
                    int found = list.IndexOf(behavior);
                    if (found >= 0) behaviorIdx = found;
                }
            }
            HostRollCount++;
            NetLink.Send("DICE " + Round + " " + NetState.WireIndex(behavior.owner) + " " + slotOrder + " " +
                         behaviorIdx + " " + value);
        }

        /// <summary>
        /// Recompute the key for an already finished roll.  The roll postfix
        /// runs after RollDice has overwritten _curRoll indirectly, so it passes
        /// the behaviour explicitly.
        /// </summary>
        public static string CurrentDiceKeyOf(BattleDiceBehavior b)
        {
            BattleDiceBehavior prev = _curRoll;
            _curRoll = b;
            string k = CurrentDiceKey();
            _curRoll = prev;
            return k;
        }
    }
}
