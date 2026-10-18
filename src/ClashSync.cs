using System;
using System.Collections.Generic;
using LOR_DiceSystem;
using UnityEngine;

namespace RuinaNet
{
    // Wire snapshots install cardAry directly, bypassing AddCard's aggro changes.
    // Replay only those changes on the host; never replay card costs or abilities.
    internal static class ClashSync
    {
        internal sealed class Choice
        {
            public BattleDiceCardModel Card;
            public BattleUnitModel Target;
            public int TargetSlot;
        }

        public static List<Choice> Capture(BattleUnitModel unit)
        {
            if (!BattleSync.Active || !BattleSync.IsHost || unit == null ||
                unit.faction != Faction.Player || BattleSync.IsMine(unit)) return null;
            List<Choice> result = new List<Choice>();
            foreach (BattlePlayingCardDataInUnitModel card in unit.cardSlotDetail.cardAry)
                result.Add(card == null ? null : new Choice {
                    Card = card.card, Target = card.target, TargetSlot = card.targetSlotOrder
                });
            return result;
        }

        private static BattlePlayingCardDataInUnitModel At(BattleUnitModel unit, int slot)
        {
            if (unit == null || unit.cardSlotDetail == null || unit.cardSlotDetail.cardAry == null ||
                slot < 0 || slot >= unit.cardSlotDetail.cardAry.Count) return null;
            return unit.cardSlotDetail.cardAry[slot];
        }

        private static bool AreaPage(BattlePlayingCardDataInUnitModel card)
        {
            if (card == null || card.card == null) return true;
            CardRange range = card.card.GetSpec().Ranged;
            return range == CardRange.FarArea || range == CardRange.FarAreaEach;
        }

        public static void After(BattleUnitModel source, List<Choice> before)
        {
            if (before == null || !BattleSync.Active || !BattleSync.IsHost) return;
            try
            {
                HashSet<BattleUnitModel> changed = new HashSet<BattleUnitModel>();
                int count = Math.Max(before.Count, source.cardSlotDetail.cardAry.Count);
                // Release old reciprocal targets first. All replacement slots are
                // already installed, so fallback candidates cannot include removed pages.
                for (int slot = 0; slot < count; slot++)
                {
                    Choice old = slot < before.Count ? before[slot] : null;
                    BattlePlayingCardDataInUnitModel now = At(source, slot);
                    if (old == null || old.Target == null || old.Target.faction != Faction.Enemy ||
                        (now != null && now.target == old.Target && now.targetSlotOrder == old.TargetSlot)) continue;
                    BattleUnitModel enemy = old.Target;
                    BattlePlayingCardDataInUnitModel reply = At(enemy, old.TargetSlot);
                    if (reply == null || reply.target != source || reply.targetSlotOrder != slot ||
                        !enemy.isTauntable) continue;

                    List<BattlePlayingCardDataInUnitModel> candidates = new List<BattlePlayingCardDataInUnitModel>();
                    foreach (BattleUnitModel player in BattleObjectManager.instance.GetAliveList(Faction.Player))
                    {
                        if (player.cardSlotDetail == null) continue;
                        foreach (BattlePlayingCardDataInUnitModel card in player.cardSlotDetail.cardAry)
                            if (card != null && card.target == enemy && card.targetSlotOrder == old.TargetSlot &&
                                card.speedDiceResultValue > reply.speedDiceResultValue) candidates.Add(card);
                    }
                    if (candidates.Count == 0 || AreaPage(reply))
                    {
                        reply.target = reply.earlyTarget;
                        reply.targetSlotOrder = reply.earlyTargetOrder;
                    }
                    else
                    {
                        BattlePlayingCardDataInUnitModel fallback = RandomUtil.SelectOne(candidates);
                        reply.target = fallback.owner;
                        reply.targetSlotOrder = fallback.slotOrder;
                    }
                    changed.Add(enemy);
                }

                // Match AddCard: a newly placed/replaced/retargeted page may redirect
                // an ordinary enemy page, subject to the game's own speed/passive rules.
                for (int slot = 0; slot < count; slot++)
                {
                    Choice old = slot < before.Count ? before[slot] : null;
                    BattlePlayingCardDataInUnitModel now = At(source, slot);
                    if (now == null || now.card == null || now.target == null || now.target.faction != Faction.Enemy) continue;
                    if (old != null && old.Card == now.card && old.Target == now.target && old.TargetSlot == now.targetSlotOrder) continue;
                    BattleUnitModel enemy = now.target;
                    BattlePlayingCardDataInUnitModel reply = At(enemy, now.targetSlotOrder);
                    if (reply == null || AreaPage(reply) || reply.card.GetSpec().affection == CardAffection.TeamNear ||
                        !source.CanChangeAttackTarget(enemy, slot, now.targetSlotOrder)) continue;
                    reply.target = source;
                    reply.targetSlotOrder = slot;
                    changed.Add(enemy);
                }
                foreach (BattleUnitModel enemy in changed)
                {
                    BattleSync.PublishUnitCards(enemy, true);
                    BattleSync.Diag("clash", "peer=" + NetState.WireIndex(source) +
                                    " enemy=" + NetState.WireIndex(enemy) +
                                    " cards=" + NetState.CardsLine(enemy));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][clash] target reconciliation failed: " + e.Message);
            }
        }

        public static void PublishEnemyTargets()
        {
            if (!BattleSync.Active || !BattleSync.IsHost) return;
            foreach (BattleUnitModel enemy in BattleObjectManager.instance.GetAliveList(Faction.Enemy))
                BattleSync.PublishUnitCards(enemy, false);
        }
    }
}
