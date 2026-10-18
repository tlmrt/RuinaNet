using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LOR_DiceSystem;
using UnityEngine;

namespace RuinaNet
{
    // Bind complete reception loadouts before SetUnitData initializes equipment,
    // personal EGO, passives, stats and character skins.
    internal static class Loadout
    {
        private sealed class Entry
        {
            public int Floor, Key, CurrentDeck;
            public string Book, Custom, Skin, OriginalSkin, WorkshopSkin;
            public List<string> Cards, Passives;
            public List<List<string>> ExtraDecks;
            public string Signature;
        }
        private sealed class Runtime
        {
            public UnitDataModel Original, Copy;
            public string Signature;
        }
        private static readonly Dictionary<string, Entry> Peer = new Dictionary<string, Entry>();
        private static readonly Dictionary<int, string> Mine = new Dictionary<int, string>();
        private static readonly Dictionary<UnitBattleDataModel, Runtime> Replicas = new Dictionary<UnitBattleDataModel, Runtime>();
        private static readonly HashSet<string> Noted = new HashSet<string>();
        private static readonly List<BattleUnitModel> PendingUiRefresh = new List<BattleUnitModel>();
        private static float _sinceSend = 1.5f;

        public static bool Active { get { return NetLink.Running && NetLink.Linked && Room.CoopStage &&
            (!CreatureSync.Active || CreatureSync.CanExchange); } }
        public static int MyUnits { get { return Mine.Count; } }
        public static int PeerUnits { get { return Peer.Count; } }
        private static string Key(int floor, int unit) { return floor + ":" + unit; }

        public static void Reset()
        {
            foreach (KeyValuePair<UnitBattleDataModel, Runtime> pair in Replicas)
                if (object.ReferenceEquals(pair.Key.unitData, pair.Value.Copy)) pair.Key.unitData = pair.Value.Original;
            Replicas.Clear(); Peer.Clear(); Mine.Clear(); Noted.Clear(); PendingUiRefresh.Clear();
            _sinceSend = 1.5f;
        }

        public static void Tick(float dt)
        {
            if (!Active) return;
            _sinceSend += dt;
            if (_sinceSend < 1.5f) return;
            _sinceSend = 0f;
            SendMine();
        }

        private static List<UnitBattleDataModel> FloorUnits()
        {
            StageController sc = Singleton<StageController>.Instance;
            StageLibraryFloorModel floor = sc == null ? null : sc.GetCurrentStageFloorModel();
            return floor == null ? new List<UnitBattleDataModel>() : floor.GetUnitBattleDataList();
        }
        private static int UnitKey(UnitBattleDataModel data, int slot)
        {
            if (CreatureSync.Active) return 100000 + slot;
            return slot == 0 || data.unitData.nameID <= 0 ? 100000 + slot : data.unitData.nameID;
        }
        private static bool IsMine(UnitBattleDataModel data, int slot)
        {
            if (CreatureSync.Active) return CreatureSync.OwnSlot(slot);
            if (slot == 0 || data.unitData.isSephirah) return BattleSync.IsHost;
            bool positional = slot >= Room.MyStart && slot < Room.MyStart + Room.MyCount;
            return Lineup.OwnsNameId(UnitKey(data, slot), positional);
        }

        public static void SendMine()
        {
            if (!Active) return;
            try
            {
                int floor = FloorSync.Local();
                if (floor < 0) return;
                List<UnitBattleDataModel> units = FloorUnits();
                // Old battle objects may remain after a reception; read the
                // current floor's preparation roster rather than those objects.
                for (int slot = 0; slot < units.Count; slot++)
                {
                    UnitBattleDataModel data = units[slot];
                    if (data == null || data.unitData == null || !IsMine(data, slot)) continue;
                    Runtime old;
                    if (Replicas.TryGetValue(data, out old))
                    {
                        data.unitData = old.Original;
                        Replicas.Remove(data);
                    }
                    UnitDataModel unit = data.unitData;
                    int id = UnitKey(data, slot);
                    List<string> cards = new List<string>();
                    List<DiceCardXmlInfo> deck = unit.GetDeckForBattle();
                    if (deck == null) throw new InvalidOperationException("Unreadable deck for unit " + id);
                    foreach (DiceCardXmlInfo card in deck) cards.Add(Tag(card.id));
                    List<string> deckRows = new List<string> { Join(cards) };
                    List<DeckModel> allDecks = unit.bookItem.GetDeckAll_nocopy();
                    for (int d = 1; d < allDecks.Count; d++)
                    {
                        List<string> extra = new List<string>();
                        foreach (DiceCardXmlInfo card in allDecks[d].GetCardList_nocopy()) extra.Add(Tag(card.id));
                        deckRows.Add(Join(extra));
                    }
                    unit.bookItem.TryGainUniquePassive();
                    List<string> passives = CorePageReplica.ReadPassives(unit.bookItem);
                    BookModel appearance = unit.CustomBookItem;
                    string body = "LOADOUT2 " + floor + " " + id + " " + Tag(unit.bookItem.BookId) +
                        " " + string.Join("|", deckRows.ToArray()) + " " + Join(passives) + " " + TagOf(unit.GetCustomBookItemData()) +
                        " " + Encode(CorePageReplica.ReadSkin(appearance, false)) +
                        " " + Encode(CorePageReplica.ReadSkin(appearance, true)) + " " + Encode(unit.workshopSkin) +
                        " " + unit.bookItem.GetCurrentDeckIndex();
                    if (CreatureSync.Active) CreatureSync.PublishLoadout(body);
                    else NetLink.Send(body);
                    string previous;
                    bool changed = !Mine.TryGetValue(id, out previous) || previous != body;
                    if (changed && !Room.GoSeen)
                    {
                        // The roster's hp is cached separately from its core.
                        // Recompute the owner's record too, before OnCreated
                        // copies that cache into the live battle unit.
                        if (!data.enterTheBattle && !data.isDead) data.Refreshhp();
                        LoadoutUi.RefreshPreparation(data, unit);
                    }
                    if (changed)
                        Debug.Log("[RuinaNet][loadout] published floor=" + floor + " unit=" + id +
                                  " core=" + Tag(unit.bookItem.BookId) + " cards=" + cards.Count + " passives=" + passives.Count);
                    Mine[id] = body;
                    if (changed) CreatureSync.SnapshotChanged();
                }
            }
            catch (Exception e) { Note("send:" + e.Message, "publish failed: " + e.Message); }
        }

