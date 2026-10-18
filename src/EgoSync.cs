using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using LOR_DiceSystem;
using UnityEngine;

namespace RuinaNet
{
    /// <summary>Host-authoritative floor and personal EGO card containers.</summary>
    internal static class EgoSync
    {
        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly string[] FloorNames = { "_cardInHand", "_cardInReserved", "_cardInUse", "_handEtc" };
        private static readonly string[] PersonalNames = { "_cardsAll", "_cardInHand", "_cardInUse", "_cardInReserved" };
        private static readonly FieldInfo[] FloorFields = Fields(typeof(SpecialCardListModel), FloorNames);
        private static readonly FieldInfo[] PersonalFields = Fields(typeof(BattlePersonalEgoCardDetail), PersonalNames);
        private static readonly FieldInfo CurrentCool = typeof(BattleDiceCardModel).GetField("currentCooltimeValue", Hidden);
        private static readonly FieldInfo MaxCool = typeof(BattleDiceCardModel).GetField("maxCooltimeValue", Hidden);
        private static readonly FieldInfo CostAdder = typeof(BattleDiceCardModel).GetField("_costAdder", Hidden);
        private static readonly FieldInfo CostZero = typeof(BattleDiceCardModel).GetField("_costZero", Hidden);

        public static bool Complete;
        private static int _expected;
        private static int _applied;
        private static float _liveWait;
        private static readonly Dictionary<int, string> LiveSent = new Dictionary<int, string>();

        private static FieldInfo[] Fields(Type type, string[] names)
        {
            FieldInfo[] result = new FieldInfo[names.Length];
            for (int i = 0; i < names.Length; i++) result[i] = type.GetField(names[i], Hidden);
            return result;
        }

        public static void Reset()
        {
            Complete = false;
            _expected = -1;
            _applied = 0;
            _liveWait = 0f;
            LiveSent.Clear();
        }

        public static void BeginPlanning()
        {
            _liveWait = 1f;
            LiveSent.Clear();
        }

