// RuinaNet - screen-flow trace, for working out where a second window stops.
//
// The second instance of the game has no debugger attached and no visible
// output, so when it "gets stuck somewhere before the reception" there is
// nothing to look at. With -ruinanet-diag the mod writes a compact trace of
// the screens it walks through into the log, which makes a stuck window
// obvious: the trace simply stops at the transition that never happened.
//
// Pure polling, no Harmony patches - it cannot break the screen it observes.
// Off by default; the second-instance launcher turns it on.
//
// Compiles with the in-box C# 5 compiler.

using System;
using System.Globalization;
using UnityEngine;

namespace RuinaNet
{
    internal static class Diag
    {
        /// <summary>-ruinanet-diag: write a screen-flow trace into the log.</summary>
        public static bool Enabled;

        /// <summary>-ruinanet-diag-units: also report the live unit count.</summary>
        public static bool Units;

        private static string _lastPhase = "";
        private static bool _lastStory;
        private static bool _seenController;
        private static bool _seenStoryRoot;
        private static int _lastUnits = -1;
        private static int _suppressed;

        public static void Detect()
        {
            Enabled = false;
            Units = false;
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        string a = args[i];
                        if (a == null)
                        {
                            continue;
                        }
                        if (a == "-ruinanet-diag" || a == "--ruinanet-diag")
                        {
                            Enabled = true;
                        }
                        else if (a == "-ruinanet-diag-units" || a == "--ruinanet-diag-units")
                        {
                            Units = true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            if (Enabled)
            {
                Debug.Log("[RuinaNet][diag] trace on (units=" + Units + ")");
            }
        }

        public static void Tick()
        {
            if (!Enabled)
            {
                return;
            }
            try
            {
                PollPhase();
            }
            catch (Exception e)
            {
                Note("phase poll failed: " + e.Message);
            }
            try
            {
                PollStory();
            }
            catch (Exception e)
            {
                Note("story poll failed: " + e.Message);
            }
            if (Units)
            {
                try
                {
                    PollUnits();
                }
                catch (Exception)
                {
                }
            }
        }

        // StageController is the screen state machine: title -> library ->
        // invitation -> reception setup -> battle -> results.
        private static void PollPhase()
        {
            StageController sc = Singleton<StageController>.Instance;
            if (sc == null)
            {
                if (_seenController)
                {
                    _seenController = false;
                    _lastPhase = "";
                    Note("stage controller gone");
                }
                return;
            }
            if (!_seenController)
            {
                _seenController = true;
                Note("stage controller ready");
            }
            string now = sc.Phase.ToString();
            if (now != _lastPhase)
            {
                _lastPhase = now;
                Note("phase -> " + now);
            }
        }

        private static void PollStory()
        {
            StoryScene.StoryRoot root = StoryScene.StoryRoot.Instance;
            bool open = root != null && root.storyUI != null && root.storyUI.activeSelf;
            if (!_seenStoryRoot && root != null)
            {
                _seenStoryRoot = true;
                Note("story root ready");
            }
            if (open != _lastStory)
            {
                _lastStory = open;
                Note(open ? "story OPEN" : "story closed");
            }
        }

        private static void PollUnits()
        {
            int n = BattleSync.AliveUnitCount();
            if (n != _lastUnits)
            {
                _lastUnits = n;
                Note("units = " + n.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Log a line, but collapse bursts. A screen that throws every frame
        /// would otherwise bury everything else in the file.
        /// </summary>
        private static void Note(string msg)
        {
            if (_suppressed > 0)
            {
                Debug.Log("[RuinaNet][diag] (" + _suppressed + " more suppressed)");
                _suppressed = 0;
            }
            Debug.Log("[RuinaNet][diag] " + msg);
        }
    }
}
