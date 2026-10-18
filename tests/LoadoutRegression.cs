using System;
using System.Collections.Generic;
using LOR_DiceSystem;
using RuinaNet;

// Production Loadout and CorePageReplica execute unchanged. Game boundaries
// below model stage records, XML assets, books and the ordered network sender.
class LoadoutRegression
{
    static int checks;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
    static LorId Id(int id) { return new LorId(id); }
    static List<string> Tags(params string[] tags) { return new List<string>(tags); }
    static string ReplaceField(string wire, int field, string value)
    {
        string[] parts = wire.Split(' '); parts[field] = value; return string.Join(" ", parts);
    }
    static UnitDataModel Unit(int core, int name)
    {
        UnitDataModel unit = new UnitDataModel(new BookModel(Singleton<BookXmlList>.Instance.GetData(Id(core), true)), name);
        unit.bookItem.SetOwner(unit); return unit;
    }
    static string Capture(UnitDataModel source, int floor)
    {
        Loadout.Reset(); Room.GoSeen = false; BattleSync.IsHost = true; FloorSync.Floor = floor;
        Singleton<StageController>.Instance.Floor.Units = new List<UnitBattleDataModel> { new UnitBattleDataModel(source) };
        UI.UIController.Prepare(Singleton<StageController>.Instance.Floor.Units[0]);
        NetLink.Sent.Clear(); Loadout.SendMine();
        Check(NetLink.Sent.Count == 1, "source publishes patron on current floor");
        return NetLink.Sent[0];
    }
    static UnitBattleDataModel Client(UnitDataModel original, int floor)
    {
        Loadout.Reset(); BattleSync.IsHost = false; Room.GoSeen = false; FloorSync.Floor = floor;
        UnitBattleDataModel record = new UnitBattleDataModel(original);
        Singleton<StageController>.Instance.Floor.Units = new List<UnitBattleDataModel> { record };
        UI.UIController.Prepare(record);
        return record;
    }
    static void Receive(string wire) { Check(Loadout.HandleMessage(wire.Split(' ')), "loadout message dispatched"); }
    static void Main()
    {
        Singleton<BookXmlList>.Instance.Items[Id(1)] = new BookXmlInfo { id = Id(1), hp = 106 };
        Singleton<BookXmlList>.Instance.Items[Id(2)] = new BookXmlInfo { id = Id(2), hp = 79 };
        Singleton<BookXmlList>.Instance.Items[Id(3)] = new BookXmlInfo { id = Id(3), hp = 42 };
        Singleton<BookXmlList>.Instance.Items[new LorId("mod A", 2)] = new BookXmlInfo { id = new LorId("mod A", 2), hp = 123 };
        for (int id = 10; id < 14; id++) ItemXmlDataList.instance.Items[Id(id)] = new DiceCardXmlInfo { id = Id(id) };
        for (int id = 20; id < 24; id++) Singleton<PassiveXmlList>.Instance.Items[Id(id)] = new PassiveXmlInfo { id = Id(id) };
        Singleton<PassiveXmlList>.Instance.Items[Id(9999999)] = new PassiveXmlInfo { id = Id(9999999) };
        Singleton<BookXmlList>.Instance.Items[Id(1)].Innate.Add(Id(20));
        Singleton<BookXmlList>.Instance.Items[Id(2)].Innate.Add(Id(21));
        Singleton<BookXmlList>.Instance.Items[Id(3)].Innate.Add(Id(22));
        UnitDataModel source = Unit(2, 777); // Positive patron nameID must still use floor-slot key.
        source.bookItem.GetDeckAll_nocopy()[0].GetCardList_nocopy().Add(ItemXmlDataList.instance.GetCardItem(Id(10), true));
        source.bookItem.GetDeckAll_nocopy()[0].GetCardList_nocopy().Add(ItemXmlDataList.instance.GetCardItem(Id(10), true));
        source.bookItem.Passives.Add(new PassiveModel(Id(21), 2, 0));
        source.bookItem.SetSkins("skin with space", "variant A"); source.workshopSkin = "workshop skin";
        string equipped = Capture(source, 1);
        Check(equipped.Contains(" 100000 base:2 "), "patron key does not depend on positive nameID");
        Check(equipped.Split(' ').Length == 11, "appearance fields stay in one encoded wire token");

        UnitDataModel original = Unit(1, 777);
        original.bookItem.Passives.Add(new PassiveModel(Id(20), 1, 0));
        BookModel originalBook = original.bookItem;
        UnitDataModel unrelatedOwner = Unit(2, 888);
        BookModel occupied = unrelatedOwner.bookItem;
        UnitBattleDataModel record = Client(original, 1);
        Check(!Loadout.ReadyForBattle(), "missing peer loadout blocks battle instead of using old core");
        Receive(equipped);
        UnitDataModel replica = record.unitData;
        Check(replica != original, "stage uses a distinct unit-data copy");
        Check(replica.bookItem.BookId.Equals(Id(2)), "peer core class is installed before native initialization");
        Check(record.hp == 79 && replica.MaxHp == 79, "first-battle HP derives from new core");
        Check(original.bookItem == originalBook && original.MaxHp == 106, "library roster equipment is untouched");
        Check(occupied.owner == unrelatedOwner, "another librarian's unique equipped copy is untouched");
        Check(replica.bookItem != occupied && replica.bookItem.owner == replica, "remote book is independent of occupied inventory copies");
        Check(replica.bookItem.GetPassiveInfoList(true).Count == 1 && replica.bookItem.Passives[0].Id.Equals(Id(21)), "effective passives replace stale local passives");
        Check(replica.GetDeckForBattle().Count == 2 && replica.GetDeckForBattle()[0].id.Equals(Id(10)), "duplicate combat pages survive exact deck transfer");
        Check(original.GetDeckForBattle().Count == 0, "remote deck does not alter original library book");
        Check(CorePageReplica.ReadSkin(replica.bookItem, false) == "skin with space", "skin decoded and installed");
        Check(CorePageReplica.ReadSkin(replica.bookItem, true) == "variant A" && replica.workshopSkin == "workshop skin", "variant and workshop appearance transferred");
        Check(Loadout.ReadyForBattle(), "valid complete peer loadout opens battle readiness");
        Receive(equipped);
        Check(record.unitData == replica, "identical snapshots do not rebuild the stage copy");
        record.unitData = original;
        Receive(equipped);
        Check(record.unitData != original && record.unitData.bookItem.BookId.Equals(Id(2)), "same signature repairs replacement of stage data");
        Loadout.Reset();
        Check(record.unitData == original && original.bookItem == originalBook, "closing reception restores original record reference");

        UnitDataModel next = Unit(3, 777);
        next.bookItem.Passives.Add(new PassiveModel(Id(22), 3, 0));
        string changed = Capture(next, 1);
        record = Client(original, 1); Receive(equipped); Receive(changed);
        Check(record.unitData.bookItem.BookId.Equals(Id(3)) && record.hp == 42, "rapid core switch installs final core and stats");
        Check(record.unitData.GetDeckForBattle().Count == 0, "empty new deck clears old pages");
        Check(record.unitData.bookItem.GetPassiveInfoList(true).Count == 1 && record.unitData.bookItem.Passives[0].Id.Equals(Id(22)), "second core does not retain previous core's passive");
        record.enterTheBattle = true; record.hp = 17;
        Receive(equipped);
        Check(record.hp == 17, "rebind does not heal an already participating stage record");
        Room.GoSeen = true; replica = record.unitData; Receive(changed);
        Check(record.unitData == replica, "late equipment message cannot mutate an active battle");
        Loadout.PrepareUnit(new BattleUnitModel { faction = Faction.Player }, record);
        Check(record.unitData.bookItem.BookId.Equals(Id(3)), "next native unit creation binds newest cached loadout");

        record = Client(original, 1);
        string otherFloor = changed.Replace("LOADOUT2 1 ", "LOADOUT2 2 ");
        Receive(otherFloor);
        Check(record.unitData == original && !Loadout.ReadyForBattle(), "other floor's patron snapshot cannot change this floor");
        FloorSync.Floor = 2;
        Check(Loadout.ReadyForBattle() && record.unitData.bookItem.BookId.Equals(Id(3)), "new floor resolves its own cached patron loadout");

        record = Client(original, 1); Receive(equipped); replica = record.unitData;
        Receive(ReplaceField(changed, 3, "base:999"));
        Check(record.unitData == replica && !Loadout.ReadyForBattle(), "missing core asset rejects whole update and blocks start");
        Receive(ReplaceField(changed, 4, "base:999|-|-|-"));
        Check(record.unitData == replica, "missing combat page cannot half-apply a new core");
        Receive(changed);
        Check(Loadout.ReadyForBattle(), "complete retry recovers without restarting the room");

        record = Client(original, 1);
        BattleSync.IsHost = true; Receive(equipped);
        Check(record.unitData == original, "peer cannot overwrite host-owned patron equipment");
        BattleSync.IsHost = false;
        UnitDataModel localAssistant = Unit(1, 999);
        UnitBattleDataModel localRecord = new UnitBattleDataModel(localAssistant);
        Singleton<StageController>.Instance.Floor.Units.Add(localRecord);
        Lineup.Local.Add(999);
        Receive(equipped.Replace(" 100000 ", " 999 "));
        Check(localRecord.unitData == localAssistant, "incoming loadout cannot overwrite local assistant");

        UnitDataModel modReplica = CorePageReplica.Build(original, "mod%20A:2", Tags(), Tags(), "-", "", "", "");
        Check(modReplica.MaxHp == 123 && modReplica.bookItem.BookId.packageId == "mod A", "same numeric core ID respects mod package");
        UnitDataModel cosmetic = CorePageReplica.Build(original, "base:2", Tags(), Tags(), "base:3", "look", "variant", "");
        Check(cosmetic.bookItem.BookId.Equals(Id(2)) && cosmetic.CustomBookItem.BookId.Equals(Id(3)), "core stats and appearance book remain distinct");
        Check(CorePageReplica.ReadSkin(cosmetic.CustomBookItem, false) == "look", "appearance book receives visual selection");
        Check(original.GetCustomBookItemData() == null, "custom appearance does not leak into library original");
        UnitDataModel multi = CorePageReplica.Build(original, "base:2", Tags("base:10"), Tags(), "-", "", "", "",
            new List<List<string>> { Tags("base:11"), Tags("base:12"), Tags("base:13") }, 2);
        Check(multi.bookItem.GetDeckAll_nocopy()[1].Cards[0].id.Equals(Id(11)), "secondary stance deck is preserved");
        Check(multi.bookItem.GetDeckAll_nocopy()[3].Cards[0].id.Equals(Id(13)), "fourth stance deck is preserved");
        Check(multi.bookItem.GetCurrentDeckIndex() == 2, "current deck index is preserved");

        // Regression: flattened current ids used to be removed by the game's
        // origin-slot normalization. Keep replacement and empty-slot attribution.
        source = Unit(2, 777);
        PassiveModel replacement = new PassiveModel(Id(21), 0, 0);
        replacement.originData.currentpassive = Singleton<PassiveXmlList>.Instance.GetData(Id(23));
        source.bookItem.Passives.Add(replacement);
        PassiveModel attributed = new PassiveModel(Id(9999999), 0, 0);
        attributed.originData.currentpassive = Singleton<PassiveXmlList>.Instance.GetData(Id(20));
        source.bookItem.Passives.Add(attributed);
        Singleton<PassiveXmlList>.Instance.GetData(Id(23)).StartHpDelta = -7;
        string attributedWire = Capture(source, 1);
        Check(attributedWire.Contains("base:21>base:23"), "wire preserves innate-slot replacement origin");
        Check(attributedWire.Contains("base:9999999>base:20"), "wire preserves attribution into an empty slot");
        Check(Singleton<StageController>.Instance.Floor.Units[0].hp == 72, "owner stage hp is recomputed through start-HP passives");
        record = Client(original, 1); Receive(attributedWire);
        Check(record.hp == 72 && record.RefreshCount == 1, "replica stage hp runs original refresh lifecycle once");
        List<BookPassiveInfo> actual = record.unitData.bookItem.GetPassiveInfoList(true);
        Check(actual.Count == 2 && actual.Exists(delegate(BookPassiveInfo p) { return p.passive.id == Id(23); }), "replacement passive survives normalization");
        Check(actual.Exists(delegate(BookPassiveInfo p) { return p.passive.id == Id(20); }), "attributed extra passive survives normalization");
        Check(!actual.Exists(delegate(BookPassiveInfo p) { return p.passive.id == Id(21); }), "removed original passive is not reintroduced");
        record.unitData.bookItem.TryGainUniquePassive();
        Check(record.unitData.bookItem.GetPassiveInfoList(true).Count == 2, "second script initialization preserves attributed set");
        Receive(attributedWire);
        Check(record.RefreshCount == 1 && record.hp == 72, "identical snapshot neither reapplies start-HP reduction nor heals");
        Check(UI.UIController.Instance.Panel.currentAvailbleUnitslots[0].ShownUnit == record.unitData, "preparation slot rebinds to mirrored unit data");
        Check(UI.UIController.Instance.Panel.currentAvailbleUnitslots[0].IsToggleSelected, "UI refresh preserves selected librarian checkbox");
        Check(UI.UIController.Instance.Panel.InfoRightPanel.ShownUnit == record.unitData, "selected info panel rebinds to mirrored passive data");
        Check(UI.UIController.Instance.Panel.currentAvailbleUnitslots[0].ShownHp == 72, "preparation hp bar reloads recalculated starting HP");
        UI.UIController.Instance.CurrentUIPhase = UI.UIPhase.Librarian_CardList;
        Loadout.RefreshPreparation();
        Check(UI.UIController.Instance.Panel.currentAvailbleUnitslots[0].IsToggleSelected, "opening configuration does not clear lineup");
        Loadout.Reset();
        Check(record.unitData == original, "attributed replica still restores original stage reference");
        Loadout.Reset();
        // Abnormality snapshots use roster slots, independent of the two saves'
        // assistant names. Both roles must hash the same owner-authored rows.
        CreatureSync.Active = CreatureSync.CanExchange = true;
        CreatureSync.Eligible = 3; CreatureSync.HostMask = 1; CreatureSync.Floor = FloorSync.Floor = 4;
        UnitDataModel guestSource = Unit(2, 9001), hostSource = Unit(3, 9002);
        BattleSync.IsHost = false; NetLink.Sent.Clear();
        Singleton<StageController>.Instance.Floor.Units = new List<UnitBattleDataModel> {
            new UnitBattleDataModel(Unit(1, 100)), new UnitBattleDataModel(guestSource) };
        Loadout.SendMine();
        Check(NetLink.Sent.Count == 1 && NetLink.Sent[0].Contains(" 100001 "), "creature client publishes only assigned roster slot");
        string guestWire = NetLink.Sent[0];
        Check(Loadout.CreatureDigest() == null, "creature digest requires the host snapshot");
        Loadout.Reset(); BattleSync.IsHost = true; NetLink.Sent.Clear();
        Singleton<StageController>.Instance.Floor.Units = new List<UnitBattleDataModel> {
            new UnitBattleDataModel(hostSource), new UnitBattleDataModel(Unit(1, 500)) };
        Loadout.SendMine(); string hostWire = NetLink.Sent[0];
        Check(NetLink.Sent.Count == 1 && hostWire.Contains(" 100000 "), "creature host publishes only its own slot");
        Receive(guestWire);
        Check(Loadout.ReadyForBattle(), "creature roster binds both owners before battle");
        string hostDigest = Loadout.CreatureDigest();
        Check(hostDigest != null && hostDigest.Length == 64, "complete creature roster has a SHA256 digest");
        int changes = CreatureSync.Changes; Receive(guestWire);
        Check(CreatureSync.Changes == changes, "identical creature snapshots do not cancel readiness");
        Loadout.Reset(); BattleSync.IsHost = false; NetLink.Sent.Clear();
        Singleton<StageController>.Instance.Floor.Units = new List<UnitBattleDataModel> {
            new UnitBattleDataModel(Unit(1, 999)), new UnitBattleDataModel(guestSource) };
        Loadout.SendMine(); Receive(hostWire);
        Check(hostDigest == Loadout.CreatureDigest(), "both roles hash identical roster despite different local nameIDs");
        changes = CreatureSync.Changes;
        Receive(ReplaceField(hostWire, 7, "changedSkin"));
        Check(CreatureSync.Changes == changes + 1, "changed peer equipment invalidates readiness");
        Check(hostDigest != Loadout.CreatureDigest(), "appearance changes participate in the final launch digest");
        Singleton<StageController>.Instance.Floor.Units[1].IsAddedBattle = false;
        Check(Loadout.CreatureDigest() == null, "native roster changes cannot pass final equipment check");
        Loadout.Reset(); CreatureSync.Active = CreatureSync.CanExchange = false;
        Console.WriteLine("PASS: " + checks + " loadout assertions against production Loadout + CorePageReplica.");
    }
}

