using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UI;

namespace RuinaNet
{
    /// <summary>
    /// Stage 3b: who brings which librarian.
    ///
    /// The 开启舞台 screen lets each machine tick its own librarians, and the tick
    /// writes straight into UnitBattleDataModel.IsAddedBattle -- purely locally.
    /// Two players therefore walk into "the same" reception with different line-
    /// ups, which is exactly what the user reported: the two windows showed
    /// different units on the field.
    ///
    /// The agreed policy is "each side picks its own half":
    ///
    ///   * A click on a librarian is mine -- I keep it in _mine and broadcast the
    ///     whole set.
    ///   * The peer's set arrives as LINEUP -- I keep it in _peer and re-assert
    ///     the *union* on my own panel, so both machines end up with the same
    ///     IsAddedBattle flags and therefore the same field.
    ///   * A librarian the peer already claimed cannot be claimed here (the click
    ///     is swallowed with a notice), so the two sets stay disjoint.
    ///   * Ownership during the battle is "whoever picked it", with the old
    ///     positional split as the fallback for librarians nobody touched.
    ///
    /// Inert unless a link is up and the wave allows more than one librarian.
    /// </summary>
    internal static class Lineup
    {
        private static readonly List<int> _mine = new List<int>();
        private static readonly List<int> _peer = new List<int>();

        private static float _lastSendAt = -60f;
        private static float _lastNoticeAt = -60f;
        private static float _lastApplyAt = -60f;
        private static float _lastHeartbeatAt = -60f;

        private const float NoticeGap = 0.6f;
        private const float Heartbeat = 3f;

        public static bool IsHost { get { return BattleSync.IsHost; } }
        public static bool Active
        {
            get
            {
                // Only inside an armed co-op reception: two people who are merely
                // linked but playing their own receptions must not have their
                // librarian panels rewritten under them.
                return NetLink.Running && NetLink.Linked && Room.CoopStage && !CreatureSync.Active;
            }
        }
        public static bool HasPicks { get { return _mine.Count > 0 || _peer.Count > 0; } }
        public static int MyCount { get { return _mine.Count; } }
        public static int PeerCount { get { return _peer.Count; } }

        // ================= public state =================

        public static void Clear()
        {
            if (_mine.Count > 0 || _peer.Count > 0)
            {
                Debug.Log("[RuinaNet][lineup] cleared (was mine=" + ListText(_mine) + " peer=" + ListText(_peer) + ")");
            }
            _mine.Clear();
            _peer.Clear();
            _lastSendAt = -60f;
            _lastApplyAt = -60f;
        }

        /// <summary>How many librarians may stand on the field this wave.</summary>
        public static int Cap()
        {
            try
            {
                StageController sc = Singleton<StageController>.Instance;
                if (sc != null)
                {
                    StageWaveModel wave = sc.GetCurrentWaveModel();
                    if (wave != null)
                    {
                        int n = wave.AvailableUnitNumber;
                        if (n > 0) return n;
                    }
                }
            }
            catch (Exception) { }
            return 4;
        }

        /// <summary>Do we own this unit?  positionFallback keeps solo play intact.</summary>
        public static bool OwnsNameId(int nameId, bool positionFallback)
        {
            if (!HasPicks) return positionFallback;
            if (Contains(_mine, nameId)) return true;
            if (Contains(_peer, nameId)) return false;
            // Nobody touched it -- the positional split still speaks for it.
            return positionFallback;
        }

        public static bool OwnsBattleUnit(BattleUnitModel unit, bool positionFallback)
        {
            if (unit == null) return positionFallback;
            try
            {
                UnitBattleDataModel data = unit.UnitData;
                if (data == null || data.unitData == null) return positionFallback;
                return OwnsNameId(data.unitData.nameID, positionFallback);
            }
            catch (Exception) { return positionFallback; }
        }

        public static bool OwnsUnitData(UnitBattleDataModel data, bool positionFallback)
        {
            if (data == null || data.unitData == null) return positionFallback;
            return OwnsNameId(data.unitData.nameID, positionFallback);
        }

