using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using LOR_DiceSystem;
using UnityEngine;

namespace RuinaNet
{
    /// <summary>
    /// Buff mirror.
    ///
    /// HP and break were already forced to agree (the VITALS channel), yet damage
    /// still came out different on the two machines, which is what a drifting
    /// buff list looks like: Strength / Endurance / Fragile / Burn stacks change
    /// every number the resolution produces.
    ///
    /// Two rules make this work:
    ///
    ///   * ownership.  Whichever machine owns a librarian is the one that decides
    ///     what buffs are on it -- exactly the rule VITALS already uses (the host
    ///     skips the peer's own librarians there).  So each side publishes the
    ///     buff picture of the units it owns and applies the lines the peer sends
    ///     for the units it does not.  Nobody writes a buff it also simulates, so
    ///     the two machines cannot fight over the same unit.
    ///
    ///   * identity.  A buff is named either by its KeywordBuf (the plain
    ///     Strength / Burn / Fragile family, which BattleUnitBufListDetail can
    ///     build itself through AddKeywordBufThisRoundByEtc / AddKeywordBufByEtc)
    ///     or by its exact class name (everything with bufType == KeywordBuf.None
    ///     -- a large family, and the only handle those have).  The class name
    ///     works because both machines run the same game build, and
    ///     BattleBufUIData itself carries a Type for the same reason.
    ///
    /// Wire format, one message per unit, three lists plus the emotion level:
    ///
    ///   BUFS &lt;round&gt; &lt;wire&gt; &lt;activated&gt; &lt;ready&gt; &lt;readyReady&gt; &lt;emotionLv&gt;
    ///
    /// Each list is "~"-separated entries "k&lt;keyword&gt;:&lt;stack&gt;" or
    /// "t&lt;full.type.name&gt;:&lt;stack&gt;", or "-" when empty.  Writers and
    /// readers tolerate a missing readyReady / emotionLv field.
    /// </summary>
    internal static class BufSync
    {
        private const float Gap = 0.5f;

        // list slots: 0 = activated (this round), 1 = ready (next round),
        // 2 = readyReady (the round after that)
        private const int Activated = 0;
        private const int Ready = 1;
        private const int ReadyReady = 2;

        private static float _wait;
        private static readonly Dictionary<int, string> _sent = new Dictionary<int, string>();
        private static readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
        private static readonly HashSet<string> _warned = new HashSet<string>();
        private static int _applied;

        public static void Reset()
        {
            _wait = 0f;
            _sent.Clear();
        }

        public static void Tick(float dt)
        {
            if (!BattleSync.Active)
            {
                if (_sent.Count > 0) _sent.Clear();
                return;
            }

            // Host publishes through the WHOLE battle: buffs gained during combat
            // resolution (burn ticks, debuffs from enemy attacks) used to wait for
            // the next arranging phase, which is what "the two sides show different
            // buffs" looked like.  _sent dedups by content, so the extra traffic
            // is only the diffs.
            // During planning the client may create an immediate-use effect from
            // its own key page (Olivier's four trigrams are the vanilla example).
            // Publish those owned-unit changes back to the host.  During combat
            // resolution the host remains the only authority.
            if (!BattleSync.IsHost && !BattleSync.InCardPhase()) return;
            _wait += dt;
            if (_wait < Gap) return;
            _wait = 0f;

            try { SendAll(); }
            catch (Exception ex) { Debug.LogWarning("[RuinaNet][buf] send failed: " + ex.Message); }
        }

        // ================= publishing =================

        /// <summary>
        /// The units this machine is allowed to define the buffs of: its own
        /// librarians, plus (on the host) every enemy.  Mirrors the VITALS rule so
        /// the two channels can never contradict each other.
        /// </summary>
        private static List<BattleUnitModel> Publishable()
        {
            List<BattleUnitModel> all = Alive();
            List<BattleUnitModel> result = new List<BattleUnitModel>();

            bool host = BattleSync.IsHost;
            List<BattleUnitModel> players = null;
            try { players = BattleObjectManager.instance.GetAliveList(Faction.Player); }
            catch (Exception) { }

            for (int i = 0; i < all.Count; i++)
            {
                BattleUnitModel u = all[i];
                if (u == null || u.bufListDetail == null) continue;

                bool mine = BattleSync.IsMine(u);
                if (host)
                {
                    // the peer drives the librarians it picked -- never push those back
                    bool isPlayer = players != null && players.Contains(u);
                    if (isPlayer && !mine) continue;
                }
                else
                {
                    // the host is the brain for everything we do not own
                    if (!mine) continue;
                }
                result.Add(u);
            }
            return result;
        }

