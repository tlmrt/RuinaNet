using System;
using System.Collections.Generic;
using UnityEngine;

namespace RuinaNet
{
    /// <summary>
    /// Deterministic dice.
    ///
    /// Shipping the host's pips over the wire does not work: both machines roll
    /// at the same instant, so the host's value always arrives one round trip
    /// AFTER the client has already used its own number ("no host value for
    /// X:Y:Z -- kept local roll").  The battle therefore drifted apart on the
    /// very first clash.
    ///
    /// So instead of telling the peer the answer, we make both machines compute
    /// the same answer locally.  Every pip is derived from a shared seed, the
    /// round number and the ordinal of the roll inside that round -- both sides
    /// run the same rolls in the same order, so both get the same numbers with
    /// zero latency.  Nothing about the game's own logic is changed: the value
    /// still flows through the normal passive/emotion/buff correction chain,
    /// which is itself identical once state, cards and speed dice are synced.
    ///
    /// The host's DICE broadcast stays in place as a correction in case the two
    /// roll sequences ever drift apart.
    /// </summary>
    internal static class Dice
    {
        /// <summary>Ordinal of the roll inside the current round.</summary>
        public static int Seq;
        private static uint _rollIdentity;
        private static readonly Dictionary<string,int> Counts = new Dictionary<string,int>();

        public static void ResetSeed() { _seed = int.MinValue; ResetSeq(); }

        public static void ResetSeq()
        {
            Seq = 0;
            Counts.Clear();
            _rollIdentity = 0;
        }

        /// <summary>Called from BattleDiceBehavior.RollDice's prefix, before the value is made.</summary>
        public static void Advance()
        {
            Seq++;
            string key = BattleSync.CurrentDiceKeyOf(BattleSync.GetCurrentRoll()) ?? "unknown";
            int count;
            Counts.TryGetValue(key, out count);
            Counts[key] = ++count;
            uint identity = 2166136261u;
            foreach (char c in key) identity = unchecked((identity ^ c) * 16777619u);
            _rollIdentity = Mix(identity ^ ((uint)count * 3266489917u));
        }

        public static int Pips(int diceMin, int diceFace)
        {
            int span = diceFace - diceMin + 1;
            if (span <= 1) return diceMin;

            uint h = Mix((uint)Seed ^ ((uint)BattleSync.Round * 2654435761u) ^ _rollIdentity);
            int value = diceMin + (int)(h % (uint)span);
            if (Seq <= 2)
            {
                Debug.Log("[RuinaNet][dice] round " + BattleSync.Round + " seq " + Seq +
                          " -> " + value + " (of " + diceMin + ".." + diceFace + ")");
            }
            return value;
        }

        private static int _seed = int.MinValue;

        /// <summary>
        /// Shared seed.  Both machines pick the SAME name out of the pair, so
        /// they agree without spending a packet on it, and the rolls differ from
        /// match to match.
        /// </summary>
        private static int Seed
        {
            get
            {
                if (_seed != int.MinValue) return _seed;
                try
                {
                    string a = NetLink.LocalName;
                    string b = NetLink.PeerName;
                    if (a == null) a = "";
                    if (b == null) b = "";
                    string use = string.CompareOrdinal(a, b) <= 0 ? a : b;
                    int h = 17;
                    for (int i = 0; i < use.Length; i++) h = h * 31 + use[i];
                    _seed = h;
                }
                catch (Exception)
                {
                    _seed = 0;
                }
                return _seed;
            }
        }

        private static uint Mix(uint a)
        {
            a ^= a >> 16;
            a *= 0x7feb352du;
            a ^= a >> 15;
            a *= 0x846ca68bu;
            a ^= a >> 16;
            return a;
        }
    }
}
