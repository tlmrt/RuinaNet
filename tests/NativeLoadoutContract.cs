using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using RuinaNet;

// Uses the installed game's real BookModel, PassiveModel, normalization,
// CreatePassiveList and UnitBattleDataModel.Refreshhp implementations.
// XML fixtures have no skins/cards, avoiding Unity rendering/native services.
class NativeLoadoutContract
{
    static string managed;
    static int checks;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
    static void Main(string[] args)
    {
        managed = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request) {
            string path = System.IO.Path.Combine(managed, new AssemblyName(request.Name).Name + ".dll");
            return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        PassiveXmlInfo innate = Passive(881001, "ContractNoop");
        PassiveXmlInfo replacement = Passive(881002, "ContractStartHp");
        PassiveXmlInfo extra = Passive(881003, "ContractNoop");
        PassiveXmlInfo empty = Passive(9999999, "ContractNoop");
        Singleton<PassiveXmlList>.Instance.Init(new List<PassiveXmlInfo> { innate, replacement, extra, empty });
        BookXmlInfo oldXml = Core(882001, 106, new List<LorId>());
        BookXmlInfo newXml = Core(882002, 79, new List<LorId> { innate.id });
        Singleton<BookXmlList>.Instance.Init(new List<BookXmlInfo> { oldXml, newXml });
        typeof(AssemblyManager).GetMethod("LoadTypesFromAssembly", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Invoke(Singleton<AssemblyManager>.Instance, new object[] { Assembly.GetExecutingAssembly() });
        UnitDataModel original = (UnitDataModel)FormatterServices.GetUninitializedObject(typeof(UnitDataModel));
        original.giftInventory = new GiftInventory(original);
        typeof(UnitDataModel).GetField("_bookItem", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(original, new BookModel(oldXml));

        UnitDataModel flat = CorePageReplica.Build(original, "base:882002", new List<string>(),
            new List<string> { "base:881002>base:881002", "base:881003>base:881003" }, "-", "", "", "");
        flat.bookItem.TryGainUniquePassive();
        Check(!Contains(flat.bookItem, 881002), "real game prunes attributed passive when its origin slot is lost");
        Check(Contains(flat.bookItem, 881001), "real game reintroduces innate passive with flattened metadata");

        UnitDataModel copy = CorePageReplica.Build(original, "base:882002", new List<string>(),
            new List<string> { "base:881001>base:881002", "base:9999999>base:881003" }, "-", "", "", "");
        copy.bookItem.TryGainUniquePassive();
        Check(Contains(copy.bookItem, 881002), "native normalization retains replacement in its original innate slot");
        Check(Contains(copy.bookItem, 881003), "native normalization retains attribution into an empty slot");
        Check(!Contains(copy.bookItem, 881001), "native normalization does not restore removed innate passive");
        List<PassiveAbilityBase> scripts = copy.bookItem.CreatePassiveList();
        Check(scripts.Exists(delegate(PassiveAbilityBase passive) { return passive is PassiveAbility_ContractStartHp; }), "real game constructs attributed passive script");
        Check(scripts.Count == 2, "real script list contains exact effective set");
        Check(CorePageReplica.ReadPassives(copy.bookItem).Contains("base:881001>base:881002"), "native normalized slot round-trips through snapshot");
        UnitBattleDataModel record = (UnitBattleDataModel)FormatterServices.GetUninitializedObject(typeof(UnitBattleDataModel));
        record.unitData = copy; record.hp = 106;
        record.Refreshhp();
        Check(record.hp == 72, "native Refreshhp replaces cached 106 with new core 79 and attributed start-HP adjustment -7");
        record.Refreshhp();
        Check(record.hp == 72, "native Refreshhp does not accumulate start-HP adjustment");
        Check(original.bookItem.HP == 106 && original.bookItem != copy.bookItem, "native replica leaves original core stats untouched");
        copy.bookItem.CreatePassiveList();
        Check(Contains(copy.bookItem, 881002) && Contains(copy.bookItem, 881003), "second native battle initialization preserves attribution");
        Console.WriteLine("PASS: " + checks + " native loadout contracts using installed game assembly.");
    }

    static bool Contains(BookModel book, int id)
    {
        return book.GetPassiveInfoList(true).Exists(delegate(BookPassiveInfo info) { return info.passive.id == new LorId(id); });
    }
    static PassiveXmlInfo Passive(int id, string script)
    {
        return new PassiveXmlInfo { _id = id, workshopID = "", script = script, name = "contract", desc = "contract", CanReceivePassive = true, CanGivePassive = true };
    }
    static BookXmlInfo Core(int id, int hp, List<LorId> passives)
    {
        return new BookXmlInfo { _id = id, workshopID = "", CharacterSkin = new List<string>(), SuccessionPossibleNumber = 4,
            EquipEffect = new BookEquipEffect { Hp = hp, Break = 40, Speed = 5, SpeedMin = 2, SpeedDiceNum = 1,
                PassiveList = passives, OnlyCard = new List<int>() } };
    }
}

public class PassiveAbility_ContractNoop : PassiveAbilityBase { }
public class PassiveAbility_ContractStartHp : PassiveAbilityBase
{
    public override float GetStartHp(float currentHp) { return currentHp - 7; }
}
