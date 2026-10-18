using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LOR_DiceSystem;
using UnityEngine;

namespace RuinaNet
{
    /// <summary>
    /// Host-authoritative unit state broadcast.
    ///
    /// The host is the only brain.  Speed dice, enemy plays, hit points and the
    /// stagger gauge all come from it.  Every round, right before the dice start
    /// resolving, both sides exchange one USTATE line per unit; the host then
    /// broadcasts its own view of every unit, which the client overwrites its
    /// simulation with.  Anything the two processes rolled differently during
    /// their own round start is corrected here.
    ///
    /// Wire format, one message per unit:
    ///
    ///   USTATE &lt;round&gt; &lt;unitIndex&gt; &lt;hp&gt; &lt;breakGauge&gt; &lt;breakLife&gt; &lt;speeds&gt; &lt;cards&gt;
    ///   STATEEND &lt;round&gt; &lt;unitCount&gt;
    ///
    /// speeds : "~"-separated, each "&lt;value&gt;" or "b&lt;value&gt;" when the die is broken
    /// cards  : "~"-separated, one entry per card slot, in slot order:
    ///          "&lt;slot&gt;,&lt;tag&gt;,&lt;targetIndex&gt;,&lt;targetSlot&gt;,&lt;subTargets&gt;" or "-" for empty
    ///          tag        : "packageId:id"
    ///          subTargets : "|"-separated "index:slot", or "-" when there are none
    /// </summary>
    internal static class NetState
    {
        private const char ListSep = '~';
        private const char SubSep = '|';

        // client side bookkeeping
        public static int ExpectedUnits = -1;
        public static int AppliedUnits;
        public static bool PeerStateComplete;   // the other side's units are in
        public static bool StateEnded;          // we heard a "that is the whole round"
        // The host has broadcast EVERY unit (its own plus ours).  Clients only
        // release the round gate on this, never on a plain STATEEND, because the
        // first STATEEND we see is just the host submitting its own side.
        public static bool StateAllDone;

        public static int AppliedTotal;         // diagnostics
        public static int SkippedTotal;         // units the host named that we do not have

        private static readonly HashSet<int> _applied = new HashSet<int>();
        private static readonly HashSet<int> _handsApplied = new HashSet<int>();
        private static readonly Dictionary<int, int> _handRevision = new Dictionary<int, int>();
        private static readonly Dictionary<int, string[]> _planningHands = new Dictionary<int, string[]>();
        public static void BeginRound()
        {
            ExpectedUnits = -1;
            AppliedUnits = 0;
            PeerStateComplete = StateEnded = StateAllDone = false;
            _applied.Clear();
            _handsApplied.Clear();
            _handRevision.Clear();
        }

        public static void Reset()
        {
            ExpectedUnits = -1;
            AppliedUnits = 0;
            PeerStateComplete = false;
            StateEnded = false;
            StateAllDone = false;
            _pending.Clear();
            _planningHands.Clear();
            _applied.Clear();
            _handsApplied.Clear();
            _handRevision.Clear();
        }

        /// <summary>Revision numbers are scoped to one arranging phase.</summary>
        public static void BeginPlanning()
        {
            _handRevision.Clear();
            _handsApplied.Clear();
        }

        // ================= late arrivals =================

        // Whoever presses "start battle" first ships its units while the other
        // side is still on the previous round number.  Dropping those datagrams
        // made the gate time out on every round where the client pressed first,
        // and the host then broadcast a snapshot without the client's card
        // placements.  Hold them one round and replay them once ArmGate has
        // bumped the counter.
        private static readonly List<string[]> _pending = new List<string[]>();

        /// <summary>Re-dispatch datagrams that belonged to the round we just entered.</summary>
        public static void FlushPending()
        {
            if (_pending.Count == 0) return;
            List<string[]> again = new List<string[]>(_pending);
            _pending.Clear();
            for (int i = 0; i < again.Count; i++)
            {
                HandleMessage(again[i]);
            }
            Debug.Log("[RuinaNet][net] replayed " + again.Count +
                      " buffered peer packet(s) for round " + BattleSync.Round);
        }

        /// <summary>
        /// Apply the newest hand snapshot per unit that arrived while this machine
        /// was still finishing the preceding action phase.
        /// </summary>
        public static void FlushPlanningHands()
        {
            if (_planningHands.Count == 0 || !BattleSync.InCardPhase()) return;
            List<string[]> pending = new List<string[]>(_planningHands.Values);
            _planningHands.Clear();
            for (int i = 0; i < pending.Count; i++) HandleMessage(pending[i]);
            Debug.Log("[RuinaNet][hand] applied " + pending.Count + " phase-buffered snapshot(s)");
        }

