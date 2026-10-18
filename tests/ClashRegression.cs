using System;
using System.Collections.Generic;
using RuinaNet;
using LOR_DiceSystem;

// Run the production ClashSync against a small game boundary. These doubles
// represent card slots and rule queries; target reconciliation is not duplicated.
class ClashRegression
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static BattleUnitModel host, peer, enemy, other;
    static BattlePlayingCardDataInUnitModel reply;
    static BattleUnitModel Unit(int id, Faction side, bool mine)
    {
        BattleUnitModel unit = new BattleUnitModel { index = id, faction = side, Mine = mine };
        unit.cardSlotDetail.cardAry.Add(null); unit.cardSlotDetail.cardAry.Add(null);
        BattleObjectManager.instance.Units.Add(unit);
        return unit;
    }
    static BattlePlayingCardDataInUnitModel Page(BattleUnitModel source, int slot,
                                                BattleUnitModel target, int targetSlot, int speed)
    {
        BattlePlayingCardDataInUnitModel card = new BattlePlayingCardDataInUnitModel {
            owner = source, slotOrder = slot, target = target, targetSlotOrder = targetSlot,
            earlyTarget = target, earlyTargetOrder = targetSlot, speedDiceResultValue = speed,
            card = new BattleDiceCardModel()
        };
        source.cardSlotDetail.cardAry[slot] = card;
        return card;
    }
    static void Reset()
    {
        BattleSync.Active = BattleSync.IsHost = true;
        BattleSync.Published.Clear(); UnityEngine.Debug.Warnings.Clear();
        BattleObjectManager.instance.Units.Clear();
        host = Unit(0, Faction.Player, true); peer = Unit(1, Faction.Player, false);
        enemy = Unit(0, Faction.Enemy, true); other = Unit(1, Faction.Enemy, true);
        reply = Page(enemy, 0, host, 1, 3);
        Page(other, 0, host, 0, 2);
    }
    static void PlacePeer(int speed)
    {
        List<ClashSync.Choice> before = ClashSync.Capture(peer);
        Page(peer, 0, enemy, 0, speed);
        ClashSync.After(peer, before);
    }
    static void Main()
    {
        Reset();
        Check(ClashSync.Capture(host) == null, "host choices keep vanilla redirect handling");
        Check(ClashSync.Capture(enemy) == null, "enemy snapshots cannot redirect themselves");
        BattleSync.IsHost = false;
        Check(ClashSync.Capture(peer) == null, "client does not arbitrate remote targets");
        BattleSync.IsHost = true; BattleSync.Active = false;
        Check(ClashSync.Capture(peer) == null, "single player is untouched");

        Reset(); PlacePeer(6);
        Check(reply.target == peer && reply.targetSlotOrder == 0, "peer fast page redirects host enemy to peer slot");
        Check(reply.earlyTarget == host && reply.earlyTargetOrder == 1, "original AI target survives redirect");
        Check(BattleSync.Published.Count == 1 && BattleSync.Published[0] == enemy, "host publishes changed enemy only");
        Check(other.cardSlotDetail.cardAry[0].target == host, "unrelated enemy target stays intact");
        List<ClashSync.Choice> before = ClashSync.Capture(peer);
        ClashSync.After(peer, before);
        Check(BattleSync.Published.Count == 1, "unchanged periodic snapshots do not reclaim a clash");
        // The incoming hand mutates the same playing-card object in place.
        before = ClashSync.Capture(peer);
        peer.cardSlotDetail.cardAry[0].target = other;
        ClashSync.After(peer, before);
        Check(reply.target == host && reply.targetSlotOrder == 1, "moving page releases old enemy to original slot");
        Check(other.cardSlotDetail.cardAry[0].target == peer, "moving page acquires new enemy clash");
        Check(before[0].Target == enemy, "captured choices survive in-place hand mutation");
        before = ClashSync.Capture(peer);
        peer.cardSlotDetail.cardAry[0] = null;
        ClashSync.After(peer, before);
        Check(other.cardSlotDetail.cardAry[0].target == host, "cancel returns new enemy to AI target");

        Reset(); PlacePeer(6);
        Page(host, 0, enemy, 0, 5);
        before = ClashSync.Capture(peer); peer.cardSlotDetail.cardAry[0] = null;
        ClashSync.After(peer, before);
        Check(reply.target == host && reply.targetSlotOrder == 0, "cancel falls back to another faster assigned player page");
        Check(reply.earlyTargetOrder == 1, "fallback does not overwrite AI baseline");

        Reset(); PlacePeer(6);
        Page(host, 0, enemy, 0, 3);
        before = ClashSync.Capture(peer); peer.cardSlotDetail.cardAry[0] = null;
        ClashSync.After(peer, before);
        Check(reply.targetSlotOrder == 1, "equal-speed fallback does not steal the AI target");
        Reset(); PlacePeer(6);
        Page(peer, 1, enemy, 0, 7);
        before = ClashSync.Capture(peer); peer.cardSlotDetail.cardAry[0] = null;
        ClashSync.After(peer, before);
        Check(reply.target == peer && reply.targetSlotOrder == 1, "another surviving peer slot can retain the clash");
        before = ClashSync.Capture(peer); peer.cardSlotDetail.cardAry[1] = null;
        ClashSync.After(peer, before);
        Check(reply.target == host && reply.targetSlotOrder == 1, "cancel-all releases final surviving clash");

        Reset(); PlacePeer(6);
        before = ClashSync.Capture(peer); peer.cardSlotDetail.cardAry[0] = null;
        Page(peer, 1, other, 0, 7);
        ClashSync.After(peer, before);
        Check(reply.target == host, "whole-row edit releases removed slot");
        Check(other.cardSlotDetail.cardAry[0].targetSlotOrder == 1, "whole-row edit redirects to added slot");

        Reset(); PlacePeer(3);
        Check(reply.target == host, "equal speed cannot redirect a different librarian's target");
        Reset(); PlacePeer(2);
        Check(reply.target == host, "slower page is one-sided");
        Reset(); peer.ForceAggro = true; PlacePeer(1);
        Check(reply.target == peer, "game forced-aggro rule permits a slower redirect");
        Reset(); enemy.isTauntable = false; PlacePeer(8);
        Check(reply.target == host, "game unredirectable rule is respected");
        Reset(); peer.Direct = true; PlacePeer(8);
        Check(reply.target == host, "game direct-attack rule is respected");
        Reset(); reply.card.Spec.Ranged = CardRange.FarArea; PlacePeer(8);
        Check(reply.target == host, "mass summation cannot be redirected");
        Reset(); reply.card.Spec.Ranged = CardRange.FarAreaEach; PlacePeer(8);
        Check(reply.target == host, "mass individual cannot be redirected");
        Reset(); reply.card.Spec.affection = CardAffection.TeamNear; PlacePeer(8);
        Check(reply.target == host, "team-near target is excluded like vanilla AddCard");

        Reset(); PlacePeer(6);
        // A host action now takes this same clash. An unchanged peer resend must
        // not silently steal it back, nor may cancelling the peer page release it.
        Page(host, 0, enemy, 0, 8); reply.target = host; reply.targetSlotOrder = 0;
        before = ClashSync.Capture(peer); ClashSync.After(peer, before);
        Check(reply.target == host && reply.targetSlotOrder == 0, "duplicate peer snapshot preserves newer host clash");
        before = ClashSync.Capture(peer); peer.cardSlotDetail.cardAry[0] = null;
        ClashSync.After(peer, before);
        Check(reply.target == host && reply.targetSlotOrder == 0, "cancel cannot release another player's current clash");

        Reset(); PlacePeer(6);
        before = ClashSync.Capture(peer); Page(peer, 0, enemy, 0, 7);
        ClashSync.After(peer, before);
        Check(reply.target == peer && reply.earlyTarget == host, "same-target replacement preserves baseline");
        Reset(); enemy.cardSlotDetail.cardAry[0] = null; PlacePeer(8);
        Check(BattleSync.Published.Count == 0, "empty enemy slot cannot become a clash");
        Check(UnityEngine.Debug.Warnings.Count == 0, "no reconciliation exception");
        Console.WriteLine("PASS: " + checks + " clash assertions against production ClashSync.");
    }
}