        public static int SendAll(int round)
        {
            if (!BattleSync.Active || !BattleSync.IsHost) return 0;
            int count = 0;
            try
            {
                SpecialCardListModel floor = Singleton<SpecialCardListModel>.Instance;
                if (floor != null && Send(round, "F", -1, floor, FloorFields, null)) count++;
                List<BattleUnitModel> players = BattleObjectManager.instance.GetAliveList(Faction.Player);
                if (players != null)
                {
                    for (int i = 0; i < players.Count; i++)
                    {
                        BattleUnitModel u = players[i];
                        if (u != null && u.personalEgoDetail != null &&
                            Send(round, "P", NetState.WireIndex(u), u.personalEgoDetail, PersonalFields, u)) count++;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][ego] snapshot failed: " + e.Message);
            }
            NetLink.Send("EGOEND " + round.ToString(CultureInfo.InvariantCulture) + " " +
                         count.ToString(CultureInfo.InvariantCulture));
            return count;
        }

        private static List<BattleDiceCardModel> List(object owner, FieldInfo field)
        {
            if (field == null) throw new InvalidOperationException("Missing EGO container field");
            List<BattleDiceCardModel> list = field.GetValue(owner) as List<BattleDiceCardModel>;
            if (list == null) throw new InvalidOperationException("Uninitialized EGO container field " + field.Name);
            return list;
        }

        private static int Index(List<BattleDiceCardModel> pool, BattleDiceCardModel card)
        {
            int index = pool.IndexOf(card);
            if (index >= 0) return index;
            pool.Add(card);
            return pool.Count - 1;
        }

        private static string Zone(List<BattleDiceCardModel> pool, List<BattleDiceCardModel> zone)
        {
            if (zone.Count == 0) return "-";
            string[] values = new string[zone.Count];
            for (int i = 0; i < zone.Count; i++) values[i] = Index(pool, zone[i]).ToString(CultureInfo.InvariantCulture);
            return string.Join(",", values);
        }

        private static string Tag(BattleDiceCardModel card)
        {
            LorId id = card.GetID();
            string package = string.IsNullOrEmpty(id.packageId) ? "base" : id.packageId;
            return Uri.EscapeDataString(package) + ":" + id.id.ToString(CultureInfo.InvariantCulture);
        }

        private static bool Send(int round, string kind, int wire, object owner,
                                 FieldInfo[] fields, BattleUnitModel unit)
        {
            string body = BuildBody(owner, fields, unit);
            NetLink.Send("EGO2 " + round.ToString(CultureInfo.InvariantCulture) + " " + kind + " " +
                         wire.ToString(CultureInfo.InvariantCulture) + " " + body);
            return true;
        }

        private static string BuildBody(object owner, FieldInfo[] fields, BattleUnitModel unit)
        {
            List<BattleDiceCardModel> pool = new List<BattleDiceCardModel>();
            string[] zones = new string[fields.Length];
            for (int i = 0; i < fields.Length; i++) zones[i] = Zone(pool, List(owner, fields[i]));

            // Reuse the exact runtime object when an EGO page is currently placed.
            if (unit != null && unit.cardSlotDetail != null)
            {
                foreach (BattlePlayingCardDataInUnitModel placed in unit.cardSlotDetail.cardAry)
                    if (placed != null && placed.card != null && placed.card.XmlData.IsPersonal()) Index(pool, placed.card);
            }

            string[] defs = new string[pool.Count];
            for (int i = 0; i < pool.Count; i++)
            {
                BattleDiceCardModel card = pool[i];
                float current = CurrentCool == null ? card.CurrentCooltimeValue : (float)CurrentCool.GetValue(card);
                float max = MaxCool == null ? card.MaxCooltimeValue : (float)MaxCool.GetValue(card);
                int add = CostAdder == null ? 0 : (int)CostAdder.GetValue(card);
                bool zero = CostZero != null && (bool)CostZero.GetValue(card);
                defs[i] = Tag(card) + "," + current.ToString("R", CultureInfo.InvariantCulture) + "," +
                          max.ToString("R", CultureInfo.InvariantCulture) + "," +
                          card.CurCost.ToString(CultureInfo.InvariantCulture) + "," +
                          add.ToString(CultureInfo.InvariantCulture) + "," + (zero ? "1" : "0") + "," +
                          (card.temporary ? "1" : "0") + "," + (card.exhaust ? "1" : "0") + "," +
                          (card.costSpended ? "1" : "0");
            }
            return (defs.Length == 0 ? "-" : string.Join("~", defs)) + " " + string.Join(" ", zones);
        }

        private static int StageTurn()
        {
            try
            {
                StageController sc = Singleton<StageController>.Instance;
                if (sc != null) return sc.RoundTurn;
            }
            catch (Exception) { }
            return BattleSync.Round;
        }

        public static void Tick(float dt)
        {
            if (!BattleSync.Active || !BattleSync.InCardPhase() || BattleSync.GateArmed) return;
            _liveWait += dt;
            if (_liveWait < 0.2f) return;
            _liveWait = 0f;
            SendOwnedPersonalLive(false);
        }

        public static void SendOwnedPersonalLive(bool force)
        {
            if (!BattleSync.Active) return;
            List<BattleUnitModel> mine = BattleSync.MyUnits();
            for (int i = 0; i < mine.Count; i++)
            {
                BattleUnitModel u = mine[i];
                if (u == null || u.personalEgoDetail == null) continue;
                int wire = NetState.WireIndex(u);
                string body = BuildBody(u.personalEgoDetail, PersonalFields, u);
                string was;
                if (!force && LiveSent.TryGetValue(wire, out was) && was == body) continue;
                LiveSent[wire] = body;
                NetLink.Send("EGOLIVE " + StageTurn().ToString(CultureInfo.InvariantCulture) + " " +
                             wire.ToString(CultureInfo.InvariantCulture) + " " + body);
                Debug.Log("[RuinaNet][ego] published personal EGO state for unit " + wire);
            }
        }

        public static bool HandleMessage(string[] p)
        {
            if (p == null || p.Length == 0) return false;
            if (p[0] == "EGOLIVE")
            {
                int turn, liveWire;
                if (p.Length != 8 || !int.TryParse(p[1], out turn) || !int.TryParse(p[2], out liveWire) ||
                    turn != StageTurn()) return true;
                BattleUnitModel unit = NetState.Find(liveWire);
                if (unit == null || unit.faction != Faction.Player || unit.personalEgoDetail == null) return true;
                if (BattleSync.IsMine(unit)) return true;
                try
                {
                    Apply(unit.personalEgoDetail, PersonalFields, unit, p[3], new[] { p[4], p[5], p[6], p[7] });
                    Debug.Log("[RuinaNet][ego] adopted peer personal EGO state for unit " + liveWire);
                }
                catch (Exception e) { Debug.LogWarning("[RuinaNet][ego] live apply failed: " + e.Message); }
                return true;
            }
            if (p[0] == "EGOEND")
            {
                int round, expected;
                if (p.Length < 3 || !int.TryParse(p[1], out round) || !int.TryParse(p[2], out expected)) return true;
                if (BattleSync.IsHost || round != BattleSync.Round) return true;
                _expected = expected;
                Complete = _applied == _expected;
                Debug.Log("[RuinaNet][ego] EGOEND round=" + round + " applied=" + _applied + "/" + _expected);
                return true;
            }
            if (p[0] != "EGO2") return false;
            int r, wire;
            if (p.Length != 9 || !int.TryParse(p[1], out r) || !int.TryParse(p[3], out wire)) return true;
            if (BattleSync.IsHost || r != BattleSync.Round) return true;
            try
            {
                bool personal = p[2] == "P";
                object owner;
                FieldInfo[] fields;
                BattleUnitModel unit = null;
                if (personal)
                {
                    unit = NetState.Find(wire);
                    if (unit == null || unit.personalEgoDetail == null) return true;
                    owner = unit.personalEgoDetail;
                    fields = PersonalFields;
                }
                else
                {
                    owner = Singleton<SpecialCardListModel>.Instance;
                    fields = FloorFields;
                }
                if (owner == null) return true;
                if (Apply(owner, fields, unit, p[4], new[] { p[5], p[6], p[7], p[8] })) _applied++;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][ego] apply failed: " + e.Message);
            }
            return true;
        }

