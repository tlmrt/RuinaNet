using System;
using System.Reflection;
using UI;
using UnityEngine;

namespace RuinaNet
{
    internal static class LoadoutUi
    {
        private static readonly FieldInfo InfoUnit = typeof(UIBattleSettingLibrarianInfoPanel).GetField(
            "unitdata", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static void RefreshPreparation(UnitBattleDataModel data, UnitDataModel previous)
        {
            try
            {
                UI.UIController controller = UI.UIController.Instance;
                if (controller == null || controller.CurrentUIPhase != UIPhase.BattleSetting) return;
                UIBattleSettingPanel panel = controller.GetUIPanel(UIPanelType.BattleSetting) as UIBattleSettingPanel;
                if (panel == null) return;
                if (panel.currentAvailbleUnitslots != null)
                    foreach (UICharacterSlot slot in panel.currentAvailbleUnitslots)
                    {
                        if (slot == null || slot.unitBattleData != data || !slot.gameObject.activeInHierarchy) continue;
                        bool selected = slot.IsToggleSelected;
                        // This refresh rebinds the UnitData pointer and hp bar.
                        // Restore the checkbox: SetBattleCharacter clears it.
                        slot.SetBattleCharacter(data);
                        slot.SetToggle(selected);
                    }
                RefreshInfo(panel.InfoRightPanel, data, previous);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][loadout] preparation UI refresh failed: " + e.Message);
            }
        }

        private static void RefreshInfo(UIBattleSettingLibrarianInfoPanel panel, UnitBattleDataModel data, UnitDataModel previous)
        {
            if (panel == null || InfoUnit == null || !panel.gameObject.activeInHierarchy) return;
            UnitDataModel shown = InfoUnit.GetValue(panel) as UnitDataModel;
            if (object.ReferenceEquals(shown, previous) || object.ReferenceEquals(shown, data.unitData))
                panel.SetData(data.unitData);
        }
    }
}