        /// <summary>
        /// True only when the peer *explicitly* claimed this librarian's nameID.
        ///
        /// This is the one test the in-battle control restriction is allowed to use.
        /// "Not mine" also comes out of the positional fallback, and a positional
        /// guess is exactly what cannot be trusted here: when it is wrong the player
        /// is locked out of their own librarians -- every card click refused by
        /// BattleSyncPatches.CardsObject_Pre / CardsApply_Pre -- which is precisely
        /// what "the client cannot play any card" is.  A librarian nobody claimed is
        /// therefore never blocked.
        /// </summary>
        public static bool PeerClaimed(BattleUnitModel unit)
        {
            try
            {
                if (unit == null) return false;
                UnitBattleDataModel d = unit.UnitData;
                if (d == null || d.unitData == null) return false;
                int id = d.unitData.nameID;
                if (id < 0) return false;
                return Contains(_peer, id);
            }
            catch (Exception) { return false; }
        }

        /// <summary>True only when this process explicitly claimed the librarian.</summary>
        public static bool LocalClaimed(BattleUnitModel unit)
        {
            try
            {
                if (unit == null) return false;
                UnitBattleDataModel d = unit.UnitData;
                if (d == null || d.unitData == null) return false;
                int id = d.unitData.nameID;
                if (id < 0) return false;
                return Contains(_mine, id);
            }
            catch (Exception) { return false; }
        }

        // ================= the click =================

        /// <summary>
        /// Prefix on UIBattleSettingPanel.SelectedToggles -- the one funnel every
        /// librarian click goes through.  Returning false swallows the click, so a
        /// refused pick never reaches the game at all.
        /// </summary>
        public static bool OnToggleClicked(UICharacterSlot slot)
        {
            try
            {
                if (!Active) return true;
                if (Cap() <= 1) return true;      // one-slot waves are not worth splitting
                if (slot == null) return true;
                int idx = IndexOf(slot);
                int id = KeyOf(slot, idx);
                if (id < 0) return true;

                string label = Label(slot);
                bool willBeOn = !slot.IsToggleSelected;

                // Slot zero is the current floor's patron librarian.  Roland has
                // no stable nameID here (he becomes synthetic key 100000), which
                // is why checking only isSephirah still let the client claim him.
                if (idx == 0 && !IsHost && willBeOn)
                {
                    Notice("本楼层的专属司书由房主控制");
                    ApplyUnion(true);
                    return false;
                }

                if (willBeOn)
                {
                    if (Contains(_peer, id))
                    {
                        Notice("「" + label + "」是对面的，你选不了");
                        ApplyUnion(true);
                        return false;
                    }
                    // Judge against what the panel actually shows, not against our
                    // own bookkeeping: the game pre-ticks the first slots itself, so
                    // the two can differ by exactly those.
                    if (CountTicked() >= Cap())
                    {
                        Notice("这一场最多 " + Cap() + " 名司书，先取消一个");
                        return false;
                    }
                    _mine.Add(id);
                }
                else
                {
                    if (Contains(_peer, id))
                    {
                        Notice("「" + label + "」是对面的，你取消不了");
                        ApplyUnion(true);
                        return false;
                    }
                    if (CountTicked() <= 1)
                    {
                        Notice("至少要留一名司书");
                        return false;
                    }
                    _mine.Remove(id);
                }
                SendNow();
                Debug.Log("[RuinaNet][lineup] " + (willBeOn ? "picked " : "dropped ") + label +
                          " (id " + id + ") -- mine = " + ListText(_mine));
                ApplyUnion(true);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet][lineup] toggle failed: " + e);
                return true;
            }
        }

        /// <summary>Postfix on the same method: the game just rewrote some ticks.</summary>
        public static void AfterGameApplied()
        {
            try
            {
                if (!Active || !HasPicks) return;
                ApplyUnion(false);
            }
            catch (Exception e) { Debug.LogError("[RuinaNet][lineup] after-apply failed: " + e); }
        }

