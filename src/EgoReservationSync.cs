using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RuinaNet
{
    /// <summary>
    /// Host-arbitrated leases for the floor-wide EGO hand.  The first side to
    /// reserve a page owns it until it is cancelled or the next planning phase
    /// begins.  Lease ids and monotonically increasing revisions make delayed UDP
    /// release/state packets harmless.
    /// </summary>
    internal static class EgoReservationSync
    {
        internal sealed class ApplyState
        {
            public BattleUnitModel Source;
            public BattleDiceCardModel Card;
            public string Key;
            public int Lease;
        }

        internal sealed class RemoveState
        {
            public BattleUnitModel Unit;
            public BattleDiceCardModel Card;
            public string Key;
            public int Lease;
            public int Slot;
        }

        private sealed class Entry
        {
            public int Round;
            public string Key;
            public int Owner;       // 0 host, 1 client
            public int Lease;
            public int Revision;
            public bool Committed;
            public float ExpiresAt;
        }

        private sealed class Pending
        {
            public int Request;
            public int Round;
            public string Key;
            public float LastSent;
        }

        private static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private static readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private static readonly Dictionary<string, int> _latestRevision = new Dictionary<string, int>();
        private static int _nextRequest = 1;
        private static int _nextLease = 1;
        private static int _revision;
        private static int _planningTurn = int.MinValue;
        private static float _lastNotice = -10f;

        private const float LeaseSeconds = 6f;
        private const float RequestRetry = 0.75f;

        public static void Reset()
        {
            _entries.Clear();
            _pending.Clear();
            _latestRevision.Clear();
            _nextRequest = 1;
            _nextLease = 1;
            _revision = 0;
            _planningTurn = int.MinValue;
        }

        public static void BeginPlanning(int gameRound)
        {
            if (_planningTurn == gameRound) return;
            _planningTurn = gameRound;
            _entries.Clear();
            _pending.Clear();
            _latestRevision.Clear();
            Debug.Log("[RuinaNet][ego-lease] planning turn " + gameRound + " reset");
        }

        private static int PlanningRound()
        {
            try
            {
                StageController sc = Singleton<StageController>.Instance;
                if (sc != null) return sc.RoundTurn;
            }
            catch (Exception) { }
            return BattleSync.Round;
        }

        public static string KeyOf(BattleDiceCardModel card)
        {
            if (card == null) return "-";
            LorId id = card.GetID();
            string package = string.IsNullOrEmpty(id.packageId) ? "base" : Uri.EscapeDataString(id.packageId);
            return package + ":" + id.id.ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsFloorEgo(BattleDiceCardModel card)
        {
            try { return card != null && card.XmlData != null && card.XmlData.IsFloorEgo(); }
            catch (Exception) { return false; }
        }

        /// <summary>Called before vanilla moves the selected card out of the hand.</summary>
        public static bool BeforeApply(BattleUnitModel source, BattleDiceCardModel card, out ApplyState state)
        {
            state = null;
            if (!BattleSync.Active || !IsFloorEgo(card)) return true;
            string key = KeyOf(card);
            int round = PlanningRound();

            Entry current;
            if (BattleSync.IsHost)
            {
                if (_entries.TryGetValue(key, out current) && current.Round == round)
                {
                    if (current.Owner != 0)
                    {
                        Notice("这张楼层 E.G.O. 已由队友占用");
                        return false;
                    }
                }
                else
                {
                    current = NewEntry(round, key, 0);
                    _entries[key] = current;
                    SendState(current, "R");
                }
                state = MakeApplyState(source, card, current);
                return true;
            }

            if (_entries.TryGetValue(key, out current) && current.Round == round)
            {
                if (current.Owner == 1)
                {
                    state = MakeApplyState(source, card, current);
                    return true;
                }
                Notice("这张楼层 E.G.O. 已由房主占用");
                return false;
            }

            Pending pending;
            if (!_pending.TryGetValue(key, out pending) || pending.Round != round)
            {
                pending = new Pending();
                pending.Request = _nextRequest++;
                pending.Round = round;
                pending.Key = key;
                _pending[key] = pending;
            }
            SendRequest(pending);
            Notice("正在向房主申请这张楼层 E.G.O.，获准后再点一次目标");
            return false;
        }

        private static ApplyState MakeApplyState(BattleUnitModel source, BattleDiceCardModel card, Entry entry)
        {
            ApplyState s = new ApplyState();
            s.Source = source;
            s.Card = card;
            s.Key = entry.Key;
            s.Lease = entry.Lease;
            return s;
        }

        public static void AfterApply(ApplyState state)
        {
            if (state == null || state.Card == null || !IsPlaced(state.Source, state.Card)) return;
            if (BattleSync.IsHost)
            {
                Entry e;
                if (_entries.TryGetValue(state.Key, out e) && e.Lease == state.Lease && e.Owner == 0)
                {
                    e.Committed = true;
                    e.Revision = ++_revision;
                    SendState(e, "C");
                }
            }
            else
            {
                NetLink.Send("EGOCOMMIT " + PlanningRound().ToString(CultureInfo.InvariantCulture) + " " +
                             state.Lease.ToString(CultureInfo.InvariantCulture) + " " + state.Key);
            }
        }

        private static bool IsPlaced(BattleUnitModel unit, BattleDiceCardModel card)
        {
            try
            {
                if (unit == null || unit.cardSlotDetail == null || unit.cardSlotDetail.cardAry == null) return false;
                for (int i = 0; i < unit.cardSlotDetail.cardAry.Count; i++)
                {
                    BattlePlayingCardDataInUnitModel p = unit.cardSlotDetail.cardAry[i];
                    if (p != null && p.card == card) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        public static RemoveState BeforeRemove(BattleUnitModel unit, int slot)
        {
            try
            {
                if (!BattleSync.Active || unit == null || unit.cardSlotDetail == null ||
                    unit.cardSlotDetail.cardAry == null || slot < 0 || slot >= unit.cardSlotDetail.cardAry.Count) return null;
                BattlePlayingCardDataInUnitModel placed = unit.cardSlotDetail.cardAry[slot];
                if (placed == null || !IsFloorEgo(placed.card)) return null;
                string key = KeyOf(placed.card);
                Entry e;
                if (!_entries.TryGetValue(key, out e)) return null;
                RemoveState s = new RemoveState();
                s.Unit = unit; s.Card = placed.card; s.Key = key; s.Lease = e.Lease; s.Slot = slot;
                return s;
            }
            catch (Exception) { return null; }
        }

        public static void AfterRemove(RemoveState state)
        {
            if (state == null) return;
            try
            {
                if (state.Unit != null && state.Unit.cardSlotDetail != null && state.Unit.cardSlotDetail.cardAry != null &&
                    state.Slot >= 0 && state.Slot < state.Unit.cardSlotDetail.cardAry.Count)
                {
                    BattlePlayingCardDataInUnitModel now = state.Unit.cardSlotDetail.cardAry[state.Slot];
                    if (now != null && now.card == state.Card) return;
                }
            }
            catch (Exception) { }
            ReleaseLocal(state.Key, state.Lease);
        }

        private static void ReleaseLocal(string key, int lease)
        {
            if (BattleSync.IsHost)
            {
                Entry e;
                if (_entries.TryGetValue(key, out e) && e.Lease == lease && e.Owner == 0)
                {
                    _entries.Remove(key);
                    SendAvailable(PlanningRound(), key, ++_revision, lease);
                }
            }
            else
            {
                NetLink.Send("EGOREL " + PlanningRound().ToString(CultureInfo.InvariantCulture) + " " +
                             lease.ToString(CultureInfo.InvariantCulture) + " " + key);
            }
        }

        private static Entry NewEntry(int round, string key, int owner)
        {
            Entry e = new Entry();
            e.Round = round; e.Key = key; e.Owner = owner; e.Lease = _nextLease++;
            e.Revision = ++_revision; e.Committed = false; e.ExpiresAt = Time.unscaledTime + LeaseSeconds;
            return e;
        }

        public static bool HandleMessage(string[] p)
        {
            if (p == null || p.Length == 0) return false;
            if (p[0] == "EGOREQ") { HandleRequest(p); return true; }
            if (p[0] == "EGOGRANT") { HandleGrant(p); return true; }
            if (p[0] == "EGODENY") { HandleDeny(p); return true; }
            if (p[0] == "EGOSTATE") { HandleState(p); return true; }
            if (p[0] == "EGOREL") { HandleRelease(p); return true; }
            if (p[0] == "EGOCOMMIT") { HandleCommit(p); return true; }
            return false;
        }

        private static void HandleRequest(string[] p)
        {
            int round, request;
            if (!BattleSync.IsHost || p.Length < 4 || !int.TryParse(p[1], out round) || !int.TryParse(p[2], out request)) return;
            string key = p[3];
            if (round != PlanningRound() || !CardAvailable(key))
            {
                SendDeny(round, request, key, "unavailable");
                return;
            }
            Entry e;
            if (_entries.TryGetValue(key, out e) && e.Round == round)
            {
                if (e.Owner != 1) { SendDeny(round, request, key, "occupied"); return; }
            }
            else
            {
                e = NewEntry(round, key, 1);
                _entries[key] = e;
                SendState(e, "R");
            }
            NetLink.Send("EGOGRANT " + round.ToString(CultureInfo.InvariantCulture) + " " +
                         request.ToString(CultureInfo.InvariantCulture) + " " + e.Lease.ToString(CultureInfo.InvariantCulture) + " " +
                         e.Revision.ToString(CultureInfo.InvariantCulture) + " " + key);
        }

        private static bool CardAvailable(string key)
        {
            try
            {
                SpecialCardListModel floor = Singleton<SpecialCardListModel>.Instance;
                if (floor == null) return false;
                List<BattleDiceCardModel> hand = floor.GetHand();
                if (hand == null) return false;
                for (int i = 0; i < hand.Count; i++)
                    if (KeyOf(hand[i]) == key && hand[i].CurrentCooltimeValue <= 0f) return true;
            }
            catch (Exception) { }
            return false;
        }

        private static void HandleGrant(string[] p)
        {
            int round, request, lease, revision;
            if (BattleSync.IsHost || p.Length < 6 || !int.TryParse(p[1], out round) || !int.TryParse(p[2], out request) ||
                !int.TryParse(p[3], out lease) || !int.TryParse(p[4], out revision) || round != PlanningRound()) return;
            string key = p[5];
            Pending pending;
            if (_pending.TryGetValue(key, out pending) && pending.Request == request) _pending.Remove(key);
            PutRemote(round, key, 1, lease, revision, false);
            Notice("楼层 E.G.O. 已获房主批准，请再次点击目标放置");
        }

        private static void HandleDeny(string[] p)
        {
            int round, request, revision;
            if (BattleSync.IsHost || p.Length < 6 || !int.TryParse(p[1], out round) || !int.TryParse(p[2], out request) ||
                !int.TryParse(p[3], out revision) || round != PlanningRound()) return;
            string key = p[4];
            Pending pending;
            if (_pending.TryGetValue(key, out pending) && pending.Request == request) _pending.Remove(key);
            Notice("这张楼层 E.G.O. 当前不可用");
        }

        private static void HandleState(string[] p)
        {
            int round, revision, owner, lease, committed;
            if (BattleSync.IsHost || p.Length < 8 || !int.TryParse(p[1], out round) || !int.TryParse(p[2], out revision) ||
                !int.TryParse(p[4], out owner) || !int.TryParse(p[5], out lease) || !int.TryParse(p[6], out committed) ||
                round != PlanningRound()) return;
            string key = p[3];
            if (p[7] == "A")
            {
                int latest;
                if (_latestRevision.TryGetValue(key, out latest) && revision < latest) return;
                _latestRevision[key] = revision;
                _entries.Remove(key);
                _pending.Remove(key);
                return;
            }
            PutRemote(round, key, owner, lease, revision, committed != 0);
        }

        private static void PutRemote(int round, string key, int owner, int lease, int revision, bool committed)
        {
            int latest;
            if (_latestRevision.TryGetValue(key, out latest) && revision < latest) return;
            _latestRevision[key] = revision;
            Entry e = new Entry();
            e.Round = round; e.Key = key; e.Owner = owner; e.Lease = lease; e.Revision = revision;
            e.Committed = committed; e.ExpiresAt = float.MaxValue;
            _entries[key] = e;
            if (owner == 1) _pending.Remove(key);
        }

        private static void HandleRelease(string[] p)
        {
            int round, lease;
            if (!BattleSync.IsHost || p.Length < 4 || !int.TryParse(p[1], out round) || !int.TryParse(p[2], out lease) ||
                round != PlanningRound()) return;
            string key = p[3];
            Entry e;
            if (!_entries.TryGetValue(key, out e) || e.Lease != lease || e.Owner != 1) return;
            _entries.Remove(key);
            SendAvailable(round, key, ++_revision, lease);
        }

        private static void HandleCommit(string[] p)
        {
            int round, lease;
            if (!BattleSync.IsHost || p.Length < 4 || !int.TryParse(p[1], out round) || !int.TryParse(p[2], out lease) ||
                round != PlanningRound()) return;
            string key = p[3];
            Entry e;
            if (!_entries.TryGetValue(key, out e) || e.Lease != lease || e.Owner != 1) return;
            e.Committed = true;
            e.Revision = ++_revision;
            SendState(e, "C");
        }

        private static void SendRequest(Pending p)
        {
            p.LastSent = Time.unscaledTime;
            NetLink.Send("EGOREQ " + p.Round.ToString(CultureInfo.InvariantCulture) + " " +
                         p.Request.ToString(CultureInfo.InvariantCulture) + " " + p.Key);
        }

        private static void SendDeny(int round, int request, string key, string reason)
        {
            NetLink.Send("EGODENY " + round.ToString(CultureInfo.InvariantCulture) + " " +
                         request.ToString(CultureInfo.InvariantCulture) + " " + _revision.ToString(CultureInfo.InvariantCulture) +
                         " " + key + " " + reason);
        }

        private static void SendState(Entry e, string state)
        {
            NetLink.Send("EGOSTATE " + e.Round.ToString(CultureInfo.InvariantCulture) + " " +
                         e.Revision.ToString(CultureInfo.InvariantCulture) + " " + e.Key + " " +
                         e.Owner.ToString(CultureInfo.InvariantCulture) + " " + e.Lease.ToString(CultureInfo.InvariantCulture) + " " +
                         (e.Committed ? "1" : "0") + " " + state);
        }

        private static void SendAvailable(int round, string key, int revision, int oldLease)
        {
            NetLink.Send("EGOSTATE " + round.ToString(CultureInfo.InvariantCulture) + " " +
                         revision.ToString(CultureInfo.InvariantCulture) + " " + key + " -1 " +
                         oldLease.ToString(CultureInfo.InvariantCulture) + " 0 A");
        }

        public static void Tick(float dt)
        {
            if (!BattleSync.Active) return;
            if (BattleSync.IsHost)
            {
                List<Entry> expired = new List<Entry>();
                foreach (KeyValuePair<string, Entry> pair in _entries)
                    if (!pair.Value.Committed && Time.unscaledTime >= pair.Value.ExpiresAt) expired.Add(pair.Value);
                for (int i = 0; i < expired.Count; i++)
                {
                    Entry e = expired[i];
                    Entry current;
                    if (!_entries.TryGetValue(e.Key, out current) || current.Lease != e.Lease) continue;
                    _entries.Remove(e.Key);
                    SendAvailable(e.Round, e.Key, ++_revision, e.Lease);
                }
                return;
            }
            List<Pending> retry = new List<Pending>();
            foreach (KeyValuePair<string, Pending> pair in _pending)
                if (Time.unscaledTime - pair.Value.LastSent >= RequestRetry) retry.Add(pair.Value);
            for (int i = 0; i < retry.Count; i++) SendRequest(retry[i]);
        }

        public static string CommitBody()
        {
            List<string> rows = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                Entry e = pair.Value;
                if (e.Round != PlanningRound()) continue;
                rows.Add(e.Key + "," + e.Owner.ToString(CultureInfo.InvariantCulture) + "," +
                         e.Lease.ToString(CultureInfo.InvariantCulture) + "," + (e.Committed ? "1" : "0"));
            }
            rows.Sort(StringComparer.Ordinal);
            return rows.Count == 0 ? "-" : string.Join("~", rows.ToArray());
        }

        private static void Notice(string text)
        {
            if (Time.unscaledTime - _lastNotice < 0.5f) return;
            _lastNotice = Time.unscaledTime;
            Room.Notice(text);
        }
    }
}