        /// <summary>True when this packet belongs to the round we are about to enter.</summary>
        private static bool Stash(string[] p, int round)
        {
            if (round != BattleSync.Round + 1) return false;
            _pending.Add(p);
            Debug.Log("[RuinaNet][net] " + p[0] + " for round " + round +
                      " buffered (we are on " + BattleSync.Round + ")");
            return true;
        }

        // ================= outbound =================

        /// <summary>Client side: ship the units this process drives.</summary>
        public static int SendMine(int round)
        {
            if (!BattleSync.Active || BattleSync.IsHost) return 0;
            List<BattleUnitModel> mine = BattleSync.MyUnits();
            int n = 0;
            if (mine != null)
            {
                for (int i = 0; i < mine.Count; i++)
                {
                    if (SendUnit(round, mine[i])) n++;
                }
            }
            Debug.Log("[RuinaNet][net] sent state for " + n + " own unit(s), round " + round);
            return n;
        }

        /// <summary>Host side: the authoritative snapshot of every living unit.</summary>
        public static int SendAll(int round)
        {
            NetLink.Send("STATEBEGIN " + round);
            if (!BattleSync.Active) return 0;
            int n = 0;
            try
            {
                List<BattleUnitModel> all = BattleObjectManager.instance.GetAliveList(true);
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (SendUnit(round, all[i])) n++;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][net] SendAll failed: " + e.Message);
            }
            Debug.Log("[RuinaNet][net] broadcast state for " + n + " unit(s), round " + round);
            return n;
        }

        public static void SendStateEnd(int round, int unitCount)
        {
            if (!BattleSync.Active) return;
            NetLink.Send("STATEEND " + round.ToString(CultureInfo.InvariantCulture) +
                         " " + unitCount.ToString(CultureInfo.InvariantCulture));
            Debug.Log("[RuinaNet][net] STATEEND sent, round " + round + " units " + unitCount);
        }

        /// <summary>
        /// Host side: follows a full SendAll and tells the peer "every unit is now
        /// consistent, you may advance".  Clients gate on this instead of STATEEND.
        /// </summary>
        public static void SendStateAll(int round, int unitCount, string digest)
        {
            if (!BattleSync.Active) return;
            NetLink.Send("STATEALL " + round.ToString(CultureInfo.InvariantCulture) +
                         " " + unitCount.ToString(CultureInfo.InvariantCulture) + " " + digest);
            Debug.Log("[RuinaNet][net] STATEALL sent, round " + round + " units " + unitCount);
        }