        // ================= plumbing =================

        public static void Tick(float dt)
        {
            if (!Active)
            {
                if (HasPicks) Clear();
                _defaultsCleared = false;
                return;
            }

            // Before anybody has picked anything the panel would still carry the
            // game's own default ticks (the first AvailableUnitNumber slots).  Those
            // belong to nobody, so un-ticking one would change nothing and both
            // machines would keep the same stale set.  Wipe them so "ticked" and
            // "claimed" mean the same thing from the first click on.
            if (!HasPicks) ClearUnclaimedTicks();

            // A late joiner converges instead of never; three seconds is one
            // datagram of a few bytes.
            if (Time.unscaledTime - _lastHeartbeatAt > Heartbeat)
            {
                _lastHeartbeatAt = Time.unscaledTime;
                if (HasPicks) SendNow();
            }

            if (Time.unscaledTime - _lastApplyAt > 0.5f)
            {
                _lastApplyAt = Time.unscaledTime;
                if (HasPicks) ApplyUnion(false);
            }
        }

        private static bool _defaultsCleared;

        /// <summary>
        /// Turn off every tick that neither side has claimed.  Only runs before the
        /// first pick, so it can never eat a real choice.
        /// </summary>
        private static void ClearUnclaimedTicks()
        {
            try
            {
                UICharacterSlot[] all = Slots();
                if (all == null || all.Length == 0) return;
                int off = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    UICharacterSlot s = all[i];
                    if (s == null || !s.IsToggleSelected) continue;
                    s.SetNoToggleState();
                    off++;
                }
                if (off > 0 && !_defaultsCleared)
                {
                    _defaultsCleared = true;
                    Debug.Log("[RuinaNet][lineup] cleared " + off +
                              " default pick(s) -- each side picks its own now");
                }
            }
            catch (Exception) { }
        }

        public static bool HandleMessage(string[] p)
        {
            if (CreatureSync.Active) return true;
            if (p == null || p.Length < 2) return true;
            if (p[0] != "LINEUP") return false;

            List<int> incoming = Parse(p[1]);
            incoming = MergeDuplicates(incoming);

            bool same = SameSet(incoming, _peer);
            _peer.Clear();
            _peer.AddRange(incoming);

            // Simultaneous picks of the same librarian: the host's claim stands,
            // so only the client gives way.
            int dropped = 0;
            if (!IsHost)
            {
                for (int i = _mine.Count - 1; i >= 0; i--)
                {
                    if (Contains(_peer, _mine[i]))
                    {
                        Debug.Log("[RuinaNet][lineup] host also claimed " + _mine[i] + " -- it is his");
                        _mine.RemoveAt(i);
                        dropped++;
                    }
                }
                if (dropped > 0)
                {
                    SendNow();
                }
            }

            if (!same || dropped > 0)
            {
                Debug.Log("[RuinaNet][lineup] peer brought " + ListText(_peer) +
                          (dropped > 0 ? " (dropped own " + dropped + ")" : ""));
                Notice("对方选了 " + _peer.Count + " 名司书，一共 " + UnionCount() + " 名上场");
            }

            ApplyUnion(true);
            return true;
        }

        // ================= one set of ticks, two machines =================

        /// <summary>Re-assert the union on the local 开启舞台 panel.</summary>
        private static void ApplyUnion(bool force)
        {
            UICharacterSlot[] slots = Slots();
            if (slots == null) return;
            bool changed = false;
            for (int i = 0; i < slots.Length; i++)
            {
                UICharacterSlot s = slots[i];
                if (s == null) continue;
                int id = KeyOf(s, i);
                if (id < 0) continue;
                bool want = Contains(_mine, id) || Contains(_peer, id);
                if (s.IsToggleSelected == want) continue;
                if (want && UnionCount() > Cap())
                {
                    // The union outgrew the wave slot count; the extra one stays off.
                    continue;
                }
                if (want) s.SetYesToggleState();
                else s.SetNoToggleState();
                changed = true;
            }
            if (!changed && !force) return;
            try
            {
                UIBattleSettingPanel panel = Panel();
                if (panel != null) panel.SetAvailibleText();
            }
            catch (Exception) { }
        }

        private static int UnionCount()
        {
            int n = _mine.Count;
            for (int i = 0; i < _peer.Count; i++)
            {
                if (!Contains(_mine, _peer[i])) n++;
            }
            return n;
        }

        /// <summary>How many slots the panel is showing ticked right now.</summary>
        private static int CountTicked()
        {
            try
            {
                UICharacterSlot[] all = Slots();
                if (all == null) return 0;
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].IsToggleSelected) n++;
                }
                return n;
            }
            catch (Exception) { return 0; }
        }

        // ================= helpers =================

        private static UIBattleSettingPanel Panel()
        {
            try
            {
                UI.UIController ui = UI.UIController.Instance;
                if (ui == null) return null;
                return ui.GetUIPanel(UIPanelType.BattleSetting) as UIBattleSettingPanel;
            }
            catch (Exception) { return null; }
        }

        private static UICharacterSlot[] Slots()
        {
            try
            {
                UIBattleSettingPanel panel = Panel();
                if (panel == null) return null;
                List<UICharacterSlot> list = panel.currentAvailbleUnitslots;
                if (list == null || list.Count == 0) return null;
                return list.ToArray();
            }
            catch (Exception) { return null; }
        }

        private static int IndexOf(UICharacterSlot slot)
        {
            UICharacterSlot[] slots = Slots();
            if (slots == null) return 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == slot) return i;
            }
            return 0;
        }

        /// <summary>
        /// Cross-machine key for one librarian.  nameID is the librarian's identity
        /// in the save file and both machines share that save; a slot with no name
        /// id falls back to its position in the panel, which is equally shared.
        /// </summary>
        private static int KeyOf(UICharacterSlot slot, int idx)
        {
            if (slot == null) return -1;
            try
            {
                UnitBattleDataModel data = slot.unitBattleData;
                if (data != null && data.unitData != null && data.unitData.nameID >= 0)
                {
                    return data.unitData.nameID;
                }
            }
            catch (Exception) { }
            return 100000 + idx;
        }

        private static string Label(UICharacterSlot slot)
        {
            try
            {
                UnitBattleDataModel data = slot.unitBattleData;
                if (data != null && data.unitData != null && !string.IsNullOrEmpty(data.unitData.name))
                {
                    return data.unitData.name;
                }
            }
            catch (Exception) { }
            return "这名司书";
        }

        private static bool Contains(List<int> list, int id)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == id) return true;
            }
            return false;
        }

        private static bool SameSet(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!Contains(b, a[i])) return false;
            }
            return true;
        }

        /// <summary>Same librarian twice on one panel would mean a duplicated save.</summary>
        private static List<int> MergeDuplicates(List<int> list)
        {
            List<int> clean = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (!Contains(clean, list[i])) clean.Add(list[i]);
            }
            return clean;
        }

        private static List<int> Parse(string body)
        {
            List<int> result = new List<int>();
            if (string.IsNullOrEmpty(body) || body == "-") return result;
            string[] parts = body.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                int v;
                if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 0)
                {
                    result.Add(v);
                }
            }
            return result;
        }

        private static string ListText(List<int> list)
        {
            if (list.Count == 0) return "-";
            string s = "";
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) s += ",";
                s += list[i].ToString(CultureInfo.InvariantCulture);
            }
            return s;
        }

        private static void SendNow()
        {
            _lastSendAt = Time.unscaledTime;
            NetLink.Send("LINEUP " + ListText(_mine));
        }

        private static void Notice(string text)
        {
            if (Time.unscaledTime - _lastNoticeAt < NoticeGap) return;
            _lastNoticeAt = Time.unscaledTime;
            Debug.Log("[RuinaNet][lineup] " + text);
            try { ChatWindow.AddLine("系统", text); } catch (Exception) { }
        }
    }
}
