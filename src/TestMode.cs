// RuinaNet - co-op test mode ("slim" second instance).
//
// Testing multiplayer on one machine needs the game running twice. That does
// NOT require a second install: Unity runs two processes happily out of one
// folder, they just must not share Player.log, which is what -logFile is for.
//
// What the second copy wants taken out of the way is the noise - voices, music
// and story scenes - so two windows are not both talking at once and you are
// not clicking through the same prologue twice. That is all this does.
//
// Switched on by the launcher (双开第二个图书馆.bat):
//     LibraryOfRuina.exe -ruinanet-slim -logFile <other log>
// Add -ruinanet-noskip to keep the stories (mute only) - handy for telling
// whether a stuck screen is the story skip or something else entirely.
//
// Compiles with the in-box C# 5 compiler.
//
// ---------------------------------------------------------------------------
// Why the skip is DEFERRED by a frame instead of fired from the patch itself:
//
// StoryRoot.OpenStory does, in this order:
//     1. _onEndStoryFunc = endFunc;
//     2. storyUI.SetActive(true);
//     3. storyManager.Initialize();        <-- a Postfix here fires too early
//     4. storyManager.ShowFastButton(); ShowSkip(); SetJustRead();
//     5. storyEditor.gameObject.SetActive(true); storyEditor.OnOpenStory();
//
// Skipping from step 3 runs EndStory -> _onEndStoryFunc() -> storyUI off and
// the caller's "story finished, go on to the next screen" callback, and THEN
// step 4-5 switch the story UI and editor back ON over the top of it. The
// callback has already fired, so nothing ever drives the screen onwards - the
// second window just sits there and never reaches the reception screen.
//
// Skipping a few frames later (which is exactly when a human pressing the
// on-screen skip button would do it) avoids the re-entrancy completely.
// ---------------------------------------------------------------------------

using System;
using HarmonyLib;
using UnityEngine;

namespace RuinaNet
{
    internal static class TestMode
    {
        /// <summary>Second-instance mode: mute everything and skip story scenes.</summary>
        public static bool Slim;

        /// <summary>Skip story scenes at all. -ruinanet-noskip turns this off.</summary>
        public static bool SkipStory;

        /// <summary>How long to let a freshly opened story settle before skipping.</summary>
        private const float SkipDelay = 0.25f;

        private static bool _loggedQuiet;
        private static bool _skipPending;
        private static float _skipAt;
        private static int _skipped;

        /// <summary>Read the command line once, at mod init.</summary>
        public static void Detect()
        {
            Slim = false;
            bool noSkip = false;
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
                        if (a == "-ruinanet-slim" || a == "--ruinanet-slim")
                        {
                            Slim = true;
                        }
                        else if (a == "-ruinanet-noskip" || a == "--ruinanet-noskip")
                        {
                            noSkip = true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            SkipStory = Slim && !noSkip;
            Debug.Log("[RuinaNet] test mode slim=" + Slim + " skipStory=" + SkipStory);
        }

        /// <summary>Called by the story patch the instant a story opens.</summary>
        public static void NoteStoryOpened()
        {
            if (!SkipStory)
            {
                return;
            }
            _skipPending = true;
            _skipAt = Time.unscaledTime + SkipDelay;
        }

        /// <summary>
        /// Called every frame. Handles both the deferred story skip and the
        /// mute. Muting is re-applied rather than set once because the in-game
        /// options menu writes AudioListener.volume itself, and a settings load
        /// would otherwise undo us.
        /// </summary>
        public static void Tick()
        {
            if (!Slim)
            {
                return;
            }
            try
            {
                if (_skipPending && Time.unscaledTime >= _skipAt)
                {
                    _skipPending = false;
                    SkipNow();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet][slim] skip tick failed: " + e.Message);
            }
            try
            {
                if (AudioListener.volume != 0f)
                {
                    AudioListener.volume = 0f;
                    if (!_loggedQuiet)
                    {
                        _loggedQuiet = true;
                        Debug.Log("[RuinaNet][slim] audio muted");
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void SkipNow()
        {
            if (!SkipStory)
            {
                return;
            }
            StoryScene.StoryRoot root = StoryScene.StoryRoot.Instance;
            if (root == null || root.storyUI == null || !root.storyUI.activeSelf)
            {
                // The scene closed on its own before we got here - fine.
                return;
            }
            StoryScene.StoryManager mgr = StoryScene.StoryManager.Instance;
            if (mgr == null)
            {
                return;
            }
            mgr.SkipAll();
            _skipped++;
            Debug.Log("[RuinaNet][slim] story skipped (#" + _skipped + ")");
        }

        // Harmony discovers the patch class through its [HarmonyPatch]
        // attribute. The patch body is gated on TestMode.Slim, so installing it
        // in a normal instance costs nothing.
        public static void Install()
        {
            try
            {
                Harmony h = new Harmony("yg.ruinanet.slim");
                h.PatchAll(typeof(StorySkipPatch));
                Debug.Log("[RuinaNet][slim] story-skip patch installed");
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet][slim] story-skip patch failed: " + e);
            }
        }
    }

    // A story scene is a self-contained player that already exposes a skip
    // entry point (the on-screen skip button calls it). This patch only marks
    // the story as "wants skipping" - the actual skip happens a few frames
    // later, from TestMode.Tick, once OpenStory has finished wiring the scene
    // up. See the header comment for why firing here would wedge the flow.
    [HarmonyPatch(typeof(StoryScene.StoryManager), "Initialize")]
    internal static class StorySkipPatch
    {
        private static void Postfix(StoryScene.StoryManager __instance)
        {
            if (!TestMode.Slim || __instance == null)
            {
                return;
            }
            TestMode.NoteStoryOpened();
        }
    }
}