        /// <summary>Deterministic digest of the complete state executed next.</summary>
        public static string CommitDigest()
        {
            try
            {
                List<BattleUnitModel> units = BattleObjectManager.instance.GetAliveList(true);
                if (units == null) units = new List<BattleUnitModel>();
                units.Sort(delegate(BattleUnitModel a, BattleUnitModel b) { return WireIndex(a).CompareTo(WireIndex(b)); });
                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < units.Count; i++)
                {
                    BattleUnitModel u = units[i];
                    string line = WireIndex(u).ToString(CultureInfo.InvariantCulture) + "|" +
                                  ((int)u.faction).ToString(CultureInfo.InvariantCulture) + "|" +
                                  u.hp.ToString("0.0", CultureInfo.InvariantCulture) + "|" +
                                  BreakGauge(u).ToString(CultureInfo.InvariantCulture) + "|" +
                                  BreakLife(u).ToString(CultureInfo.InvariantCulture) + "|" +
                                  SpeedCsv(u) + "|" + CardCsv(u) + "|" +
                                  u.cardSlotDetail.PlayPoint.ToString(CultureInfo.InvariantCulture) + "|" +
                                  HandSync.SnapshotBody(u) + "\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(line);
                    for (int b = 0; b < bytes.Length; b++)
                    {
                        hash ^= bytes[b];
                        hash *= 1099511628211UL;
                    }
                }
                string shared = "EGO|" + EgoSync.CommitBody() + "|LEASE|" + EgoReservationSync.CommitBody() + "\n";
                byte[] sharedBytes = Encoding.UTF8.GetBytes(shared);
                for (int b = 0; b < sharedBytes.Length; b++)
                {
                    hash ^= sharedBytes[b];
                    hash *= 1099511628211UL;
                }
                return hash.ToString("X16", CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][net] commit digest failed: " + e.Message);
                return "ERROR";
            }
        }

        /// <summary>
        /// Host side, continuous: the living picture of the field (hit points,
        /// stagger gauge, speed dice) for every unit, sent a few times a second.
        ///
        /// The gate-time USTATE snapshot only repairs the field once per round, so
        /// between two rounds the two simulations drift apart with nothing pulling
        /// them back -- that is what the players actually see as "the numbers do not
        /// match".  Cards are deliberately left out: the client is allowed to pick
        /// its own cards, and a snapshot carrying "-" leaves its hand alone.
        /// </summary>
        public static int SendVitals(int round, bool skipPeerUnits)
        {
            if (!BattleSync.IsHost) return 0;
            if (!BattleSync.Active) return 0;
            int n = 0;
            try
            {
                List<BattleUnitModel> all = BattleObjectManager.instance.GetAliveList(true);
                // On the host only: the client owns the librarians it picked, so the
                // host must not push its own simulation of those back and undo them.
                // The host still reports every enemy, which nobody else simulates.
                List<BattleUnitModel> players = null;
                if (skipPeerUnits)
                {
                    try { players = BattleObjectManager.instance.GetAliveList(Faction.Player); }
                    catch (Exception) { players = null; }
                }
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        BattleUnitModel u = all[i];
                        if (u == null) continue;
                        if (players != null && players.Contains(u) && !BattleSync.IsMine(u)) continue;
                        NetLink.Send("VITALS " + round.ToString(CultureInfo.InvariantCulture) +
                                     " " + WireIndex(u).ToString(CultureInfo.InvariantCulture) +
                                     " " + u.hp.ToString("0.0", CultureInfo.InvariantCulture) +
                                     " " + BreakGauge(u).ToString(CultureInfo.InvariantCulture) +
                                     " " + BreakLife(u).ToString(CultureInfo.InvariantCulture) +
                                     " " + SpeedCsv(u));
                        n++;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][net] SendVitals failed: " + e.Message);
            }
            NetLink.Send("VITALSEND " + round.ToString(CultureInfo.InvariantCulture) +
                         " " + n.ToString(CultureInfo.InvariantCulture));
            return n;
        }

        /// <summary>
        /// Client side: adopt the host's numbers for one unit.  Reuses ApplyUnit by
        /// handing it the same payload with an empty card column, so the round gate,
        /// the unit lookup and the "only write when it really differs" checks all
        /// behave exactly as they do at the end of a round.
        /// </summary>
        private static void ApplyVitals(string[] p)
        {
            // Accepted at any point in the round, not only while arranging: the
            // host is the authority for combat outcomes, so its numbers are true
            // mid-resolution too.  The old phase gate was the "combat vacuum"
            // that left enemy HP and buffs apart for a whole round.
            if (p == null || p.Length < 7 || BattleSync.IsHost) return;
            int r;
            if (!int.TryParse(p[1], out r) || r != BattleSync.Round) return;
            string[] asUnit = new string[7];
            asUnit[0] = "USTATE";
            asUnit[1] = p[1];
            asUnit[2] = p[2];
            asUnit[3] = p[3];
            asUnit[4] = p[4];
            asUnit[5] = p[5];
            asUnit[6] = p[6];
            // skipCards: VITALS is a vitals-only heartbeat.  Feeding its dummy
            // card column into ApplyCards made PlaceCard clear every slot, so the
            // peer's placements were wiped every 0.7s -- which is exactly what
            // "the two sides show different pages" looked like.
            ApplyUnit(asUnit, false, true);
        }

        private static bool SendUnit(int round, BattleUnitModel u)
        {
            if (u == null) return false;
            try
            {
                StringBuilder sb = new StringBuilder(192);
                sb.Append("USTATE ");
                sb.Append(round.ToString(CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(WireIndex(u).ToString(CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(u.hp.ToString("0.0", CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(BreakGauge(u).ToString(CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(BreakLife(u).ToString(CultureInfo.InvariantCulture)).Append(' ');
                sb.Append(SpeedCsv(u)).Append(' ');
                sb.Append(CardCsv(u));
                sb.Append(' ').Append(u.cardSlotDetail.PlayPoint);
                NetLink.Send(sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][net] SendUnit " + u.index + " failed: " + e.Message);
                return false;
            }
            // HAND failure must not corrupt the USTATE count: STATEEND/STATEALL
            // tally USTATEs, and a HandSync throw here used to make SendUnit
            // return false even though the USTATE datagram had already gone out,
            // so the client ended up waiting for a count that could never match.
            try { HandSync.SendUnit(round, u, true); }
            catch (Exception e) { Debug.LogWarning("[RuinaNet][hand] publish failed: " + e.Message); }
            return true;
        }

        private static int BreakGauge(BattleUnitModel u)
        {
            try { return u.breakDetail == null ? 0 : u.breakDetail.breakGauge; }
            catch (Exception) { return 0; }
        }

        private static int BreakLife(BattleUnitModel u)
        {
            try { return u.breakDetail == null ? 0 : u.breakDetail.breakLife; }
            catch (Exception) { return 0; }
        }

        private static string SpeedCsv(BattleUnitModel u)
        {
            List<SpeedDice> sd = u.speedDiceResult;
            if (sd == null || sd.Count == 0) return "-";
            StringBuilder sb = new StringBuilder(32);
            for (int i = 0; i < sd.Count; i++)
            {
                if (i > 0) sb.Append(ListSep);
                SpeedDice d = sd[i];
                if (d == null) { sb.Append('-'); continue; }
                if (d.breaked) sb.Append('b');
                sb.Append(d.value.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string CardCsv(BattleUnitModel u)
        {
            List<BattlePlayingCardDataInUnitModel> ary =
                u.cardSlotDetail == null ? null : u.cardSlotDetail.cardAry;
            if (ary == null || ary.Count == 0) return "-";
            StringBuilder sb = new StringBuilder(96);
            for (int i = 0; i < ary.Count; i++)
            {
                if (i > 0) sb.Append(ListSep);
                BattlePlayingCardDataInUnitModel p = ary[i];
                if (p == null || p.card == null) { sb.Append('-'); continue; }

                // NO slot index here: the slots are written in order, so the position
                // in the list IS the slot.  An extra leading field made PlaceCard read
                // "0" as the card id and drop every single placement on the floor.
                sb.Append(CardTag(p)).Append(',');
                sb.Append((p.target == null ? -1 : WireIndex(p.target)).ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(p.targetSlotOrder.ToString(CultureInfo.InvariantCulture)).Append(',');

                bool any = false;
                List<BattlePlayingCardDataInUnitModel.SubTarget> subs = p.subTargets;
                if (subs != null)
                {
                    for (int s = 0; s < subs.Count; s++)
                    {
                        BattlePlayingCardDataInUnitModel.SubTarget st = subs[s];
                        if (st == null || st.target == null) continue;
                        if (any) sb.Append(SubSep);
                        sb.Append(WireIndex(st.target).ToString(CultureInfo.InvariantCulture))
                          .Append(':')
                          .Append(st.targetSlotOrder.ToString(CultureInfo.InvariantCulture));
                        any = true;
                    }
                }
                if (!any) sb.Append('-');
            }
            return sb.ToString();
        }

        private static string CardTag(BattlePlayingCardDataInUnitModel p)
        {
            LorId lid = p.card.GetID();
            if (lid == null) return "-";
            string pid = lid.packageId;
            if (string.IsNullOrEmpty(pid)) pid = "base";
            return pid + ":" + lid.id.ToString(CultureInfo.InvariantCulture);
        }

        // ================= inbound =================

        /// <summary>
        /// The speed dice of one unit, as a wire string ("7~4~b3"): used by the
        /// round-start broadcast so both machines roll the same numbers.
        /// </summary>
        public static string SpeedLine(BattleUnitModel u)
        {
            try { return SpeedCsv(u); }
            catch (Exception) { return "-"; }
        }

        /// <summary>Force this unit's speed dice to the values the host rolled.</summary>
        public static bool ApplySpeedLine(int unitIdx, string csv)
        {
            try
            {
                if (!SyncAuthority.ReceiveCombatState(BattleSync.IsHost)) return false;
                BattleUnitModel u = FindUnit(unitIdx);
                if (u == null) return false;
                ApplySpeeds(u, csv);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Where this unit has put its cards right now, as a wire string.  Used by
        /// the live placement broadcast so the other screen can see the plays while
        /// they are still being made, instead of only when the round gate opens.
        /// </summary>
        public static string CardsLine(BattleUnitModel u)
        {
            try { return CardCsv(u); }
            catch (Exception) { return "-"; }
        }

        /// <summary>Adopt the peer's card placements for one of the peer's units.</summary>
        public static bool ApplyCardsLine(int unitIdx, string csv)
        {
            try
            {
                BattleUnitModel u = FindUnit(unitIdx);
                if (u == null)
                {
                    Debug.LogWarning("[RuinaNet][net] no unit " + unitIdx +
                                     " on this side to put the peer's cards on");
                    return false;
                }
                if (!SyncAuthority.ReceiveChoices(BattleSync.IsHost, u.faction == Faction.Enemy, BattleSync.IsMine(u)))
                {
                    BattleSync.NoteDrop("cards", "authority refused unit " + unitIdx +
                                                 " (enemy=" + (u.faction == Faction.Enemy) +
                                                 " mine=" + BattleSync.IsMine(u) + ")");
                    return false;
                }
                ApplyCards(u, csv);

                // The original never draws a per-frame card icon -- the only things
                // that read cardAry are the die hover preview and the attack arrows,
                // so report what actually landed in the slots instead of guessing.
                int placed = 0;
                List<BattlePlayingCardDataInUnitModel> ary =
                    (u.cardSlotDetail != null) ? u.cardSlotDetail.cardAry : null;
                if (ary != null)
                {
                    for (int i = 0; i < ary.Count; i++) { if (ary[i] != null) placed++; }
                }
                Debug.Log("[RuinaNet][net] unit " + unitIdx + " now holds the peer's " +
                          placed + " placement(s) of " + ary.Count + " slot(s)");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][net] apply cards failed: " + e.Message);
                return false;
            }
        }

        public static bool HandleMessage(string[] p)
        {
            if (p == null || p.Length == 0) return false;
            if (!BattleSync.Active) return true;
            if (p[0] == "STATEBEGIN")
            {
                int r;
                if (p.Length < 2 || !int.TryParse(p[1], out r) || BattleSync.IsHost) return true;
                if (r != BattleSync.Round) { Stash(p, r); return true; }
                BeginRound();
                EgoSync.Reset();
                return true;
            }
            if (p[0] == "HAND2")
            {
                int r, wire, revision;
                if (p.Length < 4 || !int.TryParse(p[1], out r) || !int.TryParse(p[2], out wire) ||
                    !int.TryParse(p[3], out revision)) return true;
                if (!BattleSync.AcceptPlanningRound(r)) { Stash(p,r); return true; }
                if (!BattleSync.InCardPhase())
                {
                    string[] existing;
                    int oldRevision = -1;
                    if (_planningHands.TryGetValue(wire, out existing) && existing.Length > 3)
                        int.TryParse(existing[3], out oldRevision);
                    if (revision >= oldRevision) _planningHands[wire] = p;
                    Debug.Log("[RuinaNet][hand] buffered round=" + r + " unit=" + wire +
                              " revision=" + revision + " until arranging phase");
                    return true;
                }
                int appliedRevision;
                if (_handRevision.TryGetValue(wire, out appliedRevision) && revision < appliedRevision)
                {
                    Debug.Log("[RuinaNet][hand] ignored stale unit=" + wire + " revision=" + revision +
                              " newest=" + appliedRevision);
                    _handsApplied.Add(wire);
                    return true;
                }
                if (HandSync.Apply(p)) _handsApplied.Add(wire);
                else
                {
                    Debug.LogWarning("[RuinaNet][hand] rejected incomplete snapshot round=" + r + " unit=" + wire);
                    // Ask the peer to republish.  The sender dedups by content, so
                    // without this the gate would wait forever for a snapshot the
                    // other side believes it already sent.
                    NetLink.Send("HANDNAK " + r.ToString(CultureInfo.InvariantCulture) +
                                 " " + wire.ToString(CultureInfo.InvariantCulture));
                }
                if (_handsApplied.Contains(wire)) _handRevision[wire] = revision;
                return true;
            }
            if (p[0] == "USTATE") { ApplyUnit(p, false); return true; }
            if (p[0] == "VITALS") { ApplyVitals(p); return true; }
            if (p[0] == "VITALSEND") return true;
            if (p[0] == "STATEEND")
            {
                int round = -1;
                if (p.Length > 1) int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out round);
                if (round != BattleSync.Round)
                {
                    if (Stash(p, round)) return true;
                    // a late packet from a round we already left: ignore, or the
                    // host would think the peer is ready when it is not.
                    Debug.LogWarning("[RuinaNet][net] STATEEND for round " + round +
                                     " ignored (we are on " + BattleSync.Round + ")");
                    return true;
                }
                if (p.Length > 2)
                {
                    int n;
                    if (int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ExpectedUnits = n;
                }
                StateEnded = true;
                PeerStateComplete = ExpectedUnits >= 0 && _applied.Count == ExpectedUnits && _handsApplied.IsSupersetOf(_applied);
                Debug.Log("[RuinaNet][net] STATEEND round " + round +
                          " applied " + AppliedUnits + "/" + ExpectedUnits);
                BattleSync.OnPeerStateReady();
                return true;
            }
            if (p[0] == "STATEALL")
            {
                int round = -1;
                if (p.Length > 1) int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out round);
                if (round != BattleSync.Round)
                {
                    if (Stash(p, round)) return true;
                    Debug.LogWarning("[RuinaNet][net] STATEALL for round " + round +
                                     " ignored (we are on " + BattleSync.Round + ")");
                    return true;
                }
                int expected;
                string remoteDigest = p.Length > 3 ? p[3] : "";
                string localDigest = CommitDigest();
                StateAllDone = !BattleSync.IsHost && p.Length > 3 && int.TryParse(p[2], out expected) &&
                               expected == _applied.Count && _handsApplied.IsSupersetOf(_applied) &&
                               EgoSync.Complete && remoteDigest == localDigest;
                Debug.Log("[RuinaNet][net] STATEALL round " + round +
                          " -- host finished broadcasting, applied " + AppliedUnits + " unit(s), digest " +
                          localDigest + "/" + remoteDigest);
                if (StateAllDone) BattleSync.OnHostSnapshotReady(round, localDigest);
                else NetLink.Send("STATENAK " + round.ToString(CultureInfo.InvariantCulture) + " " +
                                  localDigest + " " + remoteDigest);
                return true;
            }
            return false;
        }

        private static void ApplyUnit(string[] p, bool ignoreRound)
        {
            ApplyUnit(p, ignoreRound, false);
        }

        private static void ApplyUnit(string[] p, bool ignoreRound, bool skipCards)
        {
            if (p.Length < 7) return;
            int round;
            if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out round)) return;
            if (!ignoreRound && round != BattleSync.Round)
            {
                // stale payload, or the peer got here first: keep it if it belongs
                // to the round we are about to enter.
                Stash(p, round);
                return;
            }
            int idx;
            if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) return;

            BattleUnitModel u = FindUnit(idx);
            if (u == null)
            {
                SkippedTotal++;
                if (SkippedTotal <= 8)
                    Debug.LogWarning("[RuinaNet][net] no local unit with index " + idx + " (skipped #" + SkippedTotal + ")");
                return;
            }
            if (!skipCards && !BattleSync.GateArmed) return;
            if (BattleSync.IsHost)
            {
                // A client owns page choices, never the simulation of enemies or HP.
                if (skipCards || u.faction != Faction.Player || BattleSync.IsMine(u)) return;
                if (p.Length > 7) ApplyCards(u, p[7]);
                _applied.Add(idx);
                AppliedUnits = _applied.Count;
                return;
            }
            if (!skipCards) _applied.Add(idx);
            AppliedUnits = _applied.Count;
            AppliedTotal++;

            float hp;
            if (float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out hp))
            {
                int want = (int)hp;
                if ((int)u.hp != want)
                {
                    // SetHp is a bare `hp = newHp;` -- no side effects, safe to overwrite.
                    u.SetHp(want);
                    Debug.Log("[RuinaNet][net] unit " + idx + " hp " + (int)u.hp + " -> " + want + " (correction)");
                }
            }

            int bg, bl;
            if (int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out bg) &&
                int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out bl))
            {
                try
                {
                    if (u.breakDetail != null)
                    {
                        u.breakDetail.breakGauge = bg;
                        u.breakDetail.breakLife = bl;
                    }
                }
                catch (Exception) { }
            }

            ApplySpeeds(u, p[6]);
            if (!skipCards && p.Length > 7) ApplyCards(u, p[7]);
            int light;
            if (!skipCards && p.Length > 8 && int.TryParse(p[8], out light)) u.cardSlotDetail.SetPlayPoint(light);
            BattleSync.RefreshUnitUi(u);
        }

        private static void ApplySpeeds(BattleUnitModel u, string csv)
        {
            if (string.IsNullOrEmpty(csv) || csv == "-") return;
            string[] parts = csv.Split(ListSep);
            List<SpeedDice> sd = u.speedDiceResult;

            // An empty list is worse than a null one: SortUnitPhase skips a unit that
            // already has dice, the hand UI reads .Count every frame, and every
            // correction below bails out on .Count == 0.  A unit that ends up here
            // fights the whole reception without a single speed die -- which is
            // exactly what "对方没有骰子" looks like.  The snapshot is a complete row
            // of dice, so rebuild the missing ones from it.
            if (sd == null || sd.Count == 0)
            {
                sd = new List<SpeedDice>(parts.Length);
                for (int i = 0; i < parts.Length; i++) sd.Add(new SpeedDice());
                u.speedDiceResult = sd;
                Debug.Log("[RuinaNet][net] unit " + u.index + " had no speed dice -- rebuilt " +
                          sd.Count + " from the peer's snapshot");
            }

            bool changed = sd.Count != parts.Length;
            while (sd.Count < parts.Length) sd.Add(new SpeedDice());
            if (sd.Count > parts.Length) sd.RemoveRange(parts.Length, sd.Count - parts.Length);
            int n = parts.Length;
            for (int i = 0; i < n; i++)
            {
                SpeedDice d = sd[i];
                if (d == null) continue;
                string s = parts[i];
                if (string.IsNullOrEmpty(s) || s == "-") continue;
                bool broken = false;
                if (s.Length > 0 && (s[0] == 'b' || s[0] == 'B')) { broken = true; s = s.Substring(1); }
                int v;
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) continue;
                if (d.value != v || d.breaked != broken) changed = true;
                d.value = v;
                d.breaked = broken;
                if (u.cardSlotDetail != null && i < u.cardSlotDetail.cardAry.Count && u.cardSlotDetail.cardAry[i] != null)
                    u.cardSlotDetail.cardAry[i].speedDiceResultValue = v;
            }
            if (!changed) return;

            try
            {
                if (u.view != null && u.view.speedDiceSetterUI != null)
                    u.view.speedDiceSetterUI.SetSpeedDicesAfterRoll(sd);
            }
            catch (Exception) { }
            Debug.Log("[RuinaNet][net] unit " + u.index + " speed dice corrected to " + csv);
        }

        private static void ApplyCards(BattleUnitModel u, string csv)
        {
            if (u.cardSlotDetail == null) return;
            List<BattlePlayingCardDataInUnitModel> ary = u.cardSlotDetail.cardAry;
            if (ary == null || ary.Count == 0) return;

            string[] entries = string.IsNullOrEmpty(csv) ? new string[0] : csv.Split(ListSep);
            List<ClashSync.Choice> clashBefore = ClashSync.Capture(u);
            for (int i = 0; i < ary.Count; i++)
            {
                string entry = i < entries.Length ? entries[i] : "-";
                PlaceCard(u, i, entry);
            }
            ClashSync.After(u, clashBefore);
        }

        /// <summary>
        /// Put one card into one speed-dice slot without going through
        /// BattlePlayingCardSlotDetail.AddCard.  AddCard picks the slot itself and
        /// rolls Random.Range for far-area sub-targets and RandomUtil.SelectOne for
        /// taunt swaps, so replaying it on two machines drifts.  This mirror of the
        /// construction path takes the slot and the sub-target list as given.
        /// </summary>
        private static void PlaceCard(BattleUnitModel u, int slot, string entry)
        {
            List<BattlePlayingCardDataInUnitModel> ary = u.cardSlotDetail.cardAry;
            if (ary == null || slot < 0 || slot >= ary.Count) return;

            string[] f = string.IsNullOrEmpty(entry) ? new string[0] : entry.Split(',');
            string tag = f.Length > 0 && f[0].Length > 0 ? f[0] : "-";
            int tgtIdx = -1;
            int tgtSlot = 0;
            if (f.Length > 1) int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out tgtIdx);
            if (f.Length > 2) int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out tgtSlot);
            string subs = f.Length > 3 ? f[3] : "-";

            BattlePlayingCardDataInUnitModel cur = ary[slot];

            // Retargeting must preserve the card's runtime buffs and cost.
            bool samePage = cur != null && tag != "-" && CardTag(cur) == tag;
            if (cur == null && tag == "-") return;
            if (cur != null && !samePage)
            {
                if (cur.cardAbility != null) cur.cardAbility.OnReleaseCard();
                if (cur.card.XmlData.IsFloorEgo()) Singleton<SpecialCardListModel>.Instance.ReturnCardToHand(u, cur.card);
                else if (cur.card.XmlData.IsPersonal()) u.personalEgoDetail.ReturnCardToHand(cur.card);
                else u.allyCardDetail.ReturnCardToHand(cur.card);
                ary[slot] = null;
            }
            if (tag == "-") return;

            BattlePlayingCardDataInUnitModel m = samePage ? cur : new BattlePlayingCardDataInUnitModel();
            BattleUnitModel tgt = FindUnit(tgtIdx);
            m.owner = u;
            m.target = tgt;
            // A redirected enemy page still needs its original AI target when
            // the redirecting librarian cancels. Periodic snapshots must not
            // replace that baseline with the current clash target.
            if (!samePage || u.faction != Faction.Enemy)
            {
                m.earlyTarget = tgt;
                m.earlyTargetOrder = tgtSlot;
            }
            m.targetSlotOrder = tgtSlot;
            m.speedDiceResultValue = SpeedValueAt(u, slot);
            m.slotOrder = slot;
            if (!samePage)
            {
                DiceCardXmlInfo info = ItemXmlDataList.instance.GetCardItem(ParseTag(tag), true);
                if (info == null) throw new InvalidOperationException("Unknown peer page " + tag);
                List<BattleDiceCardModel> hand = info.IsFloorEgo() ? Singleton<SpecialCardListModel>.Instance.GetHand()
                    : (info.IsPersonal() ? u.personalEgoDetail.GetHand() : u.allyCardDetail.GetHand());
                BattleDiceCardModel cm = hand.Find(delegate(BattleDiceCardModel c) { return c.GetID() == ParseTag(tag); });
                if (cm == null) cm = BattleDiceCardModel.CreatePlayingCard(info);
                cm.owner = u;
                m.card = cm;
                m.cardAbility = cm.CreateDiceCardSelfAbilityScript();
                if (m.cardAbility != null) { m.cardAbility.card = m; m.cardAbility.OnApplyCard(); }
                m.ResetCardQueue();
                if (info.IsFloorEgo()) Singleton<SpecialCardListModel>.Instance.UseCard(cm);
                else if (info.IsPersonal()) u.personalEgoDetail.UseCard(cm);
                else u.allyCardDetail.UseCard(cm);
                u.OnApplyCard(cm);
            }
            if (m.subTargets != null) m.subTargets.Clear();

            if (subs != "-" && subs.Length > 0)
            {
                try
                {
                    string[] ss = subs.Split(SubSep);
                    if (m.subTargets == null) m.subTargets = new List<BattlePlayingCardDataInUnitModel.SubTarget>();
                    for (int i = 0; i < ss.Length; i++)
                    {
                        string[] one = ss[i].Split(':');
                        if (one.Length < 2) continue;
                        int si, sslot;
                        if (!int.TryParse(one[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out si)) continue;
                        if (!int.TryParse(one[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sslot)) continue;
                        BattleUnitModel st = FindUnit(si);
                        if (st == null) continue;
                        BattlePlayingCardDataInUnitModel.SubTarget sub = new BattlePlayingCardDataInUnitModel.SubTarget();
                        sub.target = st;
                        sub.targetSlotOrder = sslot;
                        m.subTargets.Add(sub);
                    }
                }
                catch (Exception) { }
            }

            ary[slot] = m;
            Debug.Log("[RuinaNet][net] unit " + u.index + " slot " + slot + " <- " + tag +
                      " tgt=" + tgtIdx + "@" + tgtSlot);
        }

        private static bool SameCard(BattlePlayingCardDataInUnitModel cur, string tag, int tgtIdx, int tgtSlot)
        {
            if (tag == "-") return false;
            try
            {
                if (CardTag(cur) != tag) return false;
                int curTgt = cur.target == null ? -1 : WireIndex(cur.target);
                if (curTgt != tgtIdx) return false;
                if (cur.targetSlotOrder != tgtSlot) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static int SpeedValueAt(BattleUnitModel u, int slot)
        {
            try
            {
                SpeedDice d = u.GetSpeedDiceResult(slot);
                return d == null ? 0 : d.value;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// Index of a unit ON THE WIRE.  BattleUnitModel.index is numbered per
        /// faction -- StageController.CreateLibrarianUnit passes the librarian's
        /// list position and CreateEnemyUnitByWave restarts its counter at 0 -- so
        /// player 2 and enemy 2 both say "2".  Sending that raw made every enemy
        /// packet land on a librarian (and vice versa): the enemy's speed dice were
        /// never the ones being written, which is what "the other side's enemy dice
        /// never settle" looked like.  Enemies therefore go out offset by EnemyBase.
        /// </summary>
        public const int EnemyBase = 100;

        public static int WireIndex(BattleUnitModel u)
        {
            if (u == null) return -1;
            try
            {
                if (u.faction == Faction.Enemy) return EnemyBase + u.index;
                return u.index;
            }
            catch (Exception) { return u.index; }
        }

        /// <summary>
        /// Public lookup for other channels (BufSync) that also address units by
        /// wire index.
        /// </summary>
        public static BattleUnitModel Find(int wireIdx)
        {
            return FindUnit(wireIdx);
        }

        private static BattleUnitModel FindUnit(int idx)
        {            if (idx < 0) return null;
            bool enemy = idx >= EnemyBase;
            int local = enemy ? idx - EnemyBase : idx;
            try
            {
                List<BattleUnitModel> all = BattleObjectManager.instance.GetAliveList(true);
                if (all == null) return null;
                for (int i = 0; i < all.Count; i++)
                {
                    BattleUnitModel u = all[i];
                    if (u == null || u.index != local) continue;
                    if ((u.faction == Faction.Enemy) != enemy) continue;
                    return u;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static LorId ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag == "-") return LorId.None;
            int c = tag.IndexOf(':');
            if (c < 0)
            {
                int only;
                if (int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out only)) return new LorId(only);
                return LorId.None;
            }
            string pid = tag.Substring(0, c);
            int id;
            if (!int.TryParse(tag.Substring(c + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return LorId.None;
            if (pid.Length == 0 || pid == "base") return new LorId(id);
            return new LorId(pid, id);
        }

        /// <summary>
        /// Round-start hygiene: drop every dangling target reference the
        /// placements still hold.  A unit that died mid-fight stays reachable
        /// through cardAry (the snapshot only rewires slots it was told about),
        /// and the targeting arrow then points at a corpse -- or the game's own
        /// reads throw.  Called when a new arranging phase begins.
        /// </summary>
        public static int ClearDeadTargets()
        {
            int n = 0;
            try
            {
                List<BattleUnitModel> all = BattleObjectManager.instance.GetAliveList(true);
                if (all == null) return 0;
                for (int i = 0; i < all.Count; i++)
                {
                    BattleUnitModel u = all[i];
                    if (u == null || u.cardSlotDetail == null || u.cardSlotDetail.cardAry == null) continue;
                    List<BattlePlayingCardDataInUnitModel> ary = u.cardSlotDetail.cardAry;
                    for (int s = 0; s < ary.Count; s++)
                    {
                        BattlePlayingCardDataInUnitModel m = ary[s];
                        if (m == null) continue;
                        if (Dead(m.target)) { m.target = null; n++; }
                        if (Dead(m.earlyTarget)) { m.earlyTarget = null; n++; }
                        if (m.subTargets != null)
                        {
                            int removed = m.subTargets.RemoveAll(delegate(BattlePlayingCardDataInUnitModel.SubTarget st)
                            {
                                return st != null && Dead(st.target);
                            });
                            n += removed;
                        }
                    }
                }
            }
            catch (Exception) { }
            return n;
        }

        private static bool Dead(BattleUnitModel u)
        {
            try { return u != null && u.IsDead(); }
            catch (Exception) { return false; }
        }
    }
}
