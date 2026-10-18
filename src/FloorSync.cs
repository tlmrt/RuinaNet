// RuinaNet - library floor (Sephirah) sync.
//
// Both players share one save file, but the *floor* a reception is fought on is
// per-process state: StageController keeps its own _currentFloor, and it is what
// PrepareBattle copies out of UIController.CurrentSephirah the moment a reception
// is opened.
//
// That single value decides almost everything about the battle:
//   * which librarians stand in the line-up (StageLibraryFloorModel.InitUnitList
//     builds each floor's roster once, at StageModel.Init, and only the current
//     floor is fielded),
//   * therefore which core books, key pages and passives every unit carries,
//   * and therefore which buffs are already on the field in round 1.
//
// Two machines that open the "same" reception on two different floors are
// therefore not in the same battle at all -- different teams, different passives,
// different opening buffs -- which is exactly the "开局 buff 不同步 / 楼层不一样"
// report.  No amount of in-battle buff or hand sync can repair it, because the
// two sides never had the same units to begin with.
//
// Fix: the reception's opener carries its floor inside the STAGE message, and the
// host keeps restating its floor while both players sit on the reception.  The
// client points itself at that floor.  Setting it *before* PrepareBattle runs is
// the clean path (PrepareBattle itself pushes UIController.CurrentSephirah into
// StageController), and setting it afterwards is also safe: every floor's model is
// already built by then, so re-pointing only changes which one is fielded.
//
// The two calls in Adopt() are exactly the pair the game's own floor button runs
// (UIBattleSettingPanel.SetNextSephirah), so the UI, the stage model and the
// line-up all move together.
//
// Wire:  FLOOR <int SephirahType>
//
// Everything here is inert unless a link is up AND the reception is a co-op one.

using System;
using System.Globalization;
using UnityEngine;

namespace RuinaNet
{
    internal static class FloorSync
    {
        /// <summary>How often the host restates its floor while the room is open.</summary>
        private const float SendGap = 1f;

        /// <summary>The floor the other side told us about; -1 when none.</summary>
        private static int _hostFloor = -1;
        /// <summary>Last floor the host broadcast, so only real changes are logged.</summary>
        private static int _lastSent = int.MinValue;
        private static float _sinceSend = SendGap;
        /// <summary>Client: last floor we actually pushed into the game.</summary>
        private static int _applied = -1;

        public static bool Active
        {
            get { return NetLink.Running && NetLink.Linked && Room.CoopStage; }
        }

        /// <summary>The floor this machine is currently pointed at, or -1 if unknown.</summary>
        public static int Local()
        {
            try
            {
                StageController sc = Singleton<StageController>.Instance;
                if (sc == null) return -1;
                return (int)sc.CurrentFloor;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Remember the other side's floor, and (when it makes sense) follow it.
        /// Called both from the STAGE broadcast, so the floor is already right
        /// before the client even opens the reception, and from the FLOOR
        /// heartbeat, so a floor the host changes later still lands.
        /// </summary>
        public static void Note(int floor)
        {
            if (floor < 0 || floor == _hostFloor) return;
            _hostFloor = floor;
            Debug.Log("[RuinaNet][floor] peer floor is " + Name(floor) +
                      " (we are on " + Name(Local()) + ")");
            Reconcile();
        }

        public static void Tick(float dt)
        {
            if (CreatureSync.Active) return;
            if (!NetLink.Running || !NetLink.Linked)
            {
                Reset();
                return;
            }

            if (BattleSync.IsHost)
            {
                // Nobody to tell until the peer actually joined the reception.
                if (!Room.CoopStage) return;
                _sinceSend += dt;
                int f = Local();
                if (f < 0) return;
                bool changed = f != _lastSent;
                if (!changed && _sinceSend < SendGap) return;
                _sinceSend = 0f;
                _lastSent = f;
                NetLink.Send("FLOOR " + f.ToString(CultureInfo.InvariantCulture));
                if (changed)
                {
                    Debug.Log("[RuinaNet][floor] host floor is now " + Name(f) +
                              " -- restating it so both sides field the same line-up");
                }
                return;
            }

            Reconcile();
        }

        /// <summary>
        /// Client only: if the host is on a different floor while we are still
        /// looking at 开启舞台, follow it.  Never touches us mid-battle -- by then
        /// the units exist and the floor is history.
        /// </summary>
        private static void Reconcile()
        {
            if (BattleSync.IsHost) return;
            if (_hostFloor < 0) return;
            if (!Room.CoopStage) return;
            if (!OnBattleSetting()) return;
            if (Local() == _hostFloor && _applied == _hostFloor) return;
            Adopt(_hostFloor, false);
        }

        public static bool HandleMessage(string[] p)
        {
            if (CreatureSync.Active) return true;
            if (p == null || p.Length < 2 || p[0] != "FLOOR") return false;
            int f;
            if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out f)) return true;
            Note(f);
            return true;
        }

        /// <summary>
        /// Point this machine at the given floor, stage model and UI together.
        /// <paramref name="force"/> skips the "already there" shortcut, which is
        /// what the join path wants: PrepareBattle is about to read the UI's
        /// current floor, and we need it to read ours.
        /// </summary>
        public static bool Adopt(int floor, bool force)
        {
            try
            {
                if (floor < 0) return false;
                SephirahType sep = (SephirahType)floor;

                // The join path calls this *before* PrepareBattle, and the stage
                // model may not exist yet -- but that is fine: PrepareBattle itself
                // reads UIController.CurrentSephirah and pushes it into the stage,
                // so setting the UI copy is what actually decides the floor there.
                // The two calls are the pair the game's own floor button runs.
                int now = -1;
                StageController sc = Singleton<StageController>.Instance;
                if (sc != null)
                {
                    now = (int)sc.CurrentFloor;
                    if (!force && now == floor)
                    {
                        _applied = floor;
                        return true;
                    }
                    sc.SetCurrentSephirah(sep);
                }

                UI.UIController ui = UI.UIController.Instance;
                if (ui != null) ui.SetCurrentSephirah(sep);

                _applied = floor;
                Debug.Log("[RuinaNet][floor] floor " + Name(now) + " -> " + Name(floor) +
                          (force ? " (before opening the reception)" : " (following the host)"));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][floor] adopt failed: " + e.Message);
                return false;
            }
        }

        private static void Reset()
        {
            _hostFloor = -1;
            _lastSent = int.MinValue;
            _sinceSend = SendGap;
            _applied = -1;
        }

        private static bool OnBattleSetting()
        {
            try
            {
                UI.UIController ui = UI.UIController.Instance;
                return ui != null && ui.CurrentUIPhase == UI.UIPhase.BattleSetting;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string Name(int floor)
        {
            if (floor < 0) return "?";
            return ((SephirahType)floor).ToString() +
                   "(" + floor.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