        private static bool Apply(object owner, FieldInfo[] fields, BattleUnitModel unit,
                                  string definitions, string[] zoneText)
        {
            string[] defs = definitions == "-" ? new string[0] : definitions.Split('~');
            int[][] indices = new int[fields.Length][];
            for (int z = 0; z < fields.Length; z++) indices[z] = Parse(zoneText[z], defs.Length);

            List<BattleDiceCardModel> old = new List<BattleDiceCardModel>();
            for (int z = 0; z < fields.Length; z++)
                foreach (BattleDiceCardModel card in List(owner, fields[z])) if (!old.Contains(card)) old.Add(card);
            if (unit != null && unit.cardSlotDetail != null)
                foreach (BattlePlayingCardDataInUnitModel placed in unit.cardSlotDetail.cardAry)
                    if (placed != null && placed.card != null && placed.card.XmlData.IsPersonal() && !old.Contains(placed.card)) old.Add(placed.card);

            BattleDiceCardModel[] cards = new BattleDiceCardModel[defs.Length];
            HashSet<BattleDiceCardModel> taken = new HashSet<BattleDiceCardModel>();
            float[] current = new float[defs.Length], max = new float[defs.Length];
            int[] cost = new int[defs.Length], add = new int[defs.Length], zero = new int[defs.Length];
            int[] temporary = new int[defs.Length], exhaust = new int[defs.Length], spent = new int[defs.Length];
            for (int i = 0; i < defs.Length; i++)
            {
                string[] f = defs[i].Split(',');
                if (f.Length != 9 || !float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out current[i]) ||
                    !float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out max[i]) ||
                    !int.TryParse(f[3], out cost[i]) || !int.TryParse(f[4], out add[i]) || !int.TryParse(f[5], out zero[i]) ||
                    !int.TryParse(f[6], out temporary[i]) || !int.TryParse(f[7], out exhaust[i]) ||
                    !int.TryParse(f[8], out spent[i])) return false;
                BattleDiceCardModel card = old.Find(delegate(BattleDiceCardModel c) { return !taken.Contains(c) && Tag(c) == f[0]; });
                if (card == null)
                {
                    int split = f[0].LastIndexOf(':'); int id;
                    if (split < 0 || !int.TryParse(f[0].Substring(split + 1), out id)) return false;
                    string package = Uri.UnescapeDataString(f[0].Substring(0, split));
                    DiceCardXmlInfo info = ItemXmlDataList.instance.GetCardItem(package == "base" ? new LorId(id) : new LorId(package, id), true);
                    if (info == null) return false;
                    card = BattleDiceCardModel.CreatePlayingCard(info);
                }
                cards[i] = card; taken.Add(card);
            }

