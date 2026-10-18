using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RuinaNet
{
    /// <summary>
    /// Harmony patches implementing stage 2 (battle) synchronisation.
    /// Installed from RuinaNetInitializer.OnInitializeMod.  Every patch body
    /// starts by checking BattleSync.Active, so single player is untouched.
    /// </summary>
    internal static class BattleSyncPatches
    {
        private const string HarmonyId = "yg.ruinanet.battle";
        private static Harmony _harmony;

        private static FieldInfo _diceResultField;
        private static FieldInfo _speedDiceViewField;
        private static FieldInfo _speedDiceIndexField;

        private sealed class CardApplyState
        {
            public BattleUnitModel Source;
            public EgoReservationSync.ApplyState Ego;
        }

        public static bool Installed { get { return _harmony != null; } }

        public static bool Install()
        {
            if (_harmony != null) return true;
            try
            {
                Harmony h = new Harmony(HarmonyId);
                int ok = 0;
                int total = 0;

                // 1. the round gate
                total++;
                if (TryPatch(h, typeof(StageController), "CompleteApplyingLibrarianCardPhase",
                        new Type[] { typeof(bool) }, "Gate_Pre", null)) ok++;

                // 2. dice roll: remember the behaviour, then reconcile the value
                total++;
                if (TryPatch(h, typeof(BattleDiceBehavior), "RollDice",
                        new Type[0], "Roll_Pre", "Roll_Post")) ok++;

                // 3. battle start: clear per-battle sync state
                total++;
                if (TryPatch(h, typeof(StageController), "StartBattle",
                        new Type[0], null, "BattleStart_Post")) ok++;

                // 4. control restriction: a player may only play their own cards,
                //    and only onto the units they actually own.
                total++;
                if (TryPatch(h, typeof(BattleUnitCardsInHandUI), "ApplySelectedCard",
                        new Type[] { typeof(BattleUnitModel), typeof(int) }, "CardsApply_Pre", "CardsApply_Post")) ok++;

                // 5. hand panel: clicking a peer-owned librarian must not take over
                //    the hand, otherwise the player selects cards that the patch
                //    above then refuses to play, over and over.
                total++;
                if (TryPatch(h, typeof(BattleUnitCardsInHandUI), "SetCardsObject",
                        new Type[] { typeof(BattleUnitModel), typeof(bool) }, "CardsObject_Pre", null)) ok++;

                // 6. reception sync: the one place the game itself enters a
                //    reception, so telling the peer about it costs nothing.
                total++;
                if (TryPatch(h, typeof(UI.UIController), "PrepareBattle",
                        new Type[] { typeof(StageClassInfo), typeof(List<DropBookXmlInfo>) },
                        "PrepareBattle_Pre", "PrepareBattle_Post")) ok++;

                // 7. the co-op start gate.  Every "开始战斗" button in the game
                //    funnels through this one method, so swallowing the press here
                //    keeps the host in charge and lets it fire GO exactly once.
                total++;
                bool loadPatched = TryPatch(h, typeof(GlobalGameManager), "LoadBattleScene",
                        new Type[0], "LoadBattleScene_Pre", null);
                if (loadPatched) ok++;

                // 8. dice guard.  speedDiceResult has no initialiser; it is filled
                //    only by RollSpeedDice during SortUnitPhase.  A unit that shows
                //    up in the alive list after that point keeps a null list, and
                //    BattleUIInputController.UpdateChracterSlots then throws once a
                //    frame forever -- the client-side "librarians on the field but
                //    no speed dice row" failure.
                total++;
                if (TryPatch(h, typeof(StageController), "SortUnitPhase",
                        new Type[0], null, "SortUnitPhase_Post")) ok++;

                total++;
                if (TryPatch(h, typeof(BattleUIInputController), "UpdateChracterSlots",
                        new Type[] { typeof(bool), typeof(Predicate<BattleUnitModel>), typeof(bool) },
                        "ChracterSlots_Pre", null)) ok++;

                // 9. line-up sync.  Every librarian tick on the 开启舞台 screen
                //    goes through SelectedToggles, so that one method is the whole
                //    surface for "who brings whom": the prefix refuses a pick the
                //    peer already claimed, the postfix re-asserts the union of both
                //    players' picks on this machine's own panel.
                total++;
                if (TryPatch(h, typeof(UI.UIBattleSettingPanel), "SelectedToggles",
                        new Type[] { typeof(UI.UICharacterSlot) },
                        "Toggles_Pre", "Toggles_Post")) ok++;

                // 10. loadout sync.  SetUnitData is the single place where a battle
                //     unit installs its book pages (allyCardDetail.Init), and it runs
                //     once, before anything is drawn.  Installing the peer's deck
                //     here is therefore safe and complete -- no card exists yet.
                total++;
                if (TryPatch(h, typeof(BattleUnitModel), "SetUnitData",
                        new Type[] { typeof(UnitBattleDataModel) },
                        "SetUnitData_Pre", "SetUnitData_Post")) ok++;

                // 11. deterministic dice.  MakeDiceResult is the one place a die
                //     face is drawn, and both machines run the same rolls in the
                //     same order -- so both can compute the same pip locally,
                //     with no round trip in the way.
                total++;
                if (TryPatch(h, typeof(DiceStatCalculator), "MakeDiceResult",
                        new Type[] { typeof(int), typeof(int), typeof(int) },
                        "MakeDiceResult_Pre", null)) ok++;

                // 12. the input layer.  The guards inside StageController.CheckInput can
                //     swallow a "start the round" press before it ever reaches the gate,
                //     which then looks exactly like nobody pressed anything.  Watching the
                //     two input methods lets the press open the round regardless, and it
                //     writes the guard states down on the way.
                total++;
                if (TryPatch(h, typeof(StageController), "CheckInput",
                        new Type[] { typeof(bool) }, null, "CheckInput_Post")) ok++;

                // 13. the space bar on its own: the same key the game reads, caught even
                //     on a frame where CheckInput returned before it got that far.
                total++;
                if (TryPatch(h, typeof(BattleUIInputController), "CheckInputBattleStart",
                        new Type[0], null, "CheckBattleStart_Post")) ok++;

                // 14. the foreign-patch guard.  A workshop mod that died while
                //     loading (two instances share its workshop folder) leaves a
                //     patch behind that throws on every call, and one of those
                //     calls sits inside the round start.  Swallowing it keeps
                //     the vanilla result and lets the round come up.
                total++;
                if (TryPatchFinalizer(h, typeof(BattleUnitModel), "IsTargetable",
                        new Type[] { typeof(BattleUnitModel) }, "IsTargetable_Fin")) ok++;

                // 15. Vanilla's P-key auto planner bypasses the normal hand UI and
                //     can fill every librarian locally in one frame.  Co-op uses
                //     explicit per-player ownership, so this shortcut is disabled.
                total++;
                if (TryPatch(h, typeof(StageController), "SetAutoCardForPlayer",
                        new Type[0], "AutoCard_Pre", null)) ok++;

                // 16. Right-click / "remove all" both funnel through this method.
                //     Refuse peer-owned units and publish an allowed local removal
                //     immediately instead of waiting for the periodic hand tick.
                total++;
                if (TryPatch(h, typeof(StageController), "UnEquipPlayerCard",
                        new Type[] { typeof(BattleUnitModel) }, "UnEquip_Pre", "UnEquip_Post")) ok++;

                // 17. Enemy planning has one authority.  The client still advances
                //     through ApplyEnemyCardPhase, but it may not invent a second
                //     set of enemy pages and targets over the host snapshot.
                total++;
                if (TryPatch(h, typeof(StageController), "ApplyEnemyCardAuto",
                        new Type[] { typeof(BattleUnitModel), typeof(int), typeof(int) },
                        "EnemyAuto_Pre", null)) ok++;

                // 18. A right click on one speed die bypasses
                //     StageController.UnEquipPlayerCard and calls DeselectCard
                //     directly.  Guard the actual UI removal path as well.
                total++;
                if (TryPatch(h, typeof(LOR_BattleUnit_UI.SpeedDiceUI), "DeselectCard",
                        new Type[0], "DeselectCard_Pre", "DeselectCard_Post")) ok++;

                // 19-22. Emotion-level abnormality/EGO choices are a separate
                //       system from the playable EGO hand.  Only the host may
                //       operate this popup; its exact choice is replayed on the
                //       client through EmotionChoiceSync.
                total++;
                if (TryPatch(h, typeof(LevelUpUI), "OnSelectPassive",
                        new Type[] { typeof(EmotionPassiveCardUI) },
                        "EmotionPassive_Pre", "EmotionPassive_Post")) ok++;

                total++;
                if (TryPatch(h, typeof(LevelUpUI), "OnClickTargetUnit",
                        new Type[] { typeof(BattleUnitModel) },
                        "EmotionTarget_Pre", "EmotionTarget_Post")) ok++;

                total++;
                if (TryPatch(h, typeof(LevelUpUI), "OnSelectEgoCard",
                        new Type[] { typeof(BattleDiceCardUI) },
                        "EmotionEgoCard_Pre", "EmotionEgoCard_Post")) ok++;

                total++;
                if (TryPatch(h, typeof(LevelUpUI), "OnSelectEgo",
                        new Type[] { typeof(EmotionEgoCardUI) },
                        "EmotionEgo_Pre", "EmotionEgo_Post")) ok++;

                // 23. A completed reception ends the ownership lifetime of its
                //     selected librarians. Stage ids may repeat (replay and some
                //     chained receptions), so id-change detection alone cannot
                //     distinguish a new run from revisiting the book panel.
                total++;
                if (TryPatch(h, typeof(StageController), "CloseBattleScene",
                        new Type[0], null, "BattleClosed_Post")) ok++;

                total++;
                if (TryPatch(h, typeof(UnitDataModel), "EquipBookForUI",
                        new Type[] { typeof(BookModel), typeof(bool), typeof(bool) },
                        null, "CoreBookChanged_Post")) ok++;

                total++;
                if (TryPatch(h, typeof(BookModel), "ApplyPassiveSuccession", new Type[0],
                        null, "CoreBookChanged_Post")) ok++;

                total++;
                if (TryPatch(h, typeof(UI.UIBattleSettingPanel), "OnOpen", new Type[0],
                        null, "PreparationOpened_Post")) ok++;

                int creatureOk = 0;
                total++;
                if (TryPatch(h, typeof(UI.UIController), "OnClickStartCreatureStage",
                        new Type[] { typeof(SephirahType) }, "CreatureFirst_Pre", null)) { ok++; creatureOk++; }
                total++;
                if (TryPatch(h, typeof(UI.UICreatureRebattleNumberSlot), "<OnPointerClick>b__17_0",
                        new Type[] { typeof(bool) }, "CreatureReplay_Pre", null)) { ok++; creatureOk++; }
                total++;
                if (TryPatch(h, typeof(StageController), "EndBattlePhase_creature",
                        new Type[0], "CreatureEnd_Pre", null)) { ok++; creatureOk++; }
                total++;
                if (TryPatch(h, typeof(UI.UIController), "EnterFinalBattle",
                        new Type[] { typeof(bool), typeof(bool) }, "CreatureFinal_Pre", null)) { ok++; creatureOk++; }
                total++;
                if (TryPatch(h, typeof(StageController), "GameOver", new Type[] { typeof(bool), typeof(bool) },
                        null, "CreatureGameOver_Post")) { ok++; creatureOk++; }
                CreatureSync.HooksReady = loadPatched && creatureOk == 5;

                if (_diceResultField == null)
                {
                    _diceResultField = AccessTools.Field(typeof(BattleDiceBehavior), "_diceResultValue");
                }
                if (_diceResultField == null)
                {
                    Debug.LogWarning("[RuinaNet][battle] field _diceResultValue not found -- dice reconciliation disabled");
                }
                _speedDiceViewField = AccessTools.Field(typeof(LOR_BattleUnit_UI.SpeedDiceUI), "_view");
                if (_speedDiceViewField == null)
                {
                    Debug.LogWarning("[RuinaNet][battle] field SpeedDiceUI._view not found -- right-click ownership guard disabled");
                }
                _speedDiceIndexField = AccessTools.Field(typeof(LOR_BattleUnit_UI.SpeedDiceUI), "_speedDiceIndex");
                if (_speedDiceIndexField == null)
                {
                    Debug.LogWarning("[RuinaNet][battle] field SpeedDiceUI._speedDiceIndex not found -- EGO cancellation lease release disabled");
                }

                _harmony = h;
                Debug.Log("[RuinaNet][battle] patches: " + ok + "/" + total);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet][battle] install failed: " + e);
                return false;
            }
        }

        private static bool TryPatch(Harmony h, Type target, string method, Type[] args,
                                     string prefix, string postfix)
        {
            try
            {
                MethodInfo mi = target.GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null, args, null);
                if (mi == null)
                {
                    // retry without DeclaredOnly: the member may live on a base type
                    mi = target.GetMethod(method,
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static,
                        null, args, null);
                }
                if (mi == null)
                {
                    Debug.LogWarning("[RuinaNet][battle] method not found: " + target.Name + "." + method);
                    return false;
                }

                HarmonyMethod pre = null;
                HarmonyMethod post = null;
                if (prefix != null)
                {
                    MethodInfo p = typeof(BattleSyncPatches).GetMethod(prefix,
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (p == null)
                    {
                        Debug.LogWarning("[RuinaNet][battle] patch body missing: " + prefix);
                        return false;
                    }
                    pre = new HarmonyMethod(p);
                }
                if (postfix != null)
                {
                    MethodInfo p = typeof(BattleSyncPatches).GetMethod(postfix,
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (p == null)
                    {
                        Debug.LogWarning("[RuinaNet][battle] patch body missing: " + postfix);
                        return false;
                    }
                    post = new HarmonyMethod(p);
                }

                h.Patch(mi, pre, post);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] patch failed " + target.Name + "." + method + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Attach a finalizer on its own (no prefix, no postfix).  The five
        /// argument Harmony.Patch overload is obsolete-as-error in the bundled
        /// HarmonyX, so this goes through PatchProcessor, which is the supported
        /// route for a lonely finalizer.
        /// </summary>
        private static bool TryPatchFinalizer(Harmony h, Type target, string method, Type[] args,
                                              string finalizer)
        {
            try
            {
                MethodInfo mi = target.GetMethod(method,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null, args, null);
                if (mi == null)
                {
                    mi = target.GetMethod(method,
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static,
                        null, args, null);
                }
                if (mi == null)
                {
                    Debug.LogWarning("[RuinaNet][battle] method not found: " + target.Name + "." + method);
                    return false;
                }

                MethodInfo fin = typeof(BattleSyncPatches).GetMethod(finalizer,
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (fin == null)
                {
                    Debug.LogWarning("[RuinaNet][battle] patch body missing: " + finalizer);
                    return false;
                }

                PatchProcessor proc = new PatchProcessor(h, mi);
                proc.AddFinalizer(new HarmonyMethod(fin));
                proc.Patch();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] patch failed " + target.Name + "." + method + ": " + e.Message);
                return false;
            }
        }

        // ================= patch bodies =================

        /// <summary>
        /// Finalizer for BattleUnitModel.IsTargetable.  A workshop mod here
        /// (AbnormalityRemake) reads its own dictionary with the indexer inside
        /// its postfix, so when its initialisation was cut short - which happens
        /// whenever two instances load the game at the same time, because they
        /// share one workshop folder - every IsTargetable call throws.  That
        /// throw lands in BattleUnitModel.OnRoundStartOnlyUI ->
        /// BookModel.GetSpeedDiceRule and the round start dies half way: the
        /// second instance ends up with no speed dice row at all.  Swallowing
        /// the exception keeps the vanilla result (the mod's tweak simply does
        /// not apply) and lets the round come up; Compat then puts the broken
        /// initialisation back together so it stops throwing for good.
        /// </summary>
        private static Exception IsTargetable_Fin(Exception __exception)
        {
            if (__exception == null) return null;
            if (!NetLink.Linked) return __exception;    // solo play: leave the game's own error alone
            Compat.NoteForeignThrow(__exception);
            return null;
        }

        /// <summary>
        /// StageController.CompleteApplyingLibrarianCardPhase(bool auto)
        ///
        /// This is the one and only place the battle advances past the card
        /// placement phase.  When the peer has not locked in yet we swallow the
        /// call (return false) and remember that the gate is armed; BattleSync
        /// releases it later by re-calling the method with auto = true.
        /// </summary>
        private static bool Gate_Pre(bool __0)
        {
            if (__0) return true;                       // our own release, or the game's auto path
            if (!BattleSync.Active) return true;        // single player
            // Only the arranging phase may arm a round.  The other two callers of this
            // method (an emotion card and a passive, both mid-battle) would otherwise
            // bump the round counter out of lockstep with the peer.
            if (!BattleSync.InCardPhase()) return true;
            // Already gated this round: keep blocking while the gate is still
            // armed, so mashing the button cannot slip past a waiting peer.
            if (BattleSync.RoundHandled) return !BattleSync.GateArmed;

            // Arm only.  The hand-off happens in BattleSync.Tick on the next
            // frame, because releasing from inside this prefix would re-enter
            // CompleteApplyingLibrarianCardPhase and land right back here.
            BattleSync.ArmGate(-1, "button");
            return false;
        }

        /// <summary>
        /// StageController.CheckInput(bool force)
        ///
        /// Runs once a frame while the battle wants input.  Every guard inside it
        /// (an open popup, the battle story, a returning camera, a running tutorial)
        /// returns silently, so a press that got eaten left no trace anywhere -- the
        /// reception simply never started a round.  Catching the press here arms the
        /// round gate anyway, and writes the guard states down on the way.
        /// force == true means the big centre button was clicked.
        /// </summary>
        private static void CheckInput_Post(bool forcelyInput)
        {
            try
            {
                if (!BattleSync.InputWanted(forcelyInput)) return;
                BattleSync.ArmFromInput(forcelyInput ? "button" : "space");
            }
            catch (Exception) { }
        }

        /// <summary>
        /// BattleUIInputController.CheckInputBattleStart(): the space bar / left menu
        /// key path on its own, caught even on frames where CheckInput returned early.
        /// </summary>
        private static void CheckBattleStart_Post(bool __result)
        {
            if (!__result) return;
            try
            {
                if (!BattleSync.InputWanted(false)) return;
                BattleSync.ArmFromInput("space-key");
            }
            catch (Exception) { }
        }

        private static float _lastAutoNoticeAt = -10f;

        /// <summary>Disable vanilla P-key automatic page placement in co-op.</summary>
        private static bool AutoCard_Pre()
        {
            if (!BattleSync.Active) return true;
            if (Time.unscaledTime - _lastAutoNoticeAt > 1f)
            {
                _lastAutoNoticeAt = Time.unscaledTime;
                Room.Notice("联机战斗中不能使用【P】一键出牌，请分别为自己的司书配牌");
            }
            Debug.Log("[RuinaNet][battle] blocked vanilla auto-card command in co-op");
            return false;
        }

        private static bool EnemyAuto_Pre()
        {
            return !BattleSync.Active || BattleSync.IsHost;
        }

        private static BattleUnitModel SpeedDiceUnit(LOR_BattleUnit_UI.SpeedDiceUI ui)
        {
            if (ui == null || _speedDiceViewField == null) return null;
            BattleUnitView view = _speedDiceViewField.GetValue(ui) as BattleUnitView;
            return view == null ? null : view.model;
        }

        /// <summary>Guard the direct right-click cancellation path.</summary>
        private static bool DeselectCard_Pre(LOR_BattleUnit_UI.SpeedDiceUI __instance,
                                             ref EgoReservationSync.RemoveState __state)
        {
            __state = null;
            if (!BattleSync.Active || !BattleSync.InCardPhase()) return true;
            try
            {
                BattleUnitModel unit = SpeedDiceUnit(__instance);
                if (unit == null) return true;
                if (BattleSync.GateArmed)
                {
                    Refuse(unit, "pages are locked after local ready");
                    Room.Notice("你已经准备完成，本回合的书页已锁定");
                    return false;
                }
                if (Ownership.IsPeer(unit))
                {
                    Refuse(unit, "cannot right-click pages from the peer's librarian");
                    Room.Notice("不能撤销队友司书使用的书页");
                    return false;
                }
                if (_speedDiceIndexField != null)
                {
                    int slot = (int)_speedDiceIndexField.GetValue(__instance);
                    __state = EgoReservationSync.BeforeRemove(unit, slot);
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] right-click ownership check failed: " + e.Message);
                return true;
            }
        }

        private static void DeselectCard_Post(LOR_BattleUnit_UI.SpeedDiceUI __instance,
                                              EgoReservationSync.RemoveState __state)
        {
            if (!BattleSync.Active || !BattleSync.InCardPhase()) return;
            EgoReservationSync.AfterRemove(__state);
            BattleUnitModel unit = SpeedDiceUnit(__instance);
            if (unit == null || !BattleSync.IsMine(unit)) return;
            try
            {
                HandSync.SendUnit(BattleSync.Round, unit, true);
                BattleSync.PublishUnitCards(unit, true);
                ClashSync.PublishEnemyTargets();
                BattleSync.RefreshHandUi();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] immediate right-click publish failed: " + e.Message);
            }
        }

        private static bool EmotionPassive_Pre()
        {
            return EmotionChoiceSync.AllowLocalChoice();
        }

        private static void EmotionPassive_Post(EmotionPassiveCardUI __0)
        {
            if (!BattleSync.Active || !BattleSync.IsHost || EmotionChoiceSync.ApplyingRemote || __0 == null) return;
            EmotionCardXmlInfo card = __0.Card;
            if (card != null && card.TargetType != EmotionTargetType.SelectOne)
                EmotionChoiceSync.HostPickedPassive(card, null);
        }

        private static bool EmotionTarget_Pre()
        {
            return EmotionChoiceSync.AllowLocalChoice();
        }

        private static void EmotionTarget_Post(LevelUpUI __instance, BattleUnitModel __0)
        {
            if (!BattleSync.Active || !BattleSync.IsHost || EmotionChoiceSync.ApplyingRemote) return;
            EmotionChoiceSync.HostPickedPassive(EmotionChoiceSync.Selected(__instance), __0);
        }

        private static bool EmotionEgoCard_Pre()
        {
            return EmotionChoiceSync.AllowLocalChoice();
        }

        private static void EmotionEgoCard_Post(BattleDiceCardUI __0)
        {
            if (!BattleSync.Active || !BattleSync.IsHost || EmotionChoiceSync.ApplyingRemote || __0 == null) return;
            EmotionChoiceSync.HostPickedEgo(__0.EgoXmlData);
        }

        private static bool EmotionEgo_Pre()
        {
            return EmotionChoiceSync.AllowLocalChoice();
        }

        private static void EmotionEgo_Post(EmotionEgoCardUI __0)
        {
            if (!BattleSync.Active || !BattleSync.IsHost || EmotionChoiceSync.ApplyingRemote || __0 == null) return;
            EmotionChoiceSync.HostPickedEgo(__0.Card);
        }

        /// <summary>Only the owner may remove a librarian's equipped pages.</summary>
        private static bool UnEquip_Pre(BattleUnitModel __0)
        {
            if (!BattleSync.Active || __0 == null) return true;
            if (BattleSync.GateArmed)
            {
                Refuse(__0, "pages are locked after local ready");
                Room.Notice("你已经准备完成，本回合的书页已锁定");
                return false;
            }
            if (Ownership.IsPeer(__0))
            {
                Refuse(__0, "cannot remove pages from the peer's librarian");
                Room.Notice("不能撤销队友司书使用的书页");
                return false;
            }
            return true;
        }

        private static void UnEquip_Post(BattleUnitModel __0)
        {
            if (!BattleSync.Active || __0 == null || !BattleSync.IsMine(__0)) return;
            try
            {
                HandSync.SendUnit(BattleSync.Round, __0, true);
                BattleSync.PublishUnitCards(__0, true);
                ClashSync.PublishEnemyTargets();
                BattleSync.RefreshHandUi();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] immediate unequip publish failed: " + e.Message);
            }
        }

        /// <summary>
        /// BattleUnitCardsInHandUI.SetCardsObject(BattleUnitModel unitModel, bool isClicked)
        ///
        /// Hand the panel over to a peer-owned librarian and the only thing the
        /// player can do with it is select cards that ApplySelectedCard refuses:
        /// ten clicks, ten identical log lines, nothing happens.  Only a *click*
        /// is refused, so hovering still previews the peer's hand.
        /// </summary>
        private static bool CardsObject_Pre(BattleUnitModel __0, bool __1)
        {
            if (!BattleSync.Active || !__1 || __0 == null) return true;
            try
            {
                if (BattleSync.GateArmed)
                {
                    Refuse(__0, "pages are locked after local ready");
                    return false;
                }
                // Only an explicit peer claim may refuse a click.  Refusing on "not
                // mine" also catches the positional fallback, and a wrong fallback
                // locks the player out of their own librarians -- every card click
                // blocked, which is exactly "the client cannot play a card".
                if (!Ownership.IsPeer(__0)) return true;
                if (_lastRefusedUnit != __0.index)
                {
                    _lastRefusedUnit = __0.index;
                    Debug.Log("[RuinaNet][battle] librarian " + __0.index +
                              " belongs to the peer -- select one of your own to give orders");
                }
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] hand ownership check failed: " + e.Message);
                return true;
            }
        }

        /// <summary>Last unit index whose ownership we already complained about.</summary>
        private static int _lastRefusedUnit = -1;

        /// <summary>Throttle for the per-frame dice guard.</summary>
        private static float _lastDiceGuardAt = -1f;

        /// <summary>
        /// UIController.PrepareBattle(StageClassInfo stageInfo, List&lt;DropBookXmlInfo&gt; selectedBooks)
        ///
        /// The single place the game enters a reception -- the confirm button in
        /// the invitation screen, a workshop invitation, and our own "加入" all
        /// funnel through it.  Telling the peer here is what lets the other side
        /// press one button instead of hunting for the same invitation by hand.
        /// </summary>
        private static bool PrepareBattle_Pre()
        {
            if (!CreatureSync.Active) return true;
            Room.Notice("请先取消异想体联机，再进入普通接待"); return false;
        }
        private static void PrepareBattle_Post(StageClassInfo __0, List<DropBookXmlInfo> __1)
        {
            if (CreatureSync.Active) return;
            StageSync.Broadcast(__0, __1);
        }

        private static bool CreatureFirst_Pre(SephirahType __0) { return CreatureSync.First(__0); }
        private static bool CreatureReplay_Pre(object __instance, bool __0) { return CreatureSync.Replay(__instance, __0); }
        private static bool CreatureEnd_Pre() { return CreatureSync.GateEnd(); }
        private static void CreatureGameOver_Post() { CreatureSync.OnGameOver(); }
        private static bool CreatureFinal_Pre()
        {
            if (!Room.Linked && !CreatureSync.Active) return true;
            Room.Notice("楼层解放战暂未接入联机，请先测试普通异想体战"); return false;
        }

        /// <summary>
        /// GlobalGameManager.LoadBattleScene()
        ///
        /// The single funnel for "开始战斗": UIBattleSettingPanel (the 开启舞台
        /// button), UIController, UIBgScreenChangeAnim and the creature-rebattle
        /// slot all call it, and BattleSceneRoot.StartBattle() only has two
        /// callers in the whole assembly (this one and the inter-wave handler).
        /// Swallowing the press here is therefore the cheapest possible co-op
        /// start gate -- no UI surgery, and the host stays the only side that can
        /// boot the battle.
        /// </summary>
        private static bool LoadBattleScene_Pre()
        {
            return Room.GateBattleStart();
        }

        /// <summary>
        /// StageController.SortUnitPhase -- the only place the game rolls speed
        /// dice for the whole field.
        /// </summary>
        private static void SortUnitPhase_Post()
        {
            FillMissingDice(true);
            // The battle UI is built by now: safe to repaint the units whose deck
            // was swapped in SetUnitData (SetUnitData runs before the UI exists,
            // and refreshing there crashed ExtendedLoader's postfix).
            Loadout.FlushUiRefresh();
            // Same moment, second job: the host publishes the whole speed-dice
            // table so both machines open the round on the same numbers.
            BattleSync.OnRoundSorted();
        }

        /// <summary>
        /// BattleUIInputController.UpdateChracterSlots(bool, Predicate, bool)
        ///
        /// Last-ditch guard.  Its RemoveAll predicate dereferences
        /// x.speedDiceResult.Count, so a single null list turns every frame into
        /// an exception and the speed dice row never appears.
        /// </summary>
        private static void ChracterSlots_Pre()
        {
            // This runs per frame, so only rescan a few times a second.
            if (Time.unscaledTime - _lastDiceGuardAt < 0.25f) return;
            FillMissingDice(false);
        }

        /// <summary>
        /// UIBattleSettingPanel.SelectedToggles(UICharacterSlot)
        ///
        /// The single funnel for "tick a librarian on the 开启舞台 screen".  A
        /// librarian the peer already claimed must not be tickable here (the two
        /// picks would otherwise both end up in the field line-up), so the prefix
        /// swallows that click before the game sees it.
        /// </summary>
        private static bool Toggles_Pre(UI.UICharacterSlot __0)
        {
            return Lineup.OnToggleClicked(__0);
        }

        /// <summary>
        /// The game just rewrote some ticks -- its own cap logic, or the
        /// single-slot wave special case.  Put the union of both players' picks
        /// back on the panel.
        /// </summary>
        private static void Toggles_Post()
        {
            Lineup.AfterGameApplied();
        }

        /// <summary>
        /// A battle unit has just been built and has just installed its own pages.
        /// If the peer owns this librarian and told us which pages it carries, put
        /// those pages in instead -- both simulations must draw from the same deck
        /// or nothing downstream can ever agree.
        /// </summary>
        private static void SetUnitData_Pre(BattleUnitModel __instance, UnitBattleDataModel __0)
        {
            Loadout.PrepareUnit(__instance, __0);
        }

        private static void CoreBookChanged_Post()
        {
            Loadout.SendMine();
        }

        private static void PreparationOpened_Post()
        {
            Loadout.RefreshPreparation();
        }

        private static void SetUnitData_Post(BattleUnitModel __instance)
        {
            Loadout.ApplyDeck(__instance);
        }

        /// <summary>
        /// DiceStatCalculator.MakeDiceResult(): every die face in the game comes
        /// out of here.  Host rolls deterministically (identical on both machines
        /// by construction, and the authoritative broadcast in Roll_Post carries
        /// the fully corrected value).  Client takes the host's broadcast value
        /// whenever it has arrived, and falls back to the same deterministic
        /// scheme when it has not -- a fallback is repaired by the late
        /// correction in Roll_Post / CorrectAwaitingDice, so the two machines
        /// converge without ever stalling the battle.
        /// </summary>
        private static bool MakeDiceResult_Pre(int diceMin, int diceFace, int tuneLevel, ref int __result)
        {
            if (!BattleSync.Active) return true;
            try
            {
                __result = BattleSync.NextDiceValue(diceMin, diceFace);
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][dice] net dice value failed: " + e.Message);
                return true;
            }
        }

        /// <summary>
        /// speedDiceResult has no initialiser, so a unit that enters the alive
        /// list after SortUnitPhase keeps a null list forever, and the hand UI
        /// reads .Count on it every frame.
        ///
        /// Always try the game's own roll, on both the expensive and the cheap
        /// path -- SortUnitPhase rolls unconditionally, so a unit still parked on
        /// null is simply one the game has not reached yet.  Never leave a
        /// half-built empty list behind: SortUnitPhase skips a unit that already
        /// has dice, NetState.ApplySpeeds bails out on .Count == 0, and the result
        /// is a participant who fights the whole reception without a single speed
        /// die.  Null (plus the peer's snapshot rebuilding it) is strictly better.
        /// </summary>
        private static void FillMissingDice(bool roll)
        {
            try
            {
                _lastDiceGuardAt = Time.unscaledTime;
                List<BattleUnitModel> list = BattleObjectManager.instance.GetAliveList();
                if (list == null) return;

                int patched = 0;
                int stubbed = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    BattleUnitModel u = list[i];
                    if (u == null || u.speedDiceResult != null) continue;
                    // A failed passive reconstruction used to leave a null entry in
                    // PassiveList.  RollSpeedDice consults that list before it can
                    // allocate speedDiceResult, so one bad entry removed every die
                    // from the unit.  Repair the invariant before retrying the game
                    // roll and retain the exception in the log when it still fails.
                    Loadout.SanitizePassives(u);
                    try { u.RollSpeedDice(); }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[RuinaNet][battle] speed roll failed for unit " +
                                         u.index + ": " + e.Message);
                    }
                    if (u.speedDiceResult == null || u.speedDiceResult.Count == 0)
                    {
                        // Do not poison the unit with an empty list.  The host SPEED
                        // snapshot can rebuild a null list, while an empty list makes
                        // several game paths believe the roll already happened.
                        u.speedDiceResult = null;
                        stubbed++;
                        continue;
                    }
                    patched++;
                }
                if (patched > 0 || stubbed > 0)
                {
                    Debug.Log("[RuinaNet][battle] dice guard: rolled " + patched +
                              " unit(s), could not roll " + stubbed + " (roundStart=" + roll + ")");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] dice guard failed: " + e.Message);
            }
        }

        /// <summary>
        /// BattleUnitCardsInHandUI.ApplySelectedCard(BattleUnitModel target, int targetSlot)
        ///
        /// The only caller is SpeedDiceUI.OnClickSpeedDice (LOR_BattleUnit_UI\SpeedDiceUI.cs:635).
        /// Blocking here costs nothing: no light is spent and no card leaves the
        /// hand until the card actually lands in a slot array, so swallowing the
        /// call simply leaves the card selected in the UI.
        /// </summary>
        private static bool CardsApply_Pre(BattleUnitModel __0, int __1,
                                           ref CardApplyState __state)
        {
            __state = null;
            if (!BattleSync.Active) return true;
            try
            {
                if (BattleSync.GateArmed)
                {
                    Refuse(__0, "pages are locked after local ready");
                    Room.Notice("你已经准备完成，本回合的书页已锁定");
                    return false;
                }
                // Same rule as CardsObject_Pre: only an explicit peer claim blocks a
                // play, so a librarian nobody claimed is never made unplayable.
                BattleUnitCardsInHandUI ui = SingletonBehavior<BattleManagerUI>.Instance.ui_unitCardsInHand;
                if (ui != null)
                {
                    BattleUnitModel src = ui.SelectedModel;
                    if (src != null && Ownership.IsPeer(src))
                    {
                        Refuse(__0, "no orders for the peer's librarian");
                        return false;
                    }
                    BattleDiceCardUI selected = ui.GetSelectedCard();
                    BattleDiceCardModel card = selected == null ? null : selected.CardModel;
                    EgoReservationSync.ApplyState ego;
                    if (!EgoReservationSync.BeforeApply(src, card, out ego)) return false;
                    __state = new CardApplyState { Source = src, Ego = ego };
                }
                // The target can legitimately be a teammate (support pages and
                // redirected attacks).  Ownership applies to the source librarian
                // whose hand is issuing the order, not to the selected target.
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] control check failed: " + e.Message);
            }
            return true;
        }

        private static void CardsApply_Post(CardApplyState __state)
        {
            if (!BattleSync.Active) return;
            EgoReservationSync.AfterApply(__state == null ? null : __state.Ego);
            if (__state == null || __state.Source == null || !BattleSync.IsMine(__state.Source)) return;
            try
            {
                HandSync.SendUnit(BattleSync.Round, __state.Source, true);
                BattleSync.PublishUnitCards(__state.Source, true);
                ClashSync.PublishEnemyTargets();
                BattleSync.RefreshHandUi();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] immediate placement publish failed: " + e.Message);
            }
        }

        /// <summary>One log line per librarian, not one per click.</summary>
        private static void Refuse(BattleUnitModel about, string why)
        {
            int idx = about == null ? -1 : about.index;
            if (_lastRefusedUnit == idx) return;
            _lastRefusedUnit = idx;
            Debug.Log("[RuinaNet][battle] " + why + " (unit " + idx + ")");
        }

        /// <summary>BattleDiceBehavior.RollDice(): remember who is rolling.</summary>
        private static void Roll_Pre(BattleDiceBehavior __instance)
        {
            if (!BattleSync.Active) return;
            BattleSync.SetCurrentRoll(__instance);
            Dice.Advance();
        }

        /// <summary>
        /// BattleDiceBehavior.RollDice() finished.
        ///
        /// Host: broadcast the fully corrected value.
        /// Client: if MakeDiceResult already consumed the host's queued value,
        /// nothing to do.  Otherwise this roll used the deterministic fallback;
        /// the host's value may have landed in the meantime (the die is still
        /// spinning, damage is settled after the animation) -- overwrite now.  If
        /// it has not landed yet, register the behaviour so HandleDice corrects
        /// it the moment the DICE packet arrives.
        /// </summary>
        private static void Roll_Post(BattleDiceBehavior __instance)
        {
            if (!BattleSync.Active) return;
            try
            {
                if (_diceResultField == null) return;
                int local = (int)_diceResultField.GetValue(__instance);

                if (BattleSync.IsHost)
                {
                    BattleSync.BroadcastRoll(__instance, local);
                    return;
                }

                // Client side.
                if (BattleSync.ConsumeHostValueMark(__instance)) return;

                string key = BattleSync.CurrentDiceKeyOf(__instance);
                if (key == null) return;
                Queue<int> q;
                if (BattleSync.HostRollQueues.TryGetValue(key, out q) && q.Count > 0)
                {
                    // The host's value arrived after MakeDiceResult fell back.
                    // The die is still spinning, so the clash settles on the
                    // host's number after all.
                    _diceResultField.SetValue(__instance, q.Dequeue());
                    BattleSync.LateCorrectionCount++;
                    Debug.Log("[RuinaNet][dice] late correction for " + key +
                              ": " + local + " -> host value");
                    return;
                }
                // Nothing yet: register for correction when the DICE packet lands.
                BattleSync.AwaitDiceCorrection(key, __instance);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][battle] roll reconcile failed: " + e.Message);
            }
            finally
            {
                BattleSync.SetCurrentRoll(null);
            }
        }

        /// <summary>
        /// The host's value for a roll the client already made locally has just
        /// arrived: overwrite that behaviour's result.  The die is usually still
        /// spinning, so the clash resolves on the host's number.
        /// </summary>
        public static void CorrectAwaitingDice(string key, int value)
        {
            if (_diceResultField == null) return;
            BattleDiceBehavior b = BattleSync.NextAwaitingDice(key);
            if (b == null) return;
            try
            {
                _diceResultField.SetValue(b, value);
                BattleSync.LateCorrectionCount++;
                Debug.Log("[RuinaNet][dice] corrected an in-flight roll for " + key +
                          " to the host value " + value);
            }
            catch (Exception) { }
        }

        /// <summary>StageController.StartBattle(): a fresh battle, drop stale per-round state.</summary>
        private static void BattleStart_Post()
        {
            _lastRefusedUnit = -1;
            BattleSync.OnBattleStart();
            CreatureSync.BattleStarted();
            Debug.Log("[RuinaNet][battle] battle started, sync state reset");
        }

        private static void BattleClosed_Post()
        {
            if (CreatureSync.Active) return;
            Loadout.Reset();
            Lineup.Clear();
            Debug.Log("[RuinaNet][lineup] battle closed, picks cleared for the next reception");
        }
    }
}