        public static void SendSnapshot()
        {
            if (!BattleSync.IsHost || !BattleSync.Active) return;
            _sent.Clear();
            SendAll();
        }

        public static void SendOwnedSnapshot()
        {
            if (!BattleSync.Active) return;
            _sent.Clear();
            SendAll();
        }

        private static void SendAll()
        {
            List<BattleUnitModel> all = Alive();
            for (int i = 0; i < all.Count; i++)
            {
                BattleUnitModel u = all[i];
                if (u == null) continue;

                int wire = NetState.WireIndex(u);
                if (wire < 0) continue;

                string active = Csv(u.bufListDetail.GetActivatedBufList());
                string ready = Csv(u.bufListDetail.GetReadyBufList());
                string readyReady = Csv(u.bufListDetail.GetReadyReadyBufList());
                int emotion = EmotionOf(u);
                string line = active + "|" + ready + "|" + readyReady + "|" + emotion;

                string was;
                if (_sent.TryGetValue(wire, out was) && was == line) continue;
                _sent[wire] = line;

                NetLink.Send("BUFS " + BattleSync.Round + " " + wire + " " +
                             active + " " + ready + " " + readyReady + " " + emotion);
            }
        }

        // ================= receiving =================

        public static bool HandleMessage(string[] p)
        {
            if (p.Length < 4 || p[0] != "BUFS") return false;

            int round;
            // Received at any point in the round, not only while arranging -- the
            // host's buff picture is authoritative mid-resolution too.
            if (!BattleSync.Active) return true;
            if (!int.TryParse(p[1], out round)) return true;
            if (round != BattleSync.Round &&
                !(BattleSync.InCardPhase() && Math.Abs(round - BattleSync.Round) <= 1)) return true;
            int wire;
            if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out wire)) return true;