        public static bool HandleMessage(string[] p)
        {
            if (p == null || p.Length == 0 || p[0] != "LOADOUT2") return false;
            if (!Active || p.Length != 11) return true;
            try
            {
                Entry entry = new Entry();
                if (!int.TryParse(p[1], out entry.Floor) || !int.TryParse(p[2], out entry.Key)) return true;
                if (!int.TryParse(p[10], out entry.CurrentDeck)) return true;
                string[] decks = p[4].Split('|');
                entry.Book = p[3]; entry.Cards = Split(decks[0]); entry.Passives = Split(p[5]); entry.Custom = p[6];
                entry.ExtraDecks = new List<List<string>>();
                for (int d = 1; d < decks.Length; d++) entry.ExtraDecks.Add(Split(decks[d]));
                entry.Skin = Decode(p[7]); entry.OriginalSkin = Decode(p[8]); entry.WorkshopSkin = Decode(p[9]);
                entry.Signature = string.Join(" ", p);
                Entry previous;
                bool changed = !Peer.TryGetValue(Key(entry.Floor, entry.Key), out previous) || previous.Signature != entry.Signature;
                Peer[Key(entry.Floor, entry.Key)] = entry;
                if (!Room.GoSeen && entry.Floor == FloorSync.Local()) ApplyFloor();
                if (changed) CreatureSync.SnapshotChanged();
            }
            catch (Exception e) { Note("receive:" + e.Message, "snapshot rejected: " + e.Message); }
            return true;
        }

        private static bool Mirror(UnitBattleDataModel data, int slot)
        {
            if (data == null || data.unitData == null || IsMine(data, slot)) return true;
            Entry entry;
            int id = UnitKey(data, slot), floor = FloorSync.Local();
            if (!Peer.TryGetValue(Key(floor, id), out entry)) return false;
            Runtime current;
            if (Replicas.TryGetValue(data, out current) && current.Signature == entry.Signature &&
                object.ReferenceEquals(data.unitData, current.Copy)) return true;
            try
            {
                UnitDataModel original = current == null ? data.unitData : current.Original;
                UnitDataModel copy = CorePageReplica.Build(original, entry.Book, entry.Cards, entry.Passives,
                    entry.Custom, entry.Skin, entry.OriginalSkin, entry.WorkshopSkin, entry.ExtraDecks, entry.CurrentDeck);
                // Preserve the stage record itself so deaths, rewards, damage
                // and floor progression still update the same StageModel object.
                UnitDataModel previous = data.unitData;
                data.unitData = copy;
                if (!data.enterTheBattle && !data.isDead) data.Refreshhp();
                else data.hp = Math.Min(data.hp, copy.MaxHp);
                Replicas[data] = new Runtime { Original = original, Copy = copy, Signature = entry.Signature };
                if (!Room.GoSeen) LoadoutUi.RefreshPreparation(data, previous);
                BattleSync.Diag("loadout", "floor=" + floor + " unit=" + id + " core=" + entry.Book +
                    " cards=" + entry.Cards.Count + " passives=" + entry.Passives.Count + " bound before unit creation");
                return true;
            }
            catch (Exception e)
            {
                Note("mirror:" + floor + ":" + id + ":" + e.Message, "cannot bind unit " + id + ": " + e.Message);
                return false;
            }
        }

