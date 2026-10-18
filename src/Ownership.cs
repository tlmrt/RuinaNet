using System;

namespace RuinaNet
{
    /// <summary>
    /// One ownership decision used by every input path.  Floor-exclusive
    /// librarians (the patron librarian of each floor, including Roland) always
    /// belong to the host.  Assistant librarians follow explicit line-up claims;
    /// an unclaimed assistant stays Unknown so a positional guess cannot lock a
    /// player out of their own hand.
    /// </summary>
    internal static class Ownership
    {
        internal enum Side
        {
            Unknown,
            Local,
            Peer
        }

        public static Side Of(BattleUnitModel unit)
        {
            if (!BattleSync.Active || unit == null) return Side.Local;
            try
            {
                if (unit.faction != Faction.Player)
                    return BattleSync.IsHost ? Side.Local : Side.Peer;

                UnitBattleDataModel data = unit.UnitData;
                if (CreatureSync.Active)
                {
                    bool local;
                    if (CreatureSync.TryOwner(data, out local)) return local ? Side.Local : Side.Peer;
                    // A scripted summon outside the agreed roster stays host-owned.
                    return BattleSync.IsHost ? Side.Local : Side.Peer;
                }
                if (IsFloorExclusive(unit))
                    return BattleSync.IsHost ? Side.Local : Side.Peer;

                if (Lineup.LocalClaimed(unit)) return Side.Local;
                if (Lineup.PeerClaimed(unit)) return Side.Peer;
            }
            catch (Exception) { }
            return Side.Unknown;
        }

        public static bool IsPeer(BattleUnitModel unit)
        {
            return Of(unit) == Side.Peer;
        }

        /// <summary>
        /// The patron librarian is the first unit of every floor.  Keter's Roland
        /// is created with isSephirah=false and has no stable nameID on the line-up
        /// screen, so the old flag-only test missed exactly that unit.  Object
        /// identity against the floor roster survives the transition into battle
        /// and works after switching floors as well.
        /// </summary>
        public static bool IsFloorExclusive(BattleUnitModel unit)
        {
            if (unit == null) return false;
            try
            {
                UnitBattleDataModel data = unit.UnitData;
                if (data != null && data.unitData != null && data.unitData.isSephirah) return true;
                // BattleUnitModel.index preserves the floor-roster slot passed to
                // CreateLibrarianUnit; the patron slot is always zero even when
                // its UnitDataModel lacks both nameID and isSephirah (Roland).
                if (unit.index == 0) return true;
                StageController sc = Singleton<StageController>.Instance;
                StageLibraryFloorModel floor = sc == null ? null : sc.GetCurrentStageFloorModel();
                if (floor == null) return false;
                System.Collections.Generic.List<UnitBattleDataModel> list = floor.GetUnitBattleDataList();
                if (list == null || list.Count == 0 || list[0] == null) return false;
                return object.ReferenceEquals(data, list[0]) ||
                       (data != null && data.unitData != null &&
                        object.ReferenceEquals(data.unitData, list[0].unitData));
            }
            catch (Exception) { return false; }
        }
    }
}