            string active = p[3];
            string ready = p.Length > 4 ? p[4] : "-";
            string readyReady = p.Length > 5 ? p[5] : "-";
            int emotion = -1;
            if (p.Length > 6)
            {
                int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out emotion);
            }

            try { Apply(wire, active, ready, readyReady, emotion); }
            catch (Exception ex) { Debug.LogWarning("[RuinaNet][buf] apply failed: " + ex.Message); }
            return true;
        }

        private static void Apply(int wire, string activeCsv, string readyCsv, string readyReadyCsv, int emotion)
        {
            BattleUnitModel u = NetState.Find(wire);
            if (u == null || u.bufListDetail == null) return;

            // Host accepts only the client's explicitly owned librarians.  Client
            // accepts the host's units and enemies, never a reflection of its own
            // local choice.
            if (BattleSync.IsHost)
            {
                if (u.faction == Faction.Enemy || BattleSync.IsMine(u)) return;
            }
            else if (u.faction == Faction.Player && BattleSync.IsMine(u)) return;

            ApplyList(u, u.bufListDetail, activeCsv, Activated);
            ApplyList(u, u.bufListDetail, readyCsv, Ready);
            ApplyList(u, u.bufListDetail, readyReadyCsv, ReadyReady);
            ApplyEmotion(u, emotion);

            // The row under a unit is drawn once, when it is built, and only redrawn
            // when the game says a stat changed.  Writing the buff tables is
            // therefore invisible on its own -- without this the icons on screen
            // keep showing the old picture even though the stacks are right.
            BattleSync.RefreshUnitUi(u);

            _applied++;
            if (_applied <= 3)
            {
                Debug.Log("[RuinaNet][buf] adopted the " + (BattleSync.IsHost ? "peer" : "host") +
                          "'s buffs for unit " + wire + " (active=" + activeCsv +
                          ", ready=" + readyCsv + ", rr=" + readyReadyCsv + ", emo=" + emotion + ")");
            }
        }

        private static void ApplyEmotion(BattleUnitModel u, int emotion)
        {
            if (emotion < 0 || emotion > 5) return;
            if (u.emotionDetail == null) return;
            if (u.emotionDetail.EmotionLevel == emotion) return;
            u.emotionDetail.SetEmotionLevel(emotion);
        }

        /// <summary>
        /// Make one of the three lists on the unit look like the peer's copy: add
        /// what is missing, restack what drifted, drop what the peer does not have.
        /// </summary>
        private static void ApplyList(BattleUnitModel u, BattleUnitBufListDetail d, string csv, int slot)
        {
            List<BattleUnitBuf> list = ListOf(d, slot);
            if (list == null) return;

            Dictionary<string, int> want = Parse(csv);

            foreach (KeyValuePair<string, int> kv in want)
            {
                BattleUnitBuf b = Find(list, kv.Key);
                if (b == null)
                {
                    if (kv.Value <= 0) continue;
                    b = Create(u, d, kv.Key, kv.Value, slot);
                    if (b == null) continue;
                }
                if (b.stack != kv.Value) b.stack = kv.Value;
            }

            // The peer's copy is the truth: anything it does not list is gone.
            // Destroy() only marks the buff, so it has to leave the table too --
            // otherwise the icon row keeps drawing a corpse.
            BattleUnitBuf[] have = list.ToArray();
            for (int i = 0; i < have.Length; i++)
            {
                BattleUnitBuf b = have[i];
                if (b == null) { list.Remove(b); continue; }
                if (b.IsDestroyed()) { list.Remove(b); continue; }
                if (want.ContainsKey(KeyOf(b))) continue;
                try { b.Destroy(); } catch (Exception) { }
                list.Remove(b);
            }
        }

        private static BattleUnitBuf Create(BattleUnitModel u, BattleUnitBufListDetail d, string key, int stack, int slot)
        {
            if (string.IsNullOrEmpty(key)) return null;

            if (key[0] == 'k')
            {
                int kw;
                if (!int.TryParse(key.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out kw)) return null;

                // Let the game build it: AddNewKeywordBufInList owns the
                // KeywordBuf -> subclass table, so we never enumerate buff types
                // ourselves.  Note it *accumulates* the stack, which is fine -- the
                // caller restacks the result to the peer's number right after.
                try
                {
                    if (slot == Activated) d.AddKeywordBufThisRoundByEtc((KeywordBuf)kw, stack, null);
                    else if (slot == Ready) d.AddKeywordBufByEtc((KeywordBuf)kw, stack, null);
                    else return null;    // ready-ready has no keyword factory
                }
                catch (Exception ex)
                {
                    Warn("kw " + kw, "keyword buf " + kw + " could not be built: " + ex.Message);
                    return null;
                }
                BattleUnitBuf made = Find(ListOf(d, slot), key);
                if (made != null) made.stack = stack;
                return made;
            }

            if (key[0] != 't') return null;

            string name = key.Substring(1);
            Type t = Resolve(name);
            if (t == null)
            {
                Warn(name, "no local class matches " + name + " -- leaving that buff out");
                return null;
            }

            BattleUnitBuf buf = null;
            try { buf = (BattleUnitBuf)Activator.CreateInstance(t); }
            catch (Exception ex)
            {
                Warn(name, name + " could not be instantiated: " + ex.Message);
                return null;
            }
            if (buf == null) return null;

            try { buf.stack = stack; } catch (Exception) { }

            // AddBuf runs the immunity / passive checks.  They can veto a buff the
            // peer legitimately has (its resistances are not identical to ours
            // yet), and the peer is the authority for this unit -- so a veto falls
            // back to putting it in the table directly.
            bool added = false;
            try
            {
                if (slot == Activated) d.AddBuf(buf);
                else if (slot == Ready) d.AddReadyBuf(buf);
                else d.AddReadyReadyBuf(buf);
                added = ListOf(d, slot).Contains(buf);
            }
            catch (Exception ex)
            {
                Warn(name, name + " could not be added: " + ex.Message);
                added = false;
            }

            if (!added)
            {
                try
                {
                    buf.Init(u);
                    ListOf(d, slot).Add(buf);
                    added = true;
                }
                catch (Exception ex)
                {
                    Warn(name, name + " could not be forced in: " + ex.Message);
                    added = false;
                }
            }
            if (added && buf.stack != stack) buf.stack = stack;
            return added ? buf : null;
        }

        private static Type Resolve(string name)
        {
            Type t;
            if (_types.TryGetValue(name, out t)) return t;

            t = null;
            try
            {
                t = Type.GetType(name);
                if (t == null) t = typeof(BattleUnitBuf).Assembly.GetType(name);
                if (t == null)
                {
                    Assembly[] asm = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < asm.Length && t == null; i++)
                    {
                        try { t = asm[i].GetType(name); }
                        catch (Exception) { }
                    }
                }
                if (t != null && (t.IsAbstract || !t.IsSubclassOf(typeof(BattleUnitBuf))))
                {
                    Warn(name, name + " is not a usable BattleUnitBuf subclass");
                    t = null;
                }
            }
            catch (Exception) { t = null; }

            _types[name] = t;      // a miss is cached too: the assemblies are all loaded already
            return t;
        }

        private static void Warn(string key, string message)
        {
            if (!_warned.Add(key)) return;
            Debug.LogWarning("[RuinaNet][buf] " + message);
        }

        // ================= serialisation =================

        private static Dictionary<string, int> Parse(string csv)
        {
            Dictionary<string, int> map = new Dictionary<string, int>();
            if (string.IsNullOrEmpty(csv) || csv == "-") return map;

            string[] parts = csv.Split('~');
            for (int i = 0; i < parts.Length; i++)
            {
                string item = parts[i];
                if (item.Length == 0) continue;

                int c = item.LastIndexOf(':');
                if (c <= 0) { map[item] = 1; continue; }

                string key = item.Substring(0, c);
                int stack;
                if (!int.TryParse(item.Substring(c + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out stack))
                {
                    stack = 1;
                }
                map[key] = stack;
            }
            return map;
        }

        private static string Csv(List<BattleUnitBuf> list)
        {
            if (list == null || list.Count == 0) return "-";

            StringBuilder sb = new StringBuilder(64);
            for (int i = 0; i < list.Count; i++)
            {
                BattleUnitBuf b = list[i];
                if (b == null || b.IsDestroyed()) continue;

                if (sb.Length > 0) sb.Append('~');
                sb.Append(KeyOf(b));
                sb.Append(':');
                sb.Append(b.stack.ToString(CultureInfo.InvariantCulture));
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        /// <summary>The cross-machine name of a buff: keyword if it has one, class name otherwise.</summary>
        private static string KeyOf(BattleUnitBuf b)
        {
            if (b == null) return "";
            if (b.bufType != KeywordBuf.None)
            {
                return "k" + ((int)b.bufType).ToString(CultureInfo.InvariantCulture);
            }
            Type t = b.GetType();
            return "t" + (t.FullName != null ? t.FullName : t.Name);
        }

        private static BattleUnitBuf Find(List<BattleUnitBuf> list, string key)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                BattleUnitBuf b = list[i];
                if (b == null || b.IsDestroyed()) continue;
                if (KeyOf(b) == key) return b;
            }
            return null;
        }

        private static List<BattleUnitBuf> ListOf(BattleUnitBufListDetail d, int slot)
        {
            if (d == null) return null;
            if (slot == Activated) return d.GetActivatedBufList();
            if (slot == Ready) return d.GetReadyBufList();
            return d.GetReadyReadyBufList();
        }

        private static int EmotionOf(BattleUnitModel u)
        {
            try
            {
                if (u == null || u.emotionDetail == null) return -1;
                return u.emotionDetail.EmotionLevel;
            }
            catch (Exception) { return -1; }
        }

        private static List<BattleUnitModel> Alive()
        {
            try
            {
                List<BattleUnitModel> all = BattleObjectManager.instance.GetAliveList(true);
                if (all != null) return all;
            }
            catch (Exception) { }
            return new List<BattleUnitModel>();
        }
    }
}