        private static void ApplyFloor()
        {
            List<UnitBattleDataModel> units = FloorUnits();
            for (int slot = 0; slot < units.Count; slot++) Mirror(units[slot], slot);
        }

        public static bool ReadyForBattle()
        {
            if (!Active) return true;
            List<UnitBattleDataModel> units = FloorUnits();
            for (int slot = 0; slot < units.Count; slot++)
            {
                UnitBattleDataModel data = units[slot];
                if (data == null || !data.IsAddedBattle || data.isDead) continue;
                if (!Mirror(data, slot))
                {
                    Room.Notice("正在等待对方的核心书页与卡组同步完成；请确认双方安装了相同的书页模组");
                    return false;
                }
            }
            return true;
        }

        public static string CreatureDigest()
        {
            if (!Active || !CreatureSync.CanExchange) return null;
            List<UnitBattleDataModel> units = FloorUnits();
            List<string> rows = new List<string>();
            for (int slot = 0; slot < units.Count; slot++)
            {
                if ((CreatureSync.Eligible & (1 << slot)) == 0) continue;
                UnitBattleDataModel data = units[slot];
                if (data == null || !data.IsAddedBattle || data.isDead) return null;
                string body; Entry peer;
                int id = UnitKey(data, slot);
                if (IsMine(data, slot)) { if (!Mine.TryGetValue(id, out body)) return null; }
                else { if (!Peer.TryGetValue(Key(CreatureSync.Floor, id), out peer)) return null; body = peer.Signature; }
                rows.Add(body);
            }
            if (rows.Count != CreatureRoster.Count(CreatureSync.Eligible)) return null;
            // Canonical owner-authored records, in roster order; local display
            // names and inventory instance IDs deliberately do not participate.
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", rows.ToArray()))))
                    .Replace("-", "").ToLowerInvariant();
        }

        public static void PrepareUnit(BattleUnitModel unit, UnitBattleDataModel data)
        {
            if (!Active || unit == null || unit.faction != Faction.Player || data == null) return;
            int slot = FloorUnits().IndexOf(data);
            if (slot >= 0 && !Mirror(data, slot))
                Note("late:" + slot, "unit " + slot + " reached creation without a complete loadout");
        }

        public static void ApplyDeck(BattleUnitModel unit)
        {
            if (!Active || unit == null || unit.faction != Faction.Player || unit.UnitData == null) return;
            // The original SetUnitData already initialized every system from
            // the replica. Do not reinitialize its deck or append old passives.
            SanitizePassives(unit);
            if (Replicas.ContainsKey(unit.UnitData)) PendingUiRefresh.Add(unit);
            List<string> actual = new List<string>();
            if (unit.passiveDetail != null && unit.passiveDetail.PassiveList != null)
                foreach (PassiveAbilityBase passive in unit.passiveDetail.PassiveList)
                    if (passive != null && passive.id != null) actual.Add(Tag(passive.id));
            BattleSync.Diag("loadout-unit", "unit=" + unit.index + " startHp=" + unit.UnitData.hp +
                           " maxHp=" + unit.MaxHp + " nativePassives=" + Join(actual));
        }
        public static void RefreshPreparation()
        {
            if (!Active || Room.GoSeen) return;
            foreach (UnitBattleDataModel data in FloorUnits())
                if (data != null && data.unitData != null) LoadoutUi.RefreshPreparation(data, data.unitData);
        }
        public static void FlushUiRefresh()
        {
            foreach (BattleUnitModel unit in PendingUiRefresh) BattleSync.RefreshUnitUi(unit);
            PendingUiRefresh.Clear();
        }
        public static int SanitizePassives(BattleUnitModel unit)
        {
            if (unit == null || unit.passiveDetail == null || unit.passiveDetail.PassiveList == null) return 0;
            return unit.passiveDetail.PassiveList.RemoveAll(delegate(PassiveAbilityBase passive) { return passive == null; });
        }

        private static void Note(string key, string message)
        {
            if (Noted.Add(key)) Debug.LogWarning("[RuinaNet][loadout] " + message);
        }
        private static string TagOf(BookModel book) { return book == null ? "-" : Tag(book.BookId); }
        private static string Tag(LorId id)
        {
            if (id == null || id == LorId.None) return "-";
            return Uri.EscapeDataString(string.IsNullOrEmpty(id.packageId) ? "base" : id.packageId) + ":" +
                   id.id.ToString(CultureInfo.InvariantCulture);
        }
        private static string Encode(string text) { return string.IsNullOrEmpty(text) ? "-" : Uri.EscapeDataString(text); }
        private static string Decode(string text) { return text == "-" ? "" : Uri.UnescapeDataString(text); }
        private static string Join(List<string> list) { return list.Count == 0 ? "-" : string.Join(",", list.ToArray()); }
        private static List<string> Split(string csv) { return csv == "-" ? new List<string>() : new List<string>(csv.Split(',')); }
    }
}