class LorId : IEquatable<LorId>
{
    public int id; public string packageId;
    public static readonly LorId None = new LorId(-1);
    public LorId(int value) : this("", value) { }
    public LorId(string package, int value) { id = value; packageId = package; }
    public bool Equals(LorId other) { return !object.ReferenceEquals(other, null) && id == other.id && packageId == other.packageId; }
    public override bool Equals(object other) { return Equals(other as LorId); }
    public override int GetHashCode() { return id ^ packageId.GetHashCode(); }
    public static bool operator ==(LorId a, LorId b) { return object.ReferenceEquals(a,b) || (!object.ReferenceEquals(a,null) && a.Equals(b)); }
    public static bool operator !=(LorId a, LorId b) { return !(a == b); }
}
class Singleton<T> where T : new() { public static T Instance = new T(); }
namespace LOR_DiceSystem { class DiceCardXmlInfo { public LorId id; } }
class ItemXmlDataList
{
    public static ItemXmlDataList instance = new ItemXmlDataList();
    public Dictionary<LorId,DiceCardXmlInfo> Items = new Dictionary<LorId,DiceCardXmlInfo>();
    public DiceCardXmlInfo GetCardItem(LorId id, bool workshop) { DiceCardXmlInfo xml; return Items.TryGetValue(id, out xml) ? xml : null; }
}
class BookXmlInfo { public LorId id; public int hp; public List<LorId> Innate = new List<LorId>(); }
class BookXmlList
{
    public Dictionary<LorId,BookXmlInfo> Items = new Dictionary<LorId,BookXmlInfo>();
    public BookXmlInfo GetData(LorId id, bool workshop) { BookXmlInfo xml; return Items.TryGetValue(id, out xml) ? xml : null; }
}
class PassiveXmlInfo { public LorId id; public bool isLock = false; public int StartHpDelta; }
class PassiveXmlList
{
    public Dictionary<LorId,PassiveXmlInfo> Items = new Dictionary<LorId,PassiveXmlInfo>();
    public PassiveXmlInfo GetData(LorId id) { PassiveXmlInfo xml; return Items.TryGetValue(id, out xml) ? xml : null; }
}
class PassiveModel
{
    public PassiveXmlInfo originpassive;
    public PassiveSaved originData = new PassiveSaved();
    public LorId Id { get { return originData.currentpassive.id; } }
    public PassiveModel(LorId id, int bookInstanceId, int isEmpty)
    {
        originpassive = Singleton<PassiveXmlList>.Instance.GetData(isEmpty == 0 ? id : new LorId(9999999));
        originData.currentpassive = originpassive;
    }
    public class PassiveSaved { public PassiveXmlInfo currentpassive; }
}
class BookPassiveInfo { public PassiveXmlInfo passive; public bool isActivated = true; }
class DeckModel { public List<DiceCardXmlInfo> Cards = new List<DiceCardXmlInfo>(); public List<DiceCardXmlInfo> GetCardList_nocopy() { return Cards; } }
class BookModel
{
    private string _characterSkin = "", _selectedOriginalSkin = "";
    public BookXmlInfo Xml; public UnitDataModel owner; public int instanceId = -1;
    public LorId BookId { get { return Xml.id; } }
    public List<PassiveModel> Passives = new List<PassiveModel>();
    public List<DeckModel> Decks = new List<DeckModel> { new DeckModel(), new DeckModel(), new DeckModel(), new DeckModel() };
    private int _currentDeck;
    public BookModel(BookXmlInfo xml) { Xml = xml; }
    public List<DeckModel> GetDeckAll_nocopy() { return Decks; }
    public void ChangeDeck(int id) { _currentDeck = id; }
    public int GetCurrentDeckIndex() { return _currentDeck; }
    public List<PassiveModel> GetPassiveModelList() { return Passives; }
    public bool TryGainUniquePassive()
    {
        Passives.RemoveAll(delegate(PassiveModel p) { return p.originpassive == null ||
            (p.originpassive.id.id != 9999999 && !Xml.Innate.Contains(p.originpassive.id)); });
        foreach (LorId id in Xml.Innate)
            if (!Passives.Exists(delegate(PassiveModel p) { return p.originpassive.id == id; }))
                Passives.Insert(0, new PassiveModel(id, instanceId, 0));
        return true;
    }
    public void SetOwner(UnitDataModel unit) { owner = unit; }
    public void SetSkins(string skin, string original) { _characterSkin = skin; _selectedOriginalSkin = original; }
    public List<BookPassiveInfo> GetPassiveInfoList(bool current)
    {
        List<BookPassiveInfo> result = new List<BookPassiveInfo>();
        foreach (PassiveModel model in Passives)
            if (model.Id.id != 9999999) result.Add(new BookPassiveInfo { passive = Singleton<PassiveXmlList>.Instance.GetData(model.Id) });
        return result;
    }
}
class UnitDataModel
{
    private BookModel _bookItem, _CustomBookItem;
    public string workshopSkin = ""; public int nameID; public bool isSephirah = false;
    public UnitDataModel(BookModel book, int name) { _bookItem = book; nameID = name; _CustomBookItem = null; }
    public BookModel bookItem { get { return _bookItem; } }
    public BookModel CustomBookItem { get { return _CustomBookItem ?? _bookItem; } }
    public BookModel GetCustomBookItemData() { return _CustomBookItem; }
    public int StartHp { get { return _bookItem.Xml.hp; } }
    public int MaxHp { get { return StartHp; } }
    public List<DiceCardXmlInfo> GetDeckForBattle() { return _bookItem.Decks[0].Cards; }
}
class UnitBattleDataModel
{
    public UnitDataModel unitData; public bool enterTheBattle, isDead = false, IsAddedBattle = true; public float hp;
    public UnitBattleDataModel(UnitDataModel unit) { unitData = unit; hp = unit.StartHp; }
    public int RefreshCount;
    public void Refreshhp()
    {
        RefreshCount++; hp = unitData.StartHp;
        unitData.bookItem.TryGainUniquePassive();
        foreach (BookPassiveInfo passive in unitData.bookItem.GetPassiveInfoList(true)) hp += passive.passive.StartHpDelta;
    }
}
enum Faction { Enemy, Player }
class PassiveAbilityBase { public LorId id = null; }
class PassiveDetail { public List<PassiveAbilityBase> PassiveList = new List<PassiveAbilityBase>(); }
class BattleUnitModel { public Faction faction; public int index = 0; public int MaxHp { get { return UnitData.unitData.MaxHp; } } public UnitBattleDataModel UnitData = null; public PassiveDetail passiveDetail = new PassiveDetail(); }
class StageLibraryFloorModel { public List<UnitBattleDataModel> Units = new List<UnitBattleDataModel>(); public List<UnitBattleDataModel> GetUnitBattleDataList() { return Units; } }
class StageController { public StageLibraryFloorModel Floor = new StageLibraryFloorModel(); public StageLibraryFloorModel GetCurrentStageFloorModel() { return Floor; } }
namespace UnityEngine
{
    class GameObject { public bool activeInHierarchy = true; }
    static class Debug { public static void Log(string message) { } public static void LogWarning(string message) { } }
}
namespace UI
{
    enum UIPhase { BattleSetting, Librarian_CardList }
    enum UIPanelType { BattleSetting }
    class UIController
    {
        public static UIController Instance = new UIController();
        public UIPhase CurrentUIPhase = UIPhase.BattleSetting;
        public UIBattleSettingPanel Panel;
        public object GetUIPanel(UIPanelType panel) { return Panel; }
        public static void Prepare(UnitBattleDataModel data)
        {
            Instance.CurrentUIPhase = UIPhase.BattleSetting;
            Instance.Panel = new UIBattleSettingPanel();
            Instance.Panel.currentAvailbleUnitslots.Add(new UICharacterSlot { unitBattleData = data, ShownUnit = data.unitData });
            Instance.Panel.InfoRightPanel.SetData(data.unitData);
        }
    }
    class UIBattleSettingPanel
    {
        public List<UICharacterSlot> currentAvailbleUnitslots = new List<UICharacterSlot>();
        public UIBattleSettingLibrarianInfoPanel InfoRightPanel = new UIBattleSettingLibrarianInfoPanel();
    }
    class UIBattleSettingLibrarianInfoPanel
    {
        private UnitDataModel unitdata;
        public UnitDataModel ShownUnit { get { return unitdata; } }
        public UnityEngine.GameObject gameObject = new UnityEngine.GameObject();
        public void SetData(UnitDataModel data) { unitdata = data; }
    }
    class UICharacterSlot
    {
        public UnitBattleDataModel unitBattleData;
        public UnitDataModel ShownUnit;
        public float ShownHp;
        public bool IsToggleSelected = true;
        public UnityEngine.GameObject gameObject = new UnityEngine.GameObject();
        public void SetBattleCharacter(UnitBattleDataModel data)
        {
            unitBattleData = data; ShownUnit = data.unitData; ShownHp = data.hp; IsToggleSelected = false;
        }
        public void SetToggle(bool selected) { IsToggleSelected = selected; }
    }
}
namespace RuinaNet
{
    static class CreatureSync
    {
        public static bool Active, CanExchange;
        public static int Eligible, Floor, HostMask;
        public static int Changes;
        public static bool OwnSlot(int slot) { return ((HostMask & (1 << slot)) != 0) == BattleSync.IsHost && (Eligible & (1 << slot)) != 0; }
        public static void PublishLoadout(string body) { NetLink.Send(body); }
        public static void SnapshotChanged() { if (Active) Changes++; }
    }
    static class NetLink { public static bool Running = true, Linked = true; public static List<string> Sent = new List<string>(); public static void Send(string message) { Sent.Add(message); } }
    static class Room { public static bool CoopStage = true, GoSeen; public static int MyStart = 1, MyCount = 1; public static void Notice(string message) { } }
    static class FloorSync { public static int Floor; public static int Local() { return Floor; } }
    static class BattleSync { public static bool IsHost; public static void Diag(string tag, string body) { } public static void RefreshUnitUi(BattleUnitModel unit) { } }
    static class Lineup { public static HashSet<int> Local = new HashSet<int>(); public static bool OwnsNameId(int id, bool fallback) { return Local.Contains(id) || fallback; } }
}
