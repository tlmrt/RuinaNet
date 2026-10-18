// RuinaNet - Library of Ruina co-op multiplayer prototype
// Entry point: co-op panel on the invitation screen. UDP and Steam relay
// share one session protocol. See SteamLink for lobby lifecycle handling.
//
// Compiles with the in-box C# 5 compiler (no .NET SDK on this machine), so:
// no string interpolation, no ?. / ?[], no nameof, no expression-bodied members.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using UI;
using UI.Title;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RuinaNet
{
    // ------------------------------------------------------------------
    // Mod entry point
    // ------------------------------------------------------------------
    public class RuinaNetInitializer : ModInitializer
    {
        public const string Version = "1.0";
        public static bool Patched;

        public override void OnInitializeMod()
        {
            // The co-op entry button is not a Harmony patch: CoopEntry polls
            // the UI phase from the resident RuinaNetPanelTick instead. See
            // CoopEntry.cs for why.
            Patched = true;
            Debug.Log("[RuinaNet] v" + Version + " patches installed");

            // Stage 5: a second instance launched on the same machine is told
            // to run quiet (muted, story scenes skipped) through the command
            // line. Read before anything else so the flag is live for the
            // patches that care about it.
            TestMode.Detect();
            Diag.Detect();

            // stage 2: battle synchronisation patches (inert until a link is up)
            BattleSyncPatches.Install();

            // stage 5: the quiet-mode story skip
            TestMode.Install();

            // one pipe for everything the peer sends; battle commands are claimed
            // first, anything else is logged for now.
            NetLink.OnPeerMessage = HandlePeerMessage;

            var go = new GameObject("RuinaNet_Driver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<RuinaNetDriver>();
            Debug.Log("[RuinaNet] v" + Version + " loaded");
        }

        private static void HandlePeerMessage(string payload)
        {
            try
            {
                if (BattleSync.HandleMessage(payload)) return;

                // CHAT rides inside the same MSG envelope; everything after the
                // command word is the body verbatim, spaces included.
                if (payload.StartsWith("CHAT "))
                {
                    ChatWindow.AddLine(ChatWindow.PeerName(), payload.Substring(5));
                    return;
                }

                Debug.Log("[RuinaNet] peer: " + payload);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RuinaNet] peer message failed: " + e.Message);
            }
        }
    }

    // ------------------------------------------------------------------
    // Main-thread pump. Steam callbacks arrive on a background thread;
    // anything that touches Unity must be marshalled through here.
    // ------------------------------------------------------------------
    public class RuinaNetDriver : MonoBehaviour
    {
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public static void Run(Action action)
        {
            if (action != null)
            {
                _queue.Enqueue(action);
            }
        }

        private bool _linkApplied;
        private bool _linkHost;
        private void Update()
        {
            try { SteamLink.Tick(); }
            catch (Exception e) { Debug.LogWarning("[RuinaNet] Steam tick failed: " + e.Message); }
            NetLink.Tick();
            bool linked = NetLink.Running && NetLink.Linked;
            if (linked != _linkApplied || (linked && _linkHost != NetLink.IsHost))
            {
                _linkApplied = linked;
                _linkHost = NetLink.IsHost;
                BattleSync.SetLink(linked, NetLink.IsHost);
            }
            // Cross-mod repair, once, on the first tick.  It has to wait for a
            // tick and not run from OnInitializeMod: every mod initialises in
            // one synchronous pass, so only after that pass is it safe to say
            // whether a shared-folder race killed one of them half way.
            try
            {
                Compat.Tick();
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] compat tick failed: " + e);
            }

            Action action;
            while (_queue.TryDequeue(out action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogError("[RuinaNet] queued action threw: " + e);
                }
            }

            // The co-op entry button and the chat window MUST be driven from
            // this always-resident driver. Driving them from RuinaNetPanelTick
            // was a deadlock: that component is added to the canvas that the
            // entry button itself creates, so before the button was ever
            // clicked there was no tick at all - the button could never appear.
            try
            {
                CoopEntry.Tick();
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] coop entry tick failed: " + e);
            }

            try
            {
                ChatWindow.Tick(Time.unscaledDeltaTime);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] chat tick failed: " + e);
            }

            // Peer cursor + ownership labels, drawn only while a co-op battle
            // is actually on screen.
            try
            {
                BattleOverlay.Tick(Time.unscaledDeltaTime);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] overlay tick failed: " + e);
            }

            // Quiet mode keeps re-asserting the mute because the in-game
            // options menu writes AudioListener.volume behind our back.
            try
            {
                TestMode.Tick();
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] slim tick failed: " + e);
            }

            // Round gate driver: decides when a blocked card phase may advance.
            try { CreatureSync.Tick(Time.unscaledDeltaTime); }
            catch (Exception e) { Debug.LogError("[RuinaNet] creature tick failed: " + e); }
            try
            {
                BattleSync.Tick(Time.unscaledDeltaTime);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] battle tick failed: " + e);
            }

            // Optional screen-flow trace (-ruinanet-diag), for finding out
            // where a second window stops.
            try
            {
                Diag.Tick();
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] diag tick failed: " + e);
            }

            // Reception-join prompt: kept alive only while the link is.
            try
            {
                StageSync.Tick(Time.unscaledDeltaTime);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] stage tick failed: " + e);
            }

            // Co-op room: split heartbeat + toolbar refresh.
            try
            {
                Room.Tick(Time.unscaledDeltaTime);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] room tick failed: " + e);
            }

            // Who drives whom, painted onto the 开启舞台 portraits.
            try
            {
                RosterMarker.Tick(Time.unscaledDeltaTime);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] roster tick failed: " + e);
            }
        }
    }

    // ------------------------------------------------------------------
    // DEPRECATED - the co-op entry moved off the title menu and onto the
    // pre-battle team-setting panel; see CoopEntry.cs. PatchAll is no longer
    // applied to this class, so MenuInjector below is dead code kept only as
    // a reference for the clone-a-vanilla-button technique.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(UITitleController), "Initialize")]
    internal static class TitleInitializePatch
    {
        private static void Postfix(UITitleController __instance)
        {
            try
            {
                MenuInjector.Inject(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuinaNet] menu inject failed: " + e);
            }
        }
    }

    // ------------------------------------------------------------------
    // Side-car injection: clone a vanilla button (keeps the native look,
    // hover animation and HSV handling), strip the vanilla click component
    // off the clone, and attach our own handler. The vanilla arrays and the
    // TitleActionType enum are left untouched on purpose - the game clamps
    // its keyboard navigation to index 3 and switches on exactly five enum
    // values, so appending to either would break its state machine.
    // ------------------------------------------------------------------
    internal static class MenuInjector
    {
        private const string ButtonName = "RuinaNet_CreateRoom";

        public static void Inject(UITitleController controller)
        {
            if (controller == null)
            {
                return;
            }

            // Initialize() runs every time the title screen opens.
            if (GameObject.Find(ButtonName) != null)
            {
                return;
            }

            Transform template = PickTemplate(controller);
            if (template == null)
            {
                Debug.LogWarning("[RuinaNet] no title button found to clone");
                return;
            }

            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.parent);
            clone.name = ButtonName;
            clone.SetActive(true);

            // Drop the vanilla behaviour so its UnityEvent bindings go with it.
            StripVanillaComponents(clone);

            // Place it after the last button of the group.
            Transform last = template;
            for (int i = 0; i < template.parent.childCount; i++)
            {
                Transform child = template.parent.GetChild(i);
                if (child.name != ButtonName && IsTitleButton(child))
                {
                    last = child;
                }
            }
            clone.transform.SetAsLastSibling();
            clone.transform.SetSiblingIndex(Mathf.Max(last.GetSiblingIndex() + 1, template.parent.childCount - 1));
            OffsetAfterSiblings(clone, last);

            clone.AddComponent<CoopMenuButton>();

            Debug.Log("[RuinaNet] title button injected (" +
                      (controller.IsRuinTitle ? "ruin" : "library") + " title)");
        }

        private static Transform PickTemplate(UITitleController controller)
        {
            if (controller.IsRuinTitle)
            {
                if (controller.Ruin_TitleButtons != null && controller.Ruin_TitleButtons.Length > 0)
                {
                    return controller.Ruin_TitleButtons[controller.Ruin_TitleButtons.Length - 1].transform;
                }
                if (controller.TitleButtons != null && controller.TitleButtons.Length > 0)
                {
                    return controller.TitleButtons[controller.TitleButtons.Length - 1].transform;
                }
            }
            else
            {
                if (controller.TitleButtons != null && controller.TitleButtons.Length > 0)
                {
                    return controller.TitleButtons[controller.TitleButtons.Length - 1].transform;
                }
                if (controller.Ruin_TitleButtons != null && controller.Ruin_TitleButtons.Length > 0)
                {
                    return controller.Ruin_TitleButtons[controller.Ruin_TitleButtons.Length - 1].transform;
                }
            }
            return null;
        }

        private static bool IsTitleButton(Transform t)
        {
            return t.GetComponent<UITitleButtonSlot>() != null
                || t.GetComponent<UITitleButtonSlot_Ruin>() != null;
        }

        private static void StripVanillaComponents(GameObject clone)
        {
            UITitleButtonSlot slot = clone.GetComponent<UITitleButtonSlot>();
            if (slot != null)
            {
                UnityEngine.Object.DestroyImmediate(slot);
            }

            UITitleButtonSlot_Ruin slotRuin = clone.GetComponent<UITitleButtonSlot_Ruin>();
            if (slotRuin != null)
            {
                UnityEngine.Object.DestroyImmediate(slotRuin);
            }

            // The clone inherits the vanilla Button too; keep it for the
            // press/hover visuals but clear its listener list.
            Button button = clone.GetComponent<Button>();
            if (button != null)
            {
                button.onClick = new Button.ButtonClickedEvent();
            }
        }

        // Vanilla title buttons are absolutely positioned (no layout group),
        // so a clone would land exactly on top of its template. Push it one
        // grid step past the last button, using the gap between the last two
        // as the step. If a layout group drives the parent, the sibling index
        // is what matters and this offset is harmlessly overridden.
        private static void OffsetAfterSiblings(GameObject clone, Transform last)
        {
            RectTransform cloneRect = clone.GetComponent<RectTransform>();
            RectTransform lastRect = last as RectTransform;
            if (cloneRect == null || lastRect == null)
            {
                return;
            }

            Vector2 step = new Vector2(0f, -60f);
            if (last.parent != null && last.GetSiblingIndex() > 0)
            {
                RectTransform prevRect = last.parent.GetChild(last.GetSiblingIndex() - 1) as RectTransform;
                if (prevRect != null)
                {
                    Vector2 delta = lastRect.anchoredPosition - prevRect.anchoredPosition;
                    if (delta.sqrMagnitude > 0.01f)
                    {
                        step = delta;
                    }
                }
            }

            cloneRect.anchorMin = lastRect.anchorMin;
            cloneRect.anchorMax = lastRect.anchorMax;
            cloneRect.pivot = lastRect.pivot;
            cloneRect.sizeDelta = lastRect.sizeDelta;
            cloneRect.anchoredPosition = lastRect.anchoredPosition + step;
        }
    }

    public class CoopMenuButton : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        private float _scale = 1f;

        public void OnPointerClick(PointerEventData eventData)
        {
            UISoundManager.instance.PlayEffectSound(UISoundType.Card_Apply);
            NetPanel.Toggle();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _scale = 1.08f;
            transform.localScale = new Vector3(_scale, _scale, 1f);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _scale = 1f;
            transform.localScale = new Vector3(1f, 1f, 1f);
        }
    }
}
