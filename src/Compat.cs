// RuinaNet - third-party mod compatibility shims.
//
// Two instances of the game share ONE Steam workshop folder, and some workshop
// mods stage temporary files next to their own assets.  "AbnormalityRemake",
// for one, converts its mp3s by writing "<mp3>.wav" into that shared BGM
// folder and deleting it again.  When both instances initialise at nearly the
// same time the second writer gets "IOException: Sharing violation on path
// ...\BGM\SecondAlarm.mp3.wav", and its OnInitializeMod() aborts in the
// middle: BGM stays null and the ten InitRemakeEmotionCardAndEgo() calls that
// follow it never run at all.
//
// The leftovers then break the battle.  That mod's own postfix on
// BattleUnitModel.IsTargetable() reads GetRemakeEmotionCardAndEgo[Hokma] with
// the indexer, so against an empty dictionary every call throws
// KeyNotFoundException.  IsTargetable is called from
// BookModel.GetSpeedDiceRule(), which BattleUnitModel.OnRoundStartOnlyUI()
// calls while RoundStartPhase_UI builds the speed dice row - so the second
// instance never gets any dice ("客机又没骰子了").
//
// The mod cannot be recompiled, so its half-finished initialisation is put
// back together from the outside, by reflection, and only when it is actually
// installed.  Everything here is best-effort: a failure is logged and
// swallowed, never thrown.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RuinaNet
{
    internal static class Compat
    {
        private const string RemakeTypeName = "AbnormalityRemake.RemakeInitializer";

        /// <summary>The three clips RemakeInitializer.OnInitializeMod builds, in its own order.</summary>
        private static readonly string[] RemakeBgm = { "SecondAlarm", "ThirdAlarm", "Iron Lotus" };

        private static bool _ticked;
        private static bool _healed;
        private static int _foreignThrows;

        /// <summary>
        /// One-shot repair, driven from the resident driver's Update().  It must
        /// not run any earlier than that: every mod's OnInitializeMod() is
        /// called in one synchronous pass before the game returns to its update
        /// loop, so by the first tick we know whether the other mod finished or
        /// died.  Repairing while it was still running would only make its own
        /// dictionary Add() throw and kill an initialisation that was fine.
        /// </summary>
        public static void Tick()
        {
            if (_ticked) return;
            _ticked = true;
            RepairAfterLoad();
        }

        /// <summary>Replay what the interrupted initialisation never got to do.  Idempotent.</summary>
        public static void RepairAfterLoad()
        {
            try
            {
                Type t = FindType(RemakeTypeName);
                if (t == null) return;                      // the mod is not installed

                int clips = RestoreBgm(t);
                int blocks = ReplaySephirahs(t);
                if (clips > 0 || blocks > 0)
                {
                    Debug.Log("[RuinaNet][compat] AbnormalityRemake was cut short while loading"
                              + " (two instances share its workshop folder) -- restored "
                              + clips + " bgm clip(s) and " + blocks + " sephirah block(s)");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][compat] repair failed: " + e.Message);
            }
        }

        /// <summary>
        /// Called from the IsTargetable finalizer when another mod's patch blew
        /// up.  Heals the cause once, then only writes it down.
        /// </summary>
        public static void NoteForeignThrow(Exception e)
        {
            if (e == null) return;
            if (!_healed)
            {
                _healed = true;
                RepairAfterLoad();
            }
            _foreignThrows++;
            if (_foreignThrows <= 5)
            {
                Debug.LogWarning("[RuinaNet][compat] another mod's patch threw ("
                                 + e.GetType().Name + ": " + e.Message
                                 + ") -- swallowed so the round can still start");
            }
        }

        // ---------------- AbnormalityRemake ----------------

        /// <summary>
        /// Rebuild the BGM dictionary the mod never finished.  The mp3s are
        /// copied into a per-process temp folder first, because the mod's own
        /// converter writes the .wav next to whatever mp3 path it is handed -
        /// handing it the shared workshop path again would just race again.
        /// </summary>
        private static int RestoreBgm(Type t)
        {
            FieldInfo f = t.GetField("BGM", BindingFlags.Public | BindingFlags.Static);
            FieldInfo pf = t.GetField("path", BindingFlags.Public | BindingFlags.Static);
            MethodInfo conv = t.GetMethod("Mp3toAudioClip",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null || pf == null || conv == null) return 0;

            Dictionary<string, AudioClip> map = f.GetValue(null) as Dictionary<string, AudioClip>;
            string dir = pf.GetValue(null) as string;
            if (string.IsNullOrEmpty(dir)) return 0;
            if (map == null)
            {
                map = new Dictionary<string, AudioClip>();
                f.SetValue(null, map);
            }

            string tmp = ScratchDir();
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            Purge(tmp, pid, false);             // leftovers from instances that are gone
            Directory.CreateDirectory(tmp);

            int added = 0;
            for (int i = 0; i < RemakeBgm.Length; i++)
            {
                string name = RemakeBgm[i];
                if (map.ContainsKey(name)) continue;

                string src = Path.Combine(Path.Combine(dir, "BGM"), name + ".mp3");
                if (!File.Exists(src)) continue;

                string copy = Path.Combine(tmp, name + "_" + pid + ".mp3");
                File.Copy(src, copy, true);
                AudioClip clip = conv.Invoke(null, new object[] { copy }) as AudioClip;
                if (clip != null)
                {
                    map[name] = clip;
                    added++;
                }
            }

            Purge(tmp, pid, true);
            string[] left;
            try { left = Directory.GetFiles(tmp); }
            catch (Exception) { left = new string[0]; }
            if (left.Length > 0)
            {
                // The mod's converter never disposes its mp3 reader, so the copy
                // is still held open.  One collection is enough to release it.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Purge(tmp, pid, true);
            }
            return added;
        }

        /// <summary>Scratch folder for the mp3 copies, shared by every instance here.</summary>
        private static string ScratchDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "RuinaNet_bgm");
            try { Directory.CreateDirectory(d); }
            catch (Exception) { }
            return d;
        }

        /// <summary>
        /// Delete scratch copies.  With all = false only what some earlier
        /// instance left behind is touched, and only once it is old enough: a
        /// second instance repairing at the very same moment still holds its own
        /// copies open, and deleting those would break its repair half way.  A
        /// delete that fails is not an error - the next launch purges whatever a
        /// dead instance left.
        /// </summary>
        private static void Purge(string dir, int pid, bool all)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception) { return; }

            DateTime cutoff = DateTime.UtcNow.AddMinutes(-10);
            int kept = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string n = Path.GetFileName(files[i]);
                if (!all && pid > 0 && n.IndexOf("_" + pid + ".") >= 0)
                {
                    kept++;
                    continue;
                }
                if (!all)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(files[i]) > cutoff)
                        {
                            kept++;
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                try { File.Delete(files[i]); }
                catch (Exception) { kept++; }
            }

            if (kept == 0)
            {
                try { Directory.Delete(dir, false); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Replay the sephirah pass the mod never reached.  Replaying beats
        /// filling the dictionary by hand: that method also collects the vanilla
        /// emotion card and EGO card tables the mod needs later on, and rewrites
        /// the card info it replaces.  Only the sephirahs it never did are
        /// touched, so a healthy instance is left completely alone.
        /// </summary>
        private static int ReplaySephirahs(Type t)
        {
            FieldInfo f = t.GetField("GetRemakeEmotionCardAndEgo",
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo init = t.GetMethod("InitRemakeEmotionCardAndEgo",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null || init == null) return 0;

            IDictionary map = f.GetValue(null) as IDictionary;
            if (map == null) return 0;

            int done = 0;
            for (int i = 1; i <= 10; i++)       // (SephirahType)1 .. (SephirahType)10
            {
                object key = Enum.ToObject(typeof(SephirahType), i);
                if (map.Contains(key)) continue;    // the mod already did this one
                try
                {
                    init.Invoke(null, new object[] { key });
                    done++;
                }
                catch (Exception e)
                {
                    // Its own Add() refused, or a data table was missing: leave
                    // the dictionary in the state the mod would have left it.
                    Debug.LogWarning("[RuinaNet][compat] could not replay sephirah " + i + ": " + e.Message);
                    try { map[key] = false; }
                    catch (Exception) { }
                }
            }
            return done;
        }

        // ---------------- plumbing ----------------

        private static Type FindType(string fullName)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    Type t = asms[i].GetType(fullName, false);
                    if (t != null) return t;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }
    }
}