            for (int i = 0; i < cards.Length; i++)
            {
                if (unit != null) cards[i].owner = unit;
                cards[i].SetCurrentCost(cost[i]);
                if (CurrentCool != null) CurrentCool.SetValue(cards[i], current[i]);
                if (MaxCool != null) MaxCool.SetValue(cards[i], max[i]);
                if (CostAdder != null) CostAdder.SetValue(cards[i], add[i]);
                if (CostZero != null) CostZero.SetValue(cards[i], zero[i] != 0);
                cards[i].temporary = temporary[i] != 0;
                cards[i].exhaust = exhaust[i] != 0;
                cards[i].costSpended = spent[i] != 0;
            }
            for (int z = 0; z < fields.Length; z++)
            {
                List<BattleDiceCardModel> target = List(owner, fields[z]);
                target.Clear();
                for (int i = 0; i < indices[z].Length; i++) target.Add(cards[indices[z][i]]);
            }
            return true;
        }

        private static int[] Parse(string csv, int count)
        {
            if (csv == "-") return new int[0];
            string[] values = csv.Split(',');
            int[] result = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
                if (!int.TryParse(values[i], out result[i]) || result[i] < 0 || result[i] >= count)
                    throw new FormatException("Invalid EGO instance index");
            return result;
        }

        /// <summary>Canonical EGO containers included in the final round digest.</summary>
        public static string CommitBody()
        {
            try
            {
                List<string> rows = new List<string>();
                SpecialCardListModel floor = Singleton<SpecialCardListModel>.Instance;
                if (floor != null) rows.Add("F:" + ContainerBody(floor, FloorFields));

                List<BattleUnitModel> players = BattleObjectManager.instance.GetAliveList(Faction.Player);
                if (players != null)
                {
                    players.Sort(delegate(BattleUnitModel a, BattleUnitModel b)
                    {
                        return NetState.WireIndex(a).CompareTo(NetState.WireIndex(b));
                    });
                    for (int i = 0; i < players.Count; i++)
                    {
                        BattleUnitModel u = players[i];
                        if (u == null || u.personalEgoDetail == null) continue;
                        rows.Add("P" + NetState.WireIndex(u).ToString(CultureInfo.InvariantCulture) + ":" +
                                 ContainerBody(u.personalEgoDetail, PersonalFields));
                    }
                }
                return rows.Count == 0 ? "-" : string.Join("|", rows.ToArray());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][ego] commit body failed: " + e.Message);
                return "ERROR";
            }
        }

        private static string ContainerBody(object owner, FieldInfo[] fields)
        {
            string[] zones = new string[fields.Length];
            for (int z = 0; z < fields.Length; z++)
            {
                List<BattleDiceCardModel> list = List(owner, fields[z]);
                string[] cards = new string[list.Count];
                for (int i = 0; i < list.Count; i++) cards[i] = CardState(list[i]);
                zones[z] = cards.Length == 0 ? "-" : string.Join(",", cards);
            }
            return string.Join("/", zones);
        }

        private static string CardState(BattleDiceCardModel card)
        {
            if (card == null) return "null";
            float current = CurrentCool == null ? card.CurrentCooltimeValue : (float)CurrentCool.GetValue(card);
            float max = MaxCool == null ? card.MaxCooltimeValue : (float)MaxCool.GetValue(card);
            return Tag(card) + "@" + current.ToString("R", CultureInfo.InvariantCulture) + "@" +
                   max.ToString("R", CultureInfo.InvariantCulture) + "@" +
                   card.CurCost.ToString(CultureInfo.InvariantCulture) + "@" +
                   (card.temporary ? "1" : "0") + (card.exhaust ? "1" : "0") +
                   (card.costSpended ? "1" : "0");
        }
    }
}