enum Faction { Enemy, Player }
namespace LOR_DiceSystem
{
    enum CardRange { Near, Far, FarArea, FarAreaEach, Special, Instance }
    enum CardAffection { One, Team, All, Passive, TeamNear }
    class DiceCardSpec { public CardRange Ranged; public CardAffection affection; }
}
class BattleDiceCardModel { public DiceCardSpec Spec = new DiceCardSpec(); public DiceCardSpec GetSpec() { return Spec; } }
class BattlePlayingCardDataInUnitModel
{
    public BattleDiceCardModel card;
    public BattleUnitModel owner, target, earlyTarget;
    public int slotOrder, targetSlotOrder, earlyTargetOrder, speedDiceResultValue;
}
class BattlePlayingCardSlotDetail { public List<BattlePlayingCardDataInUnitModel> cardAry = new List<BattlePlayingCardDataInUnitModel>(); }
class BattleUnitModel
{
    public int index;
    public Faction faction;
    public bool Mine, ForceAggro, Direct;
    public bool isTauntable = true;
    public BattlePlayingCardSlotDetail cardSlotDetail = new BattlePlayingCardSlotDetail();
    public bool CanChangeAttackTarget(BattleUnitModel enemy, int slot, int enemySlot)
    {
        return enemy.isTauntable && !Direct && (ForceAggro ||
            cardSlotDetail.cardAry[slot].speedDiceResultValue > enemy.cardSlotDetail.cardAry[enemySlot].speedDiceResultValue);
    }
}
class BattleObjectManager
{
    public static BattleObjectManager instance = new BattleObjectManager();
    public List<BattleUnitModel> Units = new List<BattleUnitModel>();
    public List<BattleUnitModel> GetAliveList(Faction side) { return Units.FindAll(delegate(BattleUnitModel u) { return u.faction == side; }); }
}
static class RandomUtil { public static T SelectOne<T>(List<T> values) { return values[0]; } }
namespace UnityEngine
{
    static class Debug
    {
        public static List<string> Warnings = new List<string>();
        public static void Log(string text) { }
        public static void LogWarning(string text) { Warnings.Add(text); throw new Exception(text); }
    }
}
namespace RuinaNet
{
    static class BattleSync
    {
        public static bool Active, IsHost;
        public static List<BattleUnitModel> Published = new List<BattleUnitModel>();
        public static bool IsMine(BattleUnitModel unit) { return unit.Mine; }
        public static void PublishUnitCards(BattleUnitModel unit, bool force) { Published.Add(unit); }
        public static void Diag(string tag, string body) { }
    }
    static class NetState
    {
        public static int WireIndex(BattleUnitModel unit) { return unit.index + (unit.faction == Faction.Enemy ? 100 : 0); }
        public static string CardsLine(BattleUnitModel unit) { return "test"; }
    }
}
