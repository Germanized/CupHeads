using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;
using CupheadOnline.Net;
using CupheadOnline.UI;
using CupheadOnline.Sync;
using CupheadOnline.Diagnostics;

namespace CupheadOnline.Patches
{
    // ────────────────────────────────────────────────────────────────────────────
    //  ARCHITECTURE
    //
    //  Main menu arrays are extended by TWO items:
    //    sentinel 99  → MULTIPLAYER (opens the lobby card)
    //    sentinel 100 → CREDITS     (opens the Credits panel)
    //
    //  The lobby (UI/LobbyView) is a printed card like the game's options menu:
    //  two player seats, a status line, a menu and a hint line. The menu only
    //  ever lists what can be done right now — rows are rebuilt from the lobby
    //  phase (idle, hosting, joining, connected as host / guest) every frame —
    //  instead of a fixed list whose labels changed meaning with the state.
    //
    //  Prefix skips original UpdateMainMenu while InMpMenu or InCredits.
    // ────────────────────────────────────────────────────────────────────────────

    // ── MP state ─────────────────────────────────────────────────────────────────

    internal static class MpMenuState
    {
        internal static bool             InMpMenu;
        internal static int              SavedMainSel;
        internal static int              MainMenuMpIndex;
        internal static int              MainMenuCreditsIndex;
        internal static bool             InputLocked;        // blocks Accept during critical states

        internal static GameObject       MainContainer;
        internal static GameObject       MpContainer;
        internal static CanvasGroup      MpCanvasGroup;
        internal static LobbyView        View;
        internal static Text             MainFooterText;
        internal static SlotSelectScreen ScreenInstance;

        // Latest message from an action or the network layer. It overrides the
        // phase's default status line for a while (see SlotSelectUpdatePatch).
        internal static string           StatusBase  = "";
        internal static bool             StatusAnimate;
        internal static float            StatusSetAt = -100f;

        // ── Reflection handles ────────────────────────────────────────────────
        internal static readonly BindingFlags BF =
            BindingFlags.NonPublic | BindingFlags.Instance;

        internal static readonly FieldInfo TextItemsField =
            typeof(SlotSelectScreen).GetField("mainMenuItems", BF);
        internal static readonly FieldInfo EnumItemsField =
            typeof(SlotSelectScreen).GetField("_availableMainMenuItems", BF);
        internal static readonly FieldInfo SelectionField =
            typeof(SlotSelectScreen).GetField("_mainMenuSelection", BF);
        internal static readonly FieldInfo TimeSinceStartField =
            typeof(SlotSelectScreen).GetField("timeSinceStart", BF);
        internal static readonly FieldInfo SelectedColorField =
            typeof(SlotSelectScreen).GetField("mainMenuSelectedColor", BF);
        internal static readonly FieldInfo UnselectedColorField =
            typeof(SlotSelectScreen).GetField("mainMenuUnselectedColor", BF);
        internal static readonly FieldInfo BackPromptField =
            typeof(SlotSelectScreen).GetField("backPrompt", BF);
        internal static readonly FieldInfo BackGlyphField =
            typeof(SlotSelectScreen).GetField("backGlyph", BF);
        internal static readonly FieldInfo BackSpacerField =
            typeof(SlotSelectScreen).GetField("backSpacer", BF);
        internal static readonly MethodInfo GetButtonDownMethod =
            typeof(SlotSelectScreen).GetMethod("GetButtonDown",
                BF, null, new[] { typeof(CupheadButton) }, null);

        internal static void Reset()
        {
            InMpMenu       = false;
            SavedMainSel   = 0;
            MainMenuMpIndex = -1;
            MainMenuCreditsIndex = -1;
            InputLocked    = false;
            MainContainer  = null;
            MpContainer    = null;
            MpCanvasGroup  = null;
            View           = null;
            MainFooterText = null;
            ScreenInstance = null;
            StatusBase     = "";
            StatusAnimate  = false;
            StatusSetAt    = -100f;
        }

        internal static void SetStatus(string msg, bool animate = false)
        {
            StatusBase    = msg ?? string.Empty;
            StatusAnimate = animate;
            StatusSetAt   = Time.unscaledTime;
        }

        internal static void ClearStatus()
        {
            StatusBase = string.Empty;
            StatusAnimate = false;
            StatusSetAt = -100f;
        }

        internal static SlotSelectScreen Resolve(SlotSelectScreen inst) => inst ?? ScreenInstance;

        internal static Color SelColor(SlotSelectScreen inst)
        {
            var target = Resolve(inst);
            if (SelectedColorField == null || target == null) return Color.white;

            try { return (Color)SelectedColorField.GetValue(target); }
            catch { return Color.white; }
        }

        internal static bool Btn(SlotSelectScreen inst, CupheadButton b)
        {
            var target = Resolve(inst);
            if (GetButtonDownMethod == null || target == null) return false;

            try { return (bool)GetButtonDownMethod.Invoke(target, new object[] { b }); }
            catch { return false; }
        }

        /// <summary>Shows the game's own BACK prompt (bottom right) while the lobby is open.</summary>
        internal static void SetBackPromptVisible(SlotSelectScreen inst, bool visible)
        {
            var target = Resolve(inst);
            if (target == null) return;
            foreach (var field in new[] { BackPromptField, BackGlyphField, BackSpacerField })
            {
                if (field == null) continue;
                try
                {
                    var rt = field.GetValue(target) as RectTransform;
                    if (rt != null && rt.gameObject.activeSelf != visible)
                        rt.gameObject.SetActive(visible);
                }
                catch { }
            }
        }
    }

    // ── Credits state ─────────────────────────────────────────────────────────────

    internal static class CreditsState
    {
        internal static bool             InCredits;
        internal static GameObject       CreditsContainer;
        internal static CanvasGroup      CreditsCanvasGroup;
        internal static GameObject       MainContainer;   // pointer to main menu root
        internal static SlotSelectScreen ScreenInstance;

        internal static void Reset()
        {
            InCredits          = false;
            CreditsContainer   = null;
            CreditsCanvasGroup = null;
            MainContainer      = null;
            ScreenInstance     = null;
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    //  AWAKE PATCH — builds the lobby + Credits panel, extends arrays
    // ────────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(SlotSelectScreen), "Awake")]
    public static class SlotSelectAwakePatch
    {
        static void Postfix(SlotSelectScreen __instance)
        {
            UiDump.MaybeRun(__instance);
            MpMenuState.Reset();
            MpMenuState.ScreenInstance    = __instance;
            CreditsState.Reset();
            CreditsState.ScreenInstance   = __instance;

            var fi = MpMenuState.TextItemsField;
            var ei = MpMenuState.EnumItemsField;
            if (fi == null || ei == null) { Plugin.Log.LogWarning("[Menu] Reflection fields missing."); return; }

            var textItems = fi.GetValue(__instance) as Text[];
            var enumItems = ei.GetValue(__instance) as SlotSelectScreen.MainMenuItem[];
            if (textItems == null || textItems.Length == 0 || enumItems == null)
            { Plugin.Log.LogWarning("[Menu] Arrays null."); return; }

            MpMenuState.MainContainer    = textItems[0].transform.parent.gameObject;
            CreditsState.MainContainer   = MpMenuState.MainContainer;
            var containerParent          = MpMenuState.MainContainer.transform.parent;

            var exitGO = textItems[textItems.Length - 1].gameObject;
            var sample = textItems[0];

            // ── Lobby card ────────────────────────────────────────────────────
            var view = LobbyView.Build(containerParent, MpMenuState.MainContainer.GetComponent<RectTransform>());
            var mpRoot = view.Root;
            var mpCg   = mpRoot.AddComponent<CanvasGroup>();
            mpCg.alpha = 0f;
            mpRoot.SetActive(false);
            MpMenuState.MpContainer   = mpRoot;
            MpMenuState.MpCanvasGroup = mpCg;
            MpMenuState.View          = view;

            var footerParent = GetUiRoot(containerParent);
            var footerGO = BuildText(footerParent != null ? footerParent.gameObject : containerParent.gameObject, "MainFooterText",
                Vector2.zero, new Vector2(520f, 24f),
                sample.font, Mathf.Max(11, sample.fontSize - 8),
                new Color(0.74f, 0.74f, 0.70f, 0.88f));
            var footerRT = footerGO.GetComponent<RectTransform>();
            if (footerRT != null)
            {
                footerRT.anchorMin = footerRT.anchorMax = new Vector2(0f, 0f);
                footerRT.pivot = new Vector2(0f, 0f);
                footerRT.anchoredPosition = new Vector2(34f, 26f);
            }
            MpMenuState.MainFooterText = footerGO.GetComponent<Text>();
            if (MpMenuState.MainFooterText != null)
            {
                MpMenuState.MainFooterText.alignment = TextAnchor.MiddleLeft;
                MpMenuState.MainFooterText.text = "CupHeads v" + PluginInfo.VERSION;
            }

            // ── Build CreditsPanel ────────────────────────────────────────────
            var credRoot = BuildPanel("CreditsPanel", containerParent,
                                      MpMenuState.MainContainer.GetComponent<RectTransform>());
            var credCg   = credRoot.AddComponent<CanvasGroup>();
            credCg.alpha = 0f;
            credRoot.SetActive(false);
            CreditsState.CreditsContainer   = credRoot;
            CreditsState.CreditsCanvasGroup = credCg;

            // Credits are positioned explicitly instead of using a layout group.
            // Unity 2017 occasionally mangles multiline menu text when it is
            // nested under dynamic layout components, so we keep each line fixed.
            var titleGO = BuildText(credRoot, "CreditsTitle",
                new Vector2(0f, 176f), new Vector2(880f, 54f),
                sample.font, sample.fontSize + 4,
                MpMenuState.SelColor(__instance));
            var titleT  = titleGO.GetComponent<Text>();
            if (titleT != null)
            {
                titleT.text     = "CREDITS";
                titleT.fontSize = sample.fontSize + 4;
                titleT.color    = MpMenuState.SelColor(__instance);
                titleT.horizontalOverflow = HorizontalWrapMode.Overflow;
                titleT.verticalOverflow   = VerticalWrapMode.Overflow;
                titleT.resizeTextForBestFit = false;
                titleT.lineSpacing        = 1.05f;
                titleT.alignment          = TextAnchor.MiddleCenter;
            }
            BuildCreditsLine(
                credRoot,
                "CreditsLine1",
                "Multiplayer Mod",
                new Vector2(0f, 86f),
                new Vector2(900f, 44f),
                sample.font,
                sample.fontSize,
                new Color(0.92f, 0.92f, 0.92f, 1f));

            BuildCreditsLine(
                credRoot,
                "CreditsLine2",
                "Made by Germanized / Sh0kr",
                new Vector2(0f, 38f),
                new Vector2(980f, 44f),
                sample.font,
                Mathf.Max(18, sample.fontSize - 1),
                new Color(0.92f, 0.92f, 0.92f, 1f));

            BuildCreditsLine(
                credRoot,
                "CreditsLine3",
                "Made for Daniel",
                new Vector2(0f, -40f),
                new Vector2(860f, 42f),
                sample.font,
                Mathf.Max(18, sample.fontSize - 1),
                new Color(0.92f, 0.92f, 0.92f, 1f));

            BuildCreditsLine(
                credRoot,
                "CreditsLine4",
                "Special thanks to Internallinked",
                new Vector2(0f, -86f),
                new Vector2(1080f, 38f),
                sample.font,
                Mathf.Max(14, sample.fontSize - 5),
                new Color(0.84f, 0.84f, 0.84f, 0.95f));

            BuildCreditsLine(
                credRoot,
                "CreditsLine5",
                "cuz me and him wanna play.",
                new Vector2(0f, -126f),
                new Vector2(980f, 40f),
                sample.font,
                Mathf.Max(15, sample.fontSize - 4),
                new Color(0.80f, 0.80f, 0.80f, 0.95f));

            var hintGO = BuildText(credRoot, "HintText",
                new Vector2(0f, -220f), new Vector2(700f, 30f),
                sample.font, Mathf.Max(12, sample.fontSize - 6),
                new Color(0.6f, 0.6f, 0.6f, 0.8f));
            var hintT = hintGO.GetComponent<Text>();
            if (hintT != null) hintT.text = "[ Press Escape to go back ]";

            // ── Append MULTIPLAYER + CREDITS to main-menu arrays ──────────────
            var mpLabelGO  = CloneItem(exitGO, MpMenuState.MainContainer, "MULTIPLAYER");
            PositionBelow(textItems, mpLabelGO);
            GameObject credLabelGO = null;
            if (Plugin.ShowCreditsMenu)
            {
                credLabelGO = CloneItem(exitGO, MpMenuState.MainContainer, "CREDITS");
                PositionBelow(textItems, credLabelGO, mpLabelGO);
            }

            int extraCount = Plugin.ShowCreditsMenu ? 2 : 1;
            var newTexts  = new Text[textItems.Length + extraCount];
            textItems.CopyTo(newTexts, 0);
            MpMenuState.MainMenuMpIndex = textItems.Length;
            newTexts[MpMenuState.MainMenuMpIndex] = mpLabelGO.GetComponent<Text>();
            MpMenuState.MainMenuCreditsIndex = -1;
            if (Plugin.ShowCreditsMenu && credLabelGO != null)
            {
                MpMenuState.MainMenuCreditsIndex = textItems.Length + 1;
                newTexts[MpMenuState.MainMenuCreditsIndex] = credLabelGO.GetComponent<Text>();
            }
            fi.SetValue(__instance, newTexts);

            var newEnums = new SlotSelectScreen.MainMenuItem[enumItems.Length + extraCount];
            enumItems.CopyTo(newEnums, 0);
            newEnums[MpMenuState.MainMenuMpIndex] = (SlotSelectScreen.MainMenuItem)99;
            if (MpMenuState.MainMenuCreditsIndex >= 0)
                newEnums[MpMenuState.MainMenuCreditsIndex] = (SlotSelectScreen.MainMenuItem)100;
            ei.SetValue(__instance, newEnums);

            if (Plugin.ShowCreditsMenu && credLabelGO != null)
            {
                __instance.StartCoroutine(EnforceLabels(
                    mpLabelGO.GetComponent<Text>(),   "MULTIPLAYER",
                    credLabelGO.GetComponent<Text>(), "CREDITS",
                    titleT,                           "CREDITS"));
            }
            else
            {
                __instance.StartCoroutine(EnforceLabels(
                    mpLabelGO.GetComponent<Text>(),   "MULTIPLAYER",
                    titleT,                           "CREDITS"));
            }

            Plugin.Log.LogInfo("[Menu] Setup complete.");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        static GameObject BuildPanel(string name, Transform parent, RectTransform srcRT)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            if (srcRT != null)
            {
                rt.anchorMin        = srcRT.anchorMin;
                rt.anchorMax        = srcRT.anchorMax;
                rt.pivot            = srcRT.pivot;
                rt.anchoredPosition = srcRT.anchoredPosition;
                rt.sizeDelta        = srcRT.sizeDelta;
            }
            return go;
        }

        static Transform GetUiRoot(Transform start)
        {
            Transform best = null;
            for (var current = start; current != null; current = current.parent)
            {
                if (current is RectTransform)
                    best = current;
            }

            return best ?? start;
        }

        static GameObject CloneItem(GameObject source, GameObject newParent, string label)
        {
            var go = UnityEngine.Object.Instantiate(source, newParent.transform, false);
            go.name = label;
            foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
            {
                if (b == null || b is Text || b is LayoutElement) continue;
                b.enabled = false;
            }
            var t = go.GetComponent<Text>();
            if (t != null)
            {
                t.text = label;
                t.supportRichText = true;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.resizeTextForBestFit = false;
                t.alignment = TextAnchor.MiddleCenter;
            }
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.enabled = true;
            le.ignoreLayout = false;
            // Use a fixed height — Cuphead text items have sizeDelta.y = 0 (auto-sized),
            // which would make VLG stack everything at the same position.
            le.preferredHeight = 40f;
            le.minHeight       = 40f;
            le.flexibleHeight  = 0f;
            le.preferredWidth  = 420f;
            le.minWidth        = 320f;

            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(le.preferredWidth, le.preferredHeight);
                rt.anchoredPosition = Vector2.zero;
            }
            return go;
        }

        static GameObject BuildText(GameObject parent, string name,
                                    Vector2 pos, Vector2 size,
                                    Font font, int fontSize, Color color)
        {
            var go  = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rt  = go.AddComponent<RectTransform>();
            rt.anchorMin        = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta        = size;
            var txt = go.AddComponent<Text>();
            txt.font               = font;
            txt.fontSize           = fontSize;
            txt.color              = color;
            txt.alignment          = TextAnchor.MiddleCenter;
            txt.supportRichText    = true;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow   = VerticalWrapMode.Overflow;
            txt.text               = "";
            return go;
        }

        static Text BuildCreditsLine(GameObject parent, string name, string text,
                                     Vector2 pos, Vector2 size,
                                     Font font, int fontSize, Color color)
        {
            var go = BuildText(parent, name, pos, size, font, fontSize, color);
            var txt = go.GetComponent<Text>();
            if (txt != null)
            {
                txt.text                 = text;
                txt.resizeTextForBestFit = false;
                txt.supportRichText      = false;
                txt.horizontalOverflow   = HorizontalWrapMode.Overflow;
                txt.verticalOverflow     = VerticalWrapMode.Truncate;
                txt.lineSpacing          = 1f;
                txt.alignByGeometry      = true;
            }

            return txt;
        }

        // Place target one menu-step below 'above' (or below the last item if above is null).
        // Step is always derived from the spacing between existing menu items so it matches
        // the game's layout regardless of individual item sizeDelta values.
        static void PositionBelow(Text[] items, GameObject target, GameObject above = null)
        {
            if (items.Length < 1) return;

            // Derive the natural step from existing item spacing
            float step;
            int last = items.Length - 1;
            if (items.Length >= 2)
            {
                float lastY = items[last].GetComponent<RectTransform>().anchoredPosition.y;
                float prevY = items[last - 1].GetComponent<RectTransform>().anchoredPosition.y;
                step = lastY - prevY;   // negative (downward)
            }
            else
            {
                float h = items[last].GetComponent<RectTransform>().rect.height;
                step = -(Mathf.Max(h, 40f) + 5f);
            }

            var refGO = above != null ? above : items[last].gameObject;
            RectTransform refRT = refGO.GetComponent<RectTransform>();
            var tgtRT = target.GetComponent<RectTransform>();
            if (refRT == null || tgtRT == null) return;

            tgtRT.anchoredPosition = new Vector2(refRT.anchoredPosition.x,
                                                  refRT.anchoredPosition.y + step);
        }

        static IEnumerator EnforceLabels(params object[] pairs)
        {
            for (int f = 0; f < 30; f++)
            {
                yield return null;
                for (int i = 0; i + 1 < pairs.Length; i += 2)
                {
                    var t = pairs[i]   as Text;
                    var s = pairs[i+1] as string;
                    if (t != null && s != null && t.text != s) t.text = s;
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    //  UPDATE PATCH
    // ────────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(SlotSelectScreen), "UpdateMainMenu")]
    public static class SlotSelectUpdatePatch
    {
        /// <summary>Where the lobby is in its flow; decides which rows exist.</summary>
        enum Phase
        {
            NoSteam,
            Idle,
            Busy,            // creating / joining a lobby, handshaking
            WaitingInvite,   // join pressed, waiting for a Steam invite
            HostingOpen,     // hosting, no partner connected yet
            InLobby,         // joined a lobby, not connected yet
            HostConnected,
            GuestConnected,
        }

        // Row ids
        const int RowHost = 1, RowJoin = 2, RowRetry = 3, RowColor = 4, RowBugReport = 5, RowBack = 6,
                  RowCancel = 7, RowOpenFriends = 8, RowInvite = 9, RowCopyCode = 10, RowCloseLobby = 11,
                  RowSaveFile = 12, RowPlayAs = 13, RowStart = 14, RowAskSync = 15, RowLeave = 16,
                  RowPlayWith = 17, RowInvitePartner = 18, RowGetUpdate = 19;

        /// <summary>The last person this player connected with, as Steam sees them now.</summary>
        struct Partner
        {
            public bool Known;
            public ulong Id;
            public string Name;
            public bool Online;
            public bool InCuphead;
        }

        static Partner GetPartner()
        {
            var partner = new Partner();
            if (_preview.HasValue)
            {
                partner.Known = true;
                partner.Name = "Daniel";
                partner.Online = true;
                partner.InCuphead = true;
                return partner;
            }

            ulong id = Plugin.LastPartnerSteamId;
            if (id == 0UL || Plugin.Net == null || !Plugin.Net.IsSteamReady)
                return partner;

            string name;
            bool online, inCuphead;
            if (!Plugin.Net.TryGetFriendPresence(id, out name, out online, out inCuphead))
                return partner;

            partner.Known = true;
            partner.Id = id;
            partner.Name = string.IsNullOrEmpty(name) ? Plugin.LastPartnerName : name;
            if (string.IsNullOrEmpty(partner.Name))
                partner.Name = "your partner";
            partner.Online = online;
            partner.InCuphead = inCuphead;
            return partner;
        }

        static string PartnerLabel(Partner partner)
        {
            return Shorten(partner.Name, 14).ToUpperInvariant();
        }

        static bool      _joinOverlayReady;
        static bool      _waitingForInvite;
        static float     _lastOverlayOpenTime = -999f;
        static float     _lastBackTime        = -999f;
        static int       _joinGeneration;
        static Coroutine _mpFadeRoutine;
        static Coroutine _creditsFadeRoutine;

        static readonly List<LobbyRow> _rows = new List<LobbyRow>(8);
        static int   _selectedId = -1;
        static Phase _phase = Phase.Idle;
        static Phase _statusPhase = Phase.Idle;

        static bool BackPressed(SlotSelectScreen inst) =>
            MpMenuState.Btn(inst, CupheadButton.Cancel)
         || MpMenuState.Btn(inst, CupheadButton.Pause);

        static void StopTrackedCoroutine(SlotSelectScreen inst, ref Coroutine routine)
        {
            if (inst != null && routine != null)
                inst.StopCoroutine(routine);

            routine = null;
        }

        static void StartTrackedCoroutine(SlotSelectScreen inst, ref Coroutine routine, IEnumerator body)
        {
            if (inst == null) return;
            StopTrackedCoroutine(inst, ref routine);
            routine = inst.StartCoroutine(body);
        }

        static void SetCanvasVisible(CanvasGroup cg, GameObject go, bool visible, float alpha = 1f)
        {
            if (go != null) go.SetActive(visible);
            if (cg == null) return;

            cg.alpha          = visible ? alpha : 0f;
            cg.interactable   = visible;
            cg.blocksRaycasts = visible;
        }

        static void HideCreditsImmediate()
        {
            CreditsState.InCredits = false;
            StopTrackedCoroutine(CreditsState.ScreenInstance, ref _creditsFadeRoutine);
            SetCanvasVisible(CreditsState.CreditsCanvasGroup, CreditsState.CreditsContainer, false);
        }

        static void HandleOverlayClosed()
        {
            if (!_waitingForInvite) return;
            Plugin.Log.LogInfo("[Menu] Steam overlay closed — cancelling join wait.");
            CancelNetworkOperation("Steam closed without an invite.");
        }

        static bool Prefix(SlotSelectScreen __instance)
        {
            // Credits panel blocks all normal menu processing
            if (CreditsState.InCredits)
            {
                RunCredits(__instance);
                return false;
            }

            if (!MpMenuState.InMpMenu) return true;

            if (MpMenuState.TimeSinceStartField != null)
            {
                float t = (float)MpMenuState.TimeSinceStartField.GetValue(__instance);
                if (t < 0.75f) return false;
            }
            RunMpMenu(__instance);
            return false;
        }

        static void Postfix(SlotSelectScreen __instance)
        {
            if (MpMenuState.InMpMenu || CreditsState.InCredits) return;

            // Prevent the same Accept press used to leave a sub-menu from
            // immediately re-opening it on the restored main-menu selection.
            if (Time.time - _lastBackTime < 0.2f) return;

            if (MpMenuState.TimeSinceStartField != null)
            {
                float t = (float)MpMenuState.TimeSinceStartField.GetValue(__instance);
                if (t < 0.75f) return;
            }

            UpdateMainFooterText();

            // An accepted Steam invite brings the lobby up by itself.
            if (InviteRouter.ConsumeOpenLobbyRequest()
             && Plugin.Net != null
             && (Plugin.Net.IsInLobby || Plugin.Net.IsInputLocked || Plugin.Net.IsConnected))
            {
                EnterMpMenu(__instance);
                return;
            }

            var ef = MpMenuState.EnumItemsField;
            var sf = MpMenuState.SelectionField;
            if (ef == null || sf == null) return;

            int sel   = (int)sf.GetValue(__instance);
            var items = ef.GetValue(__instance) as SlotSelectScreen.MainMenuItem[];
            if (items == null || sel < 0 || sel >= items.Length) return;

            int sentinel = (int)items[sel];
            if (sentinel == 99 && MpMenuState.Btn(__instance, CupheadButton.Accept))
                EnterMpMenu(__instance);
            else if (sentinel == 100 && MpMenuState.Btn(__instance, CupheadButton.Accept))
                EnterCredits(__instance);
        }

        static void UpdateMainFooterText()
        {
            if (MpMenuState.MainFooterText == null) return;

            string footer = "CupHeads v" + PluginInfo.VERSION;
            if (UpdateChecker.UpdateAvailable)
                footer += "  |  v" + UpdateChecker.LatestVersion + " is out (Multiplayer menu)";

            if (Plugin.Net != null && (Plugin.Net.IsConnected || Plugin.Net.IsInLobby))
            {
                string sessionHint = SessionSync.GetFooterHint();
                if (!string.IsNullOrEmpty(sessionHint))
                    footer += "  |  " + sessionHint;
            }
            else if (Plugin.Net != null && !Plugin.Net.IsSteamReady)
            {
                footer += "  |  Steam unavailable outside Steam";
            }
            else if (!UpdateChecker.UpdateAvailable && Plugin.EnableQoLHotkeys)
            {
                footer += "  |  F1 shortcuts";
            }

            if (MpMenuState.MainFooterText.text != footer)
                MpMenuState.MainFooterText.text = footer;
        }

        // ── Credits ──────────────────────────────────────────────────────────

        static void RunCredits(SlotSelectScreen inst)
        {
            if (!BackPressed(inst)) return;
            PlayMenuSound("level_menu_confirm");
            ExitCredits(inst);
        }

        static void EnterCredits(SlotSelectScreen inst)
        {
            if (!Plugin.ShowCreditsMenu) return;
            if (CreditsState.CreditsContainer == null) return;
            if (CreditsState.InCredits) return;

            var sf = MpMenuState.SelectionField;
            if (sf != null && MpMenuState.MainContainer != null)
                MpMenuState.SavedMainSel = (int)sf.GetValue(inst);

            if (CreditsState.MainContainer != null) CreditsState.MainContainer.SetActive(false);
            if (MpMenuState.MainFooterText != null) MpMenuState.MainFooterText.gameObject.SetActive(false);
            SetCanvasVisible(CreditsState.CreditsCanvasGroup, CreditsState.CreditsContainer, true, 0f);
            CreditsState.InCredits = true;

            PlayMenuSound("level_menu_confirm");

            if (CreditsState.ScreenInstance != null && CreditsState.CreditsCanvasGroup != null)
                StartTrackedCoroutine(CreditsState.ScreenInstance, ref _creditsFadeRoutine,
                    FadeCanvas(CreditsState.CreditsCanvasGroup, 0f, 1f, 0.2f));

            Plugin.Log.LogInfo("[Menu] Entered Credits.");
        }

        static void ExitCredits(SlotSelectScreen inst)
        {
            CreditsState.InCredits = false;
            StopTrackedCoroutine(CreditsState.ScreenInstance, ref _creditsFadeRoutine);

            if (CreditsState.MainContainer != null) CreditsState.MainContainer.SetActive(true);
            if (MpMenuState.MainFooterText != null) MpMenuState.MainFooterText.gameObject.SetActive(true);
            if (CreditsState.ScreenInstance != null && CreditsState.CreditsCanvasGroup != null)
                StartTrackedCoroutine(CreditsState.ScreenInstance, ref _creditsFadeRoutine,
                    FadeAndHide(CreditsState.CreditsCanvasGroup, CreditsState.CreditsContainer, 0.15f));
            else
                SetCanvasVisible(CreditsState.CreditsCanvasGroup, CreditsState.CreditsContainer, false);

            // Restore menu selection to CREDITS item
            var sf   = MpMenuState.SelectionField;
            if (sf != null)
                sf.SetValue(inst, MpMenuState.MainMenuCreditsIndex >= 0
                    ? MpMenuState.MainMenuCreditsIndex
                    : MpMenuState.SavedMainSel);

            Plugin.Log.LogInfo("[Menu] Exited Credits.");
        }

        // ── Clipboard ────────────────────────────────────────────────────────

        static string _clipboardCache = string.Empty;
        static float _clipboardReadAt = -10f;

        /// <summary>The OS clipboard is read at most twice a second.</summary>
        static string ReadClipboardThrottled()
        {
            float now = Time.unscaledTime;
            if (now - _clipboardReadAt >= 0.5f)
            {
                _clipboardReadAt = now;
                try { _clipboardCache = GUIUtility.systemCopyBuffer ?? string.Empty; }
                catch { _clipboardCache = string.Empty; }
            }
            return _clipboardCache;
        }

        static bool TryGetClipboardLobbyId(out string rawText, out string lobbyId)
        {
            rawText = ReadClipboardThrottled();
            lobbyId = string.Empty;
            if (string.IsNullOrEmpty(rawText) || rawText.Trim().Length == 0)
                return false;

            string trimmed = rawText.Trim();
            ulong numericId;
            if (ulong.TryParse(trimmed, out numericId) && numericId != 0UL)
            {
                rawText = trimmed;
                lobbyId = numericId.ToString();
                return true;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed,
                @"Lobby ID:\s*(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            rawText = trimmed;
            lobbyId = match.Groups[1].Value;
            return !string.IsNullOrEmpty(lobbyId);
        }

        // ── Lobby data ───────────────────────────────────────────────────────

        static int GetLobbySaveSlotIndex()
        {
            return Mathf.Clamp(PlayerData.CurrentSaveFileIndex, 0, 2);
        }

        static PlayerData GetLobbySaveData()
        {
            return PlayerData.GetDataForSlot(GetLobbySaveSlotIndex());
        }

        static bool GetLobbyPlayer1IsMugman()
        {
            var data = GetLobbySaveData();
            return data != null && data.isPlayer1Mugman;
        }

        static string GetLobbyLeadCharacterName()
        {
            return GetLobbyPlayer1IsMugman() ? "MUGMAN" : "CUPHEAD";
        }

        static string GetLobbyGuestCharacterName()
        {
            return GetLobbyPlayer1IsMugman() ? "CUPHEAD" : "MUGMAN";
        }

        static bool IsSlotEmpty(PlayerData data)
        {
            try
            {
                return data == null
                    || (!data.GetMapData(Scenes.scene_map_world_1).sessionStarted
                        && !data.IsTutorialCompleted
                        && data.CountLevelsCompleted(Level.world1BossLevels) == 0);
            }
            catch { return false; }
        }

        /// <summary>"A - 12%" for the picker; "NEW GAME" for an unused slot.</summary>
        static string BuildSaveValue(int slot)
        {
            string letter = ((char)('A' + slot)).ToString();
            var data = PlayerData.GetDataForSlot(slot);
            if (IsSlotEmpty(data))
                return letter + " - NEW GAME";

            int percent = 0;
            try
            {
                percent = Mathf.RoundToInt(data.GetCompletionPercentage());
                if (DLCManager.DLCEnabled())
                    percent += Mathf.RoundToInt(data.GetCompletionPercentageDLC());
            }
            catch { }
            return letter + " - " + percent + "%";
        }

        /// <summary>The slot as the save screen shows it: "MUGMAN A - 2%, Inkwell Isle Two".</summary>
        static string DescribeSave(int slot)
        {
            var data = PlayerData.GetDataForSlot(slot);
            if (IsSlotEmpty(data))
                return "Save " + (char)('A' + slot) + " is a brand new game";

            string title = (data.isPlayer1Mugman ? "MUGMAN " : "CUPHEAD ") + (char)('A' + slot);
            try
            {
                string key = data.isPlayer1Mugman ? "TitleScreenMugmanSlot" + (slot + 1) : "TitleScreenSlot" + (slot + 1);
                string localized = Localization.Translate(key).text;
                if (!string.IsNullOrEmpty(localized))
                    title = localized;
            }
            catch { }

            string world = string.Empty;
            try
            {
                string worldKey;
                switch (data.CurrentMap)
                {
                    case Scenes.scene_map_world_2: worldKey = "TitleScreenWorld2"; break;
                    case Scenes.scene_map_world_3: worldKey = "TitleScreenWorld3"; break;
                    case Scenes.scene_map_world_4: worldKey = "TitleScreenWorld4"; break;
                    case Scenes.scene_map_world_DLC:
                        worldKey = DLCManager.DLCEnabled() ? "TitleScreenWorldDLC" : "TitleScreenWorld1"; break;
                    default: worldKey = "TitleScreenWorld1"; break;
                }
                world = Localization.Translate(worldKey).text;
            }
            catch { }

            string percent = BuildSaveValue(slot);
            int dash = percent.IndexOf(" - ");
            string tail = dash >= 0 ? percent.Substring(dash) : string.Empty;
            return string.IsNullOrEmpty(world) ? title + tail : title + tail + ", " + world;
        }

        static string LocalPlayerName()
        {
            if (Plugin.Net != null && Plugin.Net.IsSteamReady)
            {
                try
                {
                    string name = SteamFriends.GetPersonaName();
                    if (!string.IsNullOrEmpty(name))
                        return Shorten(name, 16);
                }
                catch { }
            }
            return "You";
        }

        static string PartnerName()
        {
            if (_preview.HasValue)
                return "Daniel";
            string name = Plugin.Net != null ? Plugin.Net.CurrentPeerName : null;
            if (string.IsNullOrEmpty(name) || name == "Unknown Player")
                return "Your partner";
            return Shorten(name, 16);
        }

        // ── Developer preview ─────────────────────────────────────────────────
        // With BepInEx/config/cupheads-uipreview.flag present, Insert steps the
        // lobby through every phase with sample data (no networking), so each
        // screen can be checked without a second player.

        static Phase? _preview;
        static bool? _previewEnabled;

        static bool PreviewEnabled
        {
            get
            {
                if (!_previewEnabled.HasValue)
                {
                    try { _previewEnabled = System.IO.File.Exists(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "cupheads-uipreview.flag")); }
                    catch { _previewEnabled = false; }
                }
                return _previewEnabled.Value;
            }
        }

        static void TickPreview()
        {
            if (!PreviewEnabled || !Input.GetKeyDown(KeyCode.Insert))
                return;
            int next = _preview.HasValue ? (int)_preview.Value + 1 : 0;
            _preview = next > (int)Phase.GuestConnected ? (Phase?)null : (Phase)next;
            Plugin.Log.LogInfo("[Menu] Preview phase: " + (_preview.HasValue ? _preview.Value.ToString() : "live"));
        }

        static int PreviewPing => 42;

        // ── UI tour hooks (Diagnostics/UiTour drives the lobby without input) ──

        internal static int TourPhaseCount => (int)Phase.GuestConnected + 1;

        internal static string TourPhaseName(int phase) => ((Phase)phase).ToString();

        internal static bool TourOpenLobby()
        {
            var inst = MpMenuState.ScreenInstance;
            if (inst == null || MpMenuState.MpContainer == null)
                return false;
            if (!MpMenuState.InMpMenu)
                EnterMpMenu(inst);
            return MpMenuState.InMpMenu;
        }

        /// <summary>-1 = live data; otherwise show that phase with sample data.</summary>
        internal static void TourPreviewPhase(int phase)
        {
            _preview = phase < 0 ? (Phase?)null : (Phase)phase;
        }

        internal static void TourCloseLobby()
        {
            _preview = null;
            var inst = MpMenuState.ScreenInstance;
            if (inst != null && MpMenuState.InMpMenu)
                ExitMpMenu(inst, preserveConnection: true);
        }

        static string Shorten(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        static Color? SwatchFor(int selection)
        {
            int normalized = PlayerColorSync.NormalizeSelection(selection);
            if (normalized == PlayerColorSync.AutoSelection || normalized == PlayerColorSync.ClassicSelection)
                return null;
            return PlayerColorSync.GetPaletteColor(normalized);
        }

        static string Dots()
        {
            int n = Mathf.FloorToInt(Time.unscaledTime / 0.4f) % 4;
            return n == 0 ? "" : n == 1 ? "." : n == 2 ? ".." : "...";
        }

        // ── Phase, rows, hints ───────────────────────────────────────────────

        static Phase ComputePhase()
        {
            if (_preview.HasValue)
                return _preview.Value;
            var net = Plugin.Net;
            if (net == null || !net.IsSteamReady)
                return Phase.NoSteam;
            if (net.IsConnected)
                return net.IsHost ? Phase.HostConnected : Phase.GuestConnected;
            if (_waitingForInvite || _joinOverlayReady)
                return Phase.WaitingInvite;
            if (net.IsInputLocked)
                return Phase.Busy;
            if (net.IsInLobby)
                return net.IsHost ? Phase.HostingOpen : Phase.InLobby;
            return Phase.Idle;
        }

        static void BuildRows(Phase phase)
        {
            _rows.Clear();
            var color = LobbyRow.Picker(RowColor, "COLOR",
                PlayerColorSync.GetSelectionName(Plugin.PreferredPlayerColorSelection).ToUpperInvariant(),
                SwatchFor(Plugin.PreferredPlayerColorSelection));

            switch (phase)
            {
                case Phase.NoSteam:
                case Phase.Idle:
                {
                    bool steam = phase != Phase.NoSteam;
                    var partner = steam ? GetPartner() : new Partner();
                    if (partner.Known)
                        _rows.Add(LobbyRow.Action(RowPlayWith, "PLAY WITH " + PartnerLabel(partner), partner.Online));
                    if (steam && Plugin.Net.CanRetryLastAction)
                        _rows.Add(LobbyRow.Action(RowRetry, Plugin.Net.GetRetryActionLabel()));
                    _rows.Add(LobbyRow.Action(RowHost, "HOST A GAME", steam));
                    _rows.Add(LobbyRow.Action(RowJoin, "JOIN A GAME", steam));
                    color.GapBefore = true;
                    _rows.Add(color);
                    if (UpdateChecker.UpdateAvailable)
                        _rows.Add(new LobbyRow { Id = RowGetUpdate, Label = "GET v" + UpdateChecker.LatestVersion, Enabled = true, GapBefore = true });
                    _rows.Add(new LobbyRow { Id = RowBugReport, Label = "BUG REPORT", Enabled = true, GapBefore = !UpdateChecker.UpdateAvailable });
                    _rows.Add(LobbyRow.Action(RowBack, "BACK"));
                    break;
                }

                case Phase.Busy:
                case Phase.InLobby:
                    _rows.Add(color);
                    _rows.Add(new LobbyRow { Id = RowCancel, Label = "CANCEL", Enabled = true, GapBefore = true });
                    break;

                case Phase.WaitingInvite:
                    _rows.Add(LobbyRow.Action(RowOpenFriends, "OPEN STEAM FRIENDS"));
                    _rows.Add(color);
                    _rows.Add(new LobbyRow { Id = RowCancel, Label = "CANCEL", Enabled = true, GapBefore = true });
                    break;

                case Phase.HostingOpen:
                {
                    // The preview has no real lobby; show the rows as a host would see them.
                    bool canInvite = _preview.HasValue || Plugin.Net.CanInviteFriend;
                    var partner = GetPartner();
                    if (partner.Known && !Plugin.Net.IsInCurrentLobby(partner.Id))
                        _rows.Add(LobbyRow.Action(RowInvitePartner, "INVITE " + PartnerLabel(partner),
                            partner.Online && canInvite));
                    _rows.Add(LobbyRow.Action(RowInvite, "INVITE A FRIEND", canInvite));
                    _rows.Add(LobbyRow.Action(RowCopyCode, "COPY LOBBY CODE", _preview.HasValue || Plugin.Net.CanCopyLobbyId));
                }
                    color.GapBefore = true;
                    _rows.Add(color);
                    _rows.Add(new LobbyRow { Id = RowCloseLobby, Label = "CLOSE LOBBY", Enabled = true, GapBefore = true });
                    break;

                case Phase.HostConnected:
                {
                    int slot = GetLobbySaveSlotIndex();
                    _rows.Add(LobbyRow.Picker(RowSaveFile, "SAVE FILE", BuildSaveValue(slot)));
                    _rows.Add(LobbyRow.Picker(RowPlayAs, "YOU PLAY", GetLobbyLeadCharacterName()));
                    _rows.Add(color);
                    string reason;
                    bool canStart = SessionSync.CanHostStartRun(out reason);
                    _rows.Add(new LobbyRow { Id = RowStart, Label = "START!", Enabled = canStart, GapBefore = true });
                    _rows.Add(LobbyRow.Action(RowLeave, "LEAVE"));
                    break;
                }

                case Phase.GuestConnected:
                    _rows.Add(color);
                    if (!SessionSync.HasTrackedSave)
                        _rows.Add(LobbyRow.Action(RowAskSync, "ASK HOST AGAIN", Plugin.Net.CanRequestRecovery));
                    _rows.Add(new LobbyRow { Id = RowLeave, Label = "LEAVE", Enabled = true, GapBefore = true });
                    break;
            }
        }

        /// <summary>The row a phase should land on when it is entered.</summary>
        static int PrimaryRow(Phase phase)
        {
            switch (phase)
            {
                case Phase.Idle:
                {
                    var partner = GetPartner();
                    if (partner.Known && partner.Online) return RowPlayWith;
                    return Plugin.Net != null && Plugin.Net.CanRetryLastAction ? RowRetry : RowHost;
                }
                case Phase.NoSteam: return RowBack;
                case Phase.WaitingInvite: return RowOpenFriends;
                case Phase.HostingOpen:
                {
                    var partner = GetPartner();
                    return partner.Known && partner.Online && !Plugin.Net.IsInCurrentLobby(partner.Id)
                        ? RowInvitePartner : RowInvite;
                }
                case Phase.HostConnected: return RowStart;
                case Phase.GuestConnected: return RowColor;
                default: return RowCancel;
            }
        }

        static int IndexOfRow(int id)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Id == id) return i;
            return -1;
        }

        static string DefaultStatus(Phase phase, out Color color)
        {
            color = NativeUiKit.Ink;
            switch (phase)
            {
                case Phase.NoSteam:
                    color = NativeUiKit.Red;
                    return "Steam isn't running. Start Cuphead from Steam to play online.";
                case Phase.Busy:
                    switch (Plugin.Net.CurrentStateName)
                    {
                        case "CreatingLobby": return "Opening your lobby" + Dots();
                        case "JoiningLobby": return "Joining your friend's lobby" + Dots();
                        default: return "Shaking hands with your partner" + Dots();
                    }
                case Phase.WaitingInvite:
                    return _joinOverlayReady
                        ? "No invite yet. Open your Steam friends to accept one."
                        : "Waiting for an invite" + Dots();
                case Phase.HostingOpen:
                    return Plugin.Net.LobbyMemberCount > 1
                        ? "Your partner is joining" + Dots()
                        : "Your lobby is open! Invite a friend to play.";
                case Phase.InLobby:
                    return "Connecting to the host" + Dots();
                case Phase.HostConnected:
                    if (SessionSync.CompatibilitySeverity >= SessionIssueSeverity.Warning)
                    {
                        color = NativeUiKit.WarnInk;
                        return SessionSync.CompatibilitySummary;
                    }
                    return "Your partner is here! Pick a save, then START.";
                case Phase.GuestConnected:
                    if (SessionSync.DesyncSeverity >= SessionIssueSeverity.Warning)
                    {
                        color = NativeUiKit.WarnInk;
                        return SessionSync.DesyncSummary;
                    }
                    if (SessionSync.CompatibilitySeverity >= SessionIssueSeverity.Warning)
                    {
                        color = NativeUiKit.WarnInk;
                        return SessionSync.CompatibilitySummary;
                    }
                    return "Connected! Waiting for the host to start" + Dots();
                default:
                    if (UpdateChecker.UpdateAvailable)
                    {
                        color = NativeUiKit.WarnInk;
                        return "CupHeads v" + UpdateChecker.LatestVersion + " is out! Both players need it.";
                    }
                    return "Play the whole adventure with a friend over Steam.";
            }
        }

        static string HintFor(int id)
        {
            string clipboardRaw, clipboardLobbyId;
            switch (id)
            {
                case RowPlayWith:
                case RowInvitePartner:
                {
                    var partner = GetPartner();
                    if (!partner.Online)
                        return partner.Name + " is offline right now.";
                    string what = id == RowPlayWith
                        ? "Opens a lobby and sends " + partner.Name + " an invite."
                        : "Sends " + partner.Name + " an invite to this lobby.";
                    return partner.InCuphead ? what + " They're in Cuphead now!" : what;
                }
                case RowGetUpdate:
                    return "Opens the download page for v" + UpdateChecker.LatestVersion
                        + " (you have v" + PluginInfo.VERSION + ").";
                case RowHost:
                    return "Open a friends-only lobby, then invite someone to join you.";
                case RowJoin:
                    if (TryGetClipboardLobbyId(out clipboardRaw, out clipboardLobbyId))
                        return "A lobby code is on your clipboard. Press CONFIRM to join it.";
                    return Plugin.AutoOpenSteamFriends
                        ? "Wait for a friend's Steam invite. Your friends list opens so you can accept it."
                        : "Wait for a friend's Steam invite (Shift+Tab opens Steam).";
                case RowRetry:
                    return "Try the last host or join again.";
                case RowColor:
                    return "Your color in the lobby and in game. LEFT / RIGHT to change.";
                case RowBugReport:
                    return "Saves logs and settings to a folder you can send with a bug report.";
                case RowBack:
                    return "Back to the main menu.";
                case RowCancel:
                    return "Stop and go back.";
                case RowOpenFriends:
                    return "Open the Steam overlay to accept your friend's invite.";
                case RowInvite:
                    return "Pick a friend from Steam to invite.";
                case RowCopyCode:
                    return "Copies the lobby code. A friend with it on their clipboard can press JOIN A GAME.";
                case RowCloseLobby:
                    return "Close the lobby.";
                case RowSaveFile:
                    return DescribeSave(GetLobbySaveSlotIndex()) + ". LEFT / RIGHT to change.";
                case RowPlayAs:
                    return "Your partner plays " + GetLobbyGuestCharacterName().ToLowerInvariant().Replace("cuphead", "Cuphead").Replace("mugman", "Mugman")
                        + ". LEFT / RIGHT to swap.";
                case RowStart:
                {
                    string reason;
                    return SessionSync.CanHostStartRun(out reason)
                        ? "Off to the Inkwell Isles, together!"
                        : reason;
                }
                case RowAskSync:
                    return "Ask the host to send the save again if nothing is happening.";
                case RowLeave:
                    return "Disconnect from your partner.";
                default:
                    return string.Empty;
            }
        }

        static void RenderSeats(Phase phase)
        {
            var view = MpMenuState.View;
            if (view == null) return;

            bool leadMugman = GetLobbyPlayer1IsMugman();
            var localSwatch = SwatchFor(Plugin.PreferredPlayerColorSelection);

            var you = new LobbySeat
            {
                Occupied = true,
                Name = LocalPlayerName(),
                Swatch = localSwatch,
            };
            var empty = new LobbySeat
            {
                Occupied = false,
                Title = "OPEN SEAT",
                Detail = string.Empty,
            };

            switch (phase)
            {
                case Phase.HostConnected:
                case Phase.GuestConnected:
                {
                    bool host = phase == Phase.HostConnected;
                    var partner = new LobbySeat
                    {
                        Occupied = true,
                        Name = PartnerName(),
                        Swatch = SwatchFor(Plugin.Net.GetParticipantColorSelection(
                            (byte)(host ? PlayerId.PlayerTwo : PlayerId.PlayerOne))),
                    };

                    int ping = _preview.HasValue ? PreviewPing : Plugin.Net.Latency;
                    string quality = ping < 80 ? "great" : ping < 150 ? "okay" : "laggy";
                    partner.Detail = (host ? "Partner" : "Host") + "  ·  "
                        + (ping > 0 ? ping + " ms, " + quality : "checking ping" + Dots());

                    you.Detail = host ? "You  ·  Host" : "You  ·  Partner";
                    if (host)
                    {
                        you.Mugman = leadMugman;
                        you.Title = leadMugman ? "MUGMAN" : "CUPHEAD";
                        partner.Mugman = !leadMugman;
                        partner.Title = leadMugman ? "CUPHEAD" : "MUGMAN";
                        view.SetSeat(0, you);
                        view.SetSeat(1, partner);
                    }
                    else
                    {
                        bool known = SessionSync.HasTrackedSave;
                        partner.Mugman = known && leadMugman;
                        partner.Title = known ? (leadMugman ? "MUGMAN" : "CUPHEAD") : "HOST";
                        you.Mugman = known && !leadMugman;
                        you.Title = known ? (leadMugman ? "CUPHEAD" : "MUGMAN") : "GUEST";
                        view.SetSeat(0, partner);
                        view.SetSeat(1, you);
                    }
                    return;
                }

                case Phase.HostingOpen:
                    you.Mugman = leadMugman;
                    you.Title = leadMugman ? "MUGMAN" : "CUPHEAD";
                    you.Detail = "You  ·  Host";
                    empty.Name = Plugin.Net.LobbyMemberCount > 1 ? "Joining" + Dots() : "Waiting for a friend" + Dots();
                    break;

                case Phase.WaitingInvite:
                case Phase.Busy:
                case Phase.InLobby:
                    you.Title = "CUPHEAD";
                    you.Detail = "You";
                    empty.Name = "Waiting" + Dots();
                    break;

                default:
                    you.Title = "CUPHEAD";
                    you.Detail = phase == Phase.NoSteam ? "You  ·  Offline" : "You";
                    empty.Name = "Room for one more!";
                    break;
            }

            view.SetSeat(0, you);
            view.SetSeat(1, empty);
        }

        static void Render()
        {
            var view = MpMenuState.View;
            if (view == null) return;

            BuildRows(_phase);
            int index = IndexOfRow(_selectedId);
            if (index < 0)
            {
                index = Mathf.Max(0, IndexOfRow(PrimaryRow(_phase)));
                _selectedId = _rows.Count > 0 ? _rows[index].Id : -1;
            }

            view.SetRows(_rows, index);
            RenderSeats(_phase);

            // An action's message shows for a while (errors until the phase moves on).
            float age = Time.unscaledTime - MpMenuState.StatusSetAt;
            bool showMessage = !string.IsNullOrEmpty(MpMenuState.StatusBase)
                && (age < 4f || (_statusPhase == _phase && age < 12f));
            if (showMessage)
            {
                string text = MpMenuState.StatusBase;
                if (MpMenuState.StatusAnimate)
                    text = text.TrimEnd('.') + Dots();
                bool error = text.IndexOf("fail", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("could not", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("timed out", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("unavailable", System.StringComparison.OrdinalIgnoreCase) >= 0;
                view.SetStatus(text.Replace("\n", " "), error ? NativeUiKit.Red : NativeUiKit.Ink);
            }
            else
            {
                Color color;
                view.SetStatus(DefaultStatus(_phase, out color), color);
            }

            view.SetHint(_selectedId >= 0 ? HintFor(_selectedId) : string.Empty);
        }

        // ── MP menu loop ──────────────────────────────────────────────────────

        static void RunMpMenu(SlotSelectScreen inst)
        {
            if (MpMenuState.View == null) return;
            TickPreview();

            bool netLocked = Plugin.Net.IsInputLocked || Plugin.Net.IsConnected;
            if (netLocked)
            {
                _waitingForInvite = false;
                _joinOverlayReady = false;
            }
            MpMenuState.InputLocked = _waitingForInvite || netLocked;

            if (Plugin.Net.IsConnected && Plugin.Net.IsHost && !SessionSync.HasTrackedSave)
                PreviewHostLobbySelection(inst, updateStatus: false);

            if (Plugin.Net.ShouldForceUnlockUi(Time.realtimeSinceStartup))
            {
                Plugin.Log.LogWarning("[Menu] Input-lock watchdog fired - force unlock.");
                CancelNetworkOperation("That took too long. Please try again.");
            }

            var phase = ComputePhase();
            if (phase != _phase)
            {
                _phase = phase;
                _selectedId = PrimaryRow(phase);
            }

            MpMenuState.SetBackPromptVisible(inst, true);
            BuildRows(_phase);

            int index = IndexOfRow(_selectedId);
            if (index < 0) index = Mathf.Max(0, IndexOfRow(PrimaryRow(_phase)));
            int count = _rows.Count;

            if (count > 0)
            {
                int prev = index;
                if (MpMenuState.Btn(inst, CupheadButton.MenuDown))
                    index = (index + 1) % count;
                if (MpMenuState.Btn(inst, CupheadButton.MenuUp))
                    index = (index - 1 + count) % count;
                _selectedId = _rows[index].Id;
                if (index != prev)
                    PlayMenuSound("level_menu_move");
            }

            int dir = MpMenuState.Btn(inst, CupheadButton.MenuRight) ? 1
                    : MpMenuState.Btn(inst, CupheadButton.MenuLeft) ? -1 : 0;
            if (dir != 0 && count > 0 && _rows[index].Value != null)
            {
                ChangePicker(inst, _selectedId, dir);
                PlayMenuSound("level_menu_move");
            }

            if (BackPressed(inst))
            {
                // Debounce: ignore rapid double-presses (prevents double-shutdown)
                if (Time.time - _lastBackTime >= 0.2f)
                {
                    _lastBackTime = Time.time;
                    PlayMenuSound("level_menu_confirm");
                    HandleBack(inst);
                }
                Render();
                return;
            }

            if (count > 0 && MpMenuState.Btn(inst, CupheadButton.Accept))
            {
                var row = _rows[index];
                PlayMenuSound("level_menu_confirm");
                if (!row.Enabled)
                {
                    string why = HintFor(row.Id);
                    if (!string.IsNullOrEmpty(why))
                        MpMenuState.SetStatus(why);
                }
                else
                {
                    Activate(inst, row.Id);
                }
            }

            Render();
        }

        /// <summary>
        /// Back is never destructive: in a session it only moves the cursor to
        /// LEAVE / CLOSE LOBBY; while waiting it cancels; when idle it leaves.
        /// </summary>
        static void HandleBack(SlotSelectScreen inst)
        {
            switch (_phase)
            {
                case Phase.Idle:
                case Phase.NoSteam:
                    ExitMpMenu(inst);
                    return;
                case Phase.Busy:
                case Phase.WaitingInvite:
                case Phase.InLobby:
                    CancelNetworkOperation("Cancelled.");
                    return;
                case Phase.HostingOpen:
                    if (_selectedId == RowCloseLobby)
                        CancelNetworkOperation("Lobby closed.");
                    else
                        _selectedId = RowCloseLobby;
                    return;
                default:
                    if (_selectedId == RowLeave)
                        CancelNetworkOperation("You left the session.");
                    else
                        _selectedId = RowLeave;
                    return;
            }
        }

        static void ChangePicker(SlotSelectScreen inst, int id, int dir)
        {
            switch (id)
            {
                case RowColor:
                    OnCyclePlayerColor(dir);
                    break;
                case RowSaveFile:
                    OnCycleHostSaveSlot(inst, dir);
                    break;
                case RowPlayAs:
                    OnToggleHostLeadCharacter(inst);
                    break;
            }
        }

        static void Activate(SlotSelectScreen inst, int id)
        {
            string status;
            switch (id)
            {
                case RowPlayWith:
                    OnPlayWithPartner();
                    break;
                case RowInvitePartner:
                {
                    var partner = GetPartner();
                    if (partner.Id != 0UL)
                    {
                        Plugin.Net.InviteFriendToLobby(partner.Id, out status);
                        MpMenuState.SetStatus(status);
                    }
                    break;
                }
                case RowGetUpdate:
                    Application.OpenURL(UpdateChecker.ReleasesPage);
                    MpMenuState.SetStatus("Opened the download page in your browser.");
                    break;
                case RowHost:
                    OnHostGame();
                    break;
                case RowJoin:
                    if (!HandleJoinAccept())
                        OnJoinGame(inst);
                    break;
                case RowRetry:
                    OnRetryLast();
                    break;
                case RowColor:
                    OnCyclePlayerColor(1);
                    break;
                case RowBugReport:
                    OnExportBugReport();
                    break;
                case RowBack:
                    ExitMpMenu(inst);
                    break;
                case RowCancel:
                    CancelNetworkOperation("Cancelled.");
                    break;
                case RowOpenFriends:
                    _lastOverlayOpenTime = -999f;
                    _joinOverlayReady = true;
                    HandleJoinAccept();
                    break;
                case RowInvite:
                    OnInviteFriend();
                    break;
                case RowCopyCode:
                    OnCopyLobbyId();
                    break;
                case RowCloseLobby:
                    CancelNetworkOperation("Lobby closed.");
                    break;
                case RowSaveFile:
                    OnCycleHostSaveSlot(inst, 1);
                    break;
                case RowPlayAs:
                    OnToggleHostLeadCharacter(inst);
                    break;
                case RowStart:
                    OnStartGame(inst);
                    break;
                case RowAskSync:
                    Plugin.Net.TryRequestRecovery(out status);
                    MpMenuState.SetStatus(status);
                    break;
                case RowLeave:
                    CancelNetworkOperation("You left the session.");
                    break;
            }
            _statusPhase = ComputePhase();
        }

        // ── Enter / exit MP ───────────────────────────────────────────────────

        static void EnterMpMenu(SlotSelectScreen inst)
        {
            if (MpMenuState.MpContainer == null || MpMenuState.MainContainer == null)
            { Plugin.Log.LogWarning("[Menu] Containers not ready."); return; }

            var sf = MpMenuState.SelectionField;
            if (sf != null) MpMenuState.SavedMainSel = (int)sf.GetValue(inst);

            HideCreditsImmediate();
            if (Plugin.Net != null) Plugin.Net.OnOverlayClosed += HandleOverlayClosed;
            MpMenuState.MainContainer.SetActive(false);
            if (MpMenuState.MainFooterText != null) MpMenuState.MainFooterText.gameObject.SetActive(false);
            SetCanvasVisible(MpMenuState.MpCanvasGroup, MpMenuState.MpContainer, true, 0f);
            MpMenuState.InMpMenu     = true;
            MpMenuState.InputLocked  = false;
            MpMenuState.ClearStatus();
            _joinOverlayReady        = false;
            _waitingForInvite        = false;
            _phase                   = ComputePhase();
            _statusPhase             = _phase;
            _selectedId              = PrimaryRow(_phase);
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine);

            if (Plugin.Net != null)
                Plugin.Net.NotifyLocalAppearanceChanged();
            if (Plugin.Net.IsConnected && Plugin.Net.IsHost && !SessionSync.HasTrackedSave)
                PreviewHostLobbySelection(inst, updateStatus: false);

            MpMenuState.SetBackPromptVisible(inst, true);
            Render();

            if (MpMenuState.ScreenInstance != null)
                StartTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine,
                    FadeCanvas(MpMenuState.MpCanvasGroup, 0f, 1f, 0.2f));

            Plugin.Log.LogInfo("[Menu] Entered MP menu.");
        }

        static void ExitMpMenu(
            SlotSelectScreen inst,
            bool preserveConnection = false,
            bool restoreMainMenuSelection = true,
            bool showMainMenu = true)
        {
            MpMenuState.InMpMenu    = false;
            MpMenuState.InputLocked = false;
            _joinOverlayReady       = false;
            _waitingForInvite       = false;
            if (Plugin.Net != null) Plugin.Net.OnOverlayClosed -= HandleOverlayClosed;
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine);
            MpMenuState.SetBackPromptVisible(inst, false);

            if (MpMenuState.ScreenInstance != null && MpMenuState.MpCanvasGroup != null)
                StartTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine,
                    FadeAndHide(MpMenuState.MpCanvasGroup, MpMenuState.MpContainer, 0.15f));
            else
                SetCanvasVisible(MpMenuState.MpCanvasGroup, MpMenuState.MpContainer, false);

            if (MpMenuState.MainContainer != null)
                MpMenuState.MainContainer.SetActive(showMainMenu);
            if (MpMenuState.MainFooterText != null)
                MpMenuState.MainFooterText.gameObject.SetActive(showMainMenu);

            var sf = MpMenuState.SelectionField;
            if (restoreMainMenuSelection && sf != null)
                sf.SetValue(inst, MpMenuState.MainMenuMpIndex >= 0
                    ? MpMenuState.MainMenuMpIndex
                    : MpMenuState.SavedMainSel);

            if (!preserveConnection)
                Plugin.Net.Shutdown();
            Plugin.Log.LogInfo("[Menu] Exited MP menu.");
        }

        /// <summary>
        /// Cancels the current network operation but stays in the lobby.
        /// Order matters: invalidate coroutines first, then shutdown, then unlock UI.
        /// </summary>
        static void CancelNetworkOperation(string status = "Cancelled.")
        {
            _joinGeneration++;        // invalidates any running JoinTimeoutRoutine
            _joinOverlayReady = false;
            _waitingForInvite = false;
            Plugin.Net.Shutdown();
            MpMenuState.InputLocked = false;
            _phase = ComputePhase();
            _statusPhase = _phase;
            _selectedId = PrimaryRow(_phase);
            MpMenuState.SetStatus(status);
            Plugin.Log.LogInfo("[Menu] Network operation cancelled.");
        }

        // ── Actions ──────────────────────────────────────────────────────────

        static bool PreviewHostLobbySelection(SlotSelectScreen inst, bool updateStatus)
        {
            int slotIndex = GetLobbySaveSlotIndex();
            bool player1IsMugman = GetLobbyPlayer1IsMugman();

            SaveSlotSyncPacket packet;
            string reason;
            if (!SlotSelectEnterGamePatch.TryBroadcastLobbySelection(inst, slotIndex, player1IsMugman, out packet, out reason))
            {
                if (updateStatus)
                    MpMenuState.SetStatus(reason);
                return false;
            }

            if (updateStatus)
                MpMenuState.SetStatus(DescribeSave(slotIndex) + ".");

            return true;
        }

        static void OnCycleHostSaveSlot(SlotSelectScreen inst, int direction)
        {
            if (!Plugin.Net.IsConnected || !Plugin.Net.IsHost)
            {
                MpMenuState.SetStatus("Only the host chooses the save.");
                return;
            }

            int nextSlot = GetLobbySaveSlotIndex() + direction;
            if (nextSlot < 0) nextSlot = 2;
            if (nextSlot > 2) nextSlot = 0;

            PlayerData.CurrentSaveFileIndex = nextSlot;
            PreviewHostLobbySelection(inst, updateStatus: true);
        }

        static void OnToggleHostLeadCharacter(SlotSelectScreen inst)
        {
            if (!Plugin.Net.IsConnected || !Plugin.Net.IsHost)
            {
                MpMenuState.SetStatus("Only the host chooses who plays whom.");
                return;
            }

            var data = GetLobbySaveData();
            if (data == null)
            {
                MpMenuState.SetStatus("That save is unavailable right now.");
                return;
            }

            data.isPlayer1Mugman = !data.isPlayer1Mugman;
            if (!PreviewHostLobbySelection(inst, updateStatus: false))
                return;

            MpMenuState.SetStatus("You play " + Title(GetLobbyLeadCharacterName())
                + ", your partner plays " + Title(GetLobbyGuestCharacterName()) + ".");
        }

        static string Title(string upper)
        {
            return string.IsNullOrEmpty(upper) ? upper : upper.Substring(0, 1) + upper.Substring(1).ToLowerInvariant();
        }

        static void OnStartGame(SlotSelectScreen inst)
        {
            if (!Plugin.Net.IsConnected || !Plugin.Net.IsHost)
            {
                MpMenuState.SetStatus("Only the host can start the run.");
                return;
            }

            string reason;
            if (!SlotSelectEnterGamePatch.TryStartFromLobbySelection(
                    inst,
                    GetLobbySaveSlotIndex(),
                    GetLobbyPlayer1IsMugman(),
                    out reason))
            {
                MpMenuState.SetStatus(reason);
                return;
            }

            MpMenuState.InMpMenu = false;
            MpMenuState.InputLocked = false;
            MpMenuState.SetBackPromptVisible(inst, false);
        }

        static void OnCyclePlayerColor(int direction)
        {
            int nextSelection = PlayerColorSync.GetNextSelection(Plugin.PreferredPlayerColorSelection, direction);
            Plugin.SetPreferredPlayerColorSelection(nextSelection);
            if (Plugin.Net != null)
                Plugin.Net.NotifyLocalAppearanceChanged();
        }

        static void OnPlayWithPartner()
        {
            var partner = GetPartner();
            if (partner.Id == 0UL)
            {
                OnHostGame();
                return;
            }

            if (!Plugin.Net.StartHostAndInvite(partner.Id))
            {
                MpMenuState.InputLocked = false;
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus);
                return;
            }

            MpMenuState.InputLocked = true;
            MpMenuState.SetStatus("Opening a lobby for you and " + partner.Name, animate: true);
        }

        static void OnHostGame()
        {
            if (!Plugin.Net.StartHost())
            {
                _waitingForInvite     = false;
                MpMenuState.InputLocked = false;
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus);
                return;
            }

            MpMenuState.InputLocked = true;
        }

        static void OnJoinGame(SlotSelectScreen inst)
        {
            string clipboardRaw;
            string clipboardLobbyId;
            if (!Plugin.Net.IsSteamReady)
            {
                _waitingForInvite      = false;
                MpMenuState.InputLocked = false;
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus);
                return;
            }

            if (TryGetClipboardLobbyId(out clipboardRaw, out clipboardLobbyId))
            {
                string status;
                if (Plugin.Net.TryJoinLobbyById(clipboardRaw, out status))
                {
                    _waitingForInvite = false;
                    _joinOverlayReady = false;
                    MpMenuState.InputLocked = true;
                }
                else
                {
                    MpMenuState.InputLocked = false;
                }
                MpMenuState.SetStatus(status);
                return;
            }

            _joinGeneration++;   // invalidate any previous JoinTimeoutRoutine
            _waitingForInvite = true;
            MpMenuState.InputLocked = true;

            if (Plugin.AutoOpenSteamFriends)
            {
                string overlayStatus;
                if (Plugin.Net.OpenFriendsOverlay(out overlayStatus))
                {
                    _lastOverlayOpenTime = Time.time;
                    MpMenuState.SetStatus(overlayStatus, animate: true);
                }
                else
                {
                    _waitingForInvite = false;
                    MpMenuState.InputLocked = false;
                    MpMenuState.SetStatus(overlayStatus);
                    return;
                }
            }
            else
            {
                MpMenuState.SetStatus("Waiting for a Steam invite. Shift+Tab opens Steam", animate: true);
            }

            if (MpMenuState.ScreenInstance != null)
                MpMenuState.ScreenInstance.StartCoroutine(JoinTimeoutRoutine(_joinGeneration));
        }

        static void OnInviteFriend()
        {
            string status;
            Plugin.Net.OpenInviteDialog(out status);
            MpMenuState.SetStatus(status);
        }

        static void OnRetryLast()
        {
            string status;
            if (Plugin.Net.TryRetryLastAction(out status))
            {
                _waitingForInvite = false;
                _joinOverlayReady = false;
                MpMenuState.InputLocked = true;
            }
            MpMenuState.SetStatus(status);
        }

        static void OnCopyLobbyId()
        {
            string status;
            Plugin.Net.TryCopyLobbyId(out status);
            MpMenuState.SetStatus(status);
        }

        static void OnExportBugReport()
        {
            string folder = BugReportExporter.Export();
            GUIUtility.systemCopyBuffer = folder;
            MpMenuState.SetStatus("Bug report saved. Its folder is on your clipboard.");
        }

        // ── Join timeout + second-press overlay flow ──────────────────────────

        static IEnumerator JoinTimeoutRoutine(int gen)
        {
            const float TIMEOUT = 12f;
            float waited = 0f;

            while (waited < TIMEOUT && MpMenuState.InMpMenu && !Plugin.Net.IsConnected)
            {
                // Invalidated by cancel or a new join op — abort silently
                if (gen != _joinGeneration) yield break;
                if (!_waitingForInvite) yield break;

                // Invite arrived mid-wait — net callbacks handle UI
                if (Plugin.Net.IsInputLocked || Plugin.Net.IsConnected)
                {
                    _waitingForInvite = false;
                    _joinOverlayReady = false;
                    yield break;
                }
                yield return null;
                waited += Time.deltaTime;
            }

            // Bail if cancelled, exited, or already connected
            if (gen != _joinGeneration || !MpMenuState.InMpMenu || Plugin.Net.IsConnected)
                yield break;

            _waitingForInvite = false;
            MpMenuState.InputLocked = false;
            MpMenuState.ClearStatus();
            _joinOverlayReady = true;
        }

        static bool HandleJoinAccept()
        {
            if (!_joinOverlayReady) return false;
            if (!Plugin.Net.IsSteamReady)
            {
                _joinOverlayReady       = false;
                _waitingForInvite       = false;
                MpMenuState.InputLocked = false;
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus);
                return true;
            }

            // Spam guard: don't re-open overlay within 2 s
            if (Time.time - _lastOverlayOpenTime < 2f) return true; // swallow input, no re-open

            _joinOverlayReady     = false;
            _lastOverlayOpenTime  = Time.time;
            _waitingForInvite     = true;
            MpMenuState.InputLocked = true;
            string status;
            if (Plugin.Net.OpenFriendsOverlay(out status))
            {
                MpMenuState.SetStatus(status, animate: true);
                if (MpMenuState.ScreenInstance != null)
                    MpMenuState.ScreenInstance.StartCoroutine(JoinTimeoutRoutine(++_joinGeneration));
            }
            else
            {
                _waitingForInvite = false;
                MpMenuState.InputLocked = false;
                MpMenuState.SetStatus(status);
            }
            return true;
        }

        // ── Coroutines ────────────────────────────────────────────────────────

        static IEnumerator FadeCanvas(CanvasGroup cg, float from, float to, float duration)
        {
            if (cg == null) yield break;
            float elapsed = 0f;
            cg.alpha = from;
            cg.interactable = to > 0f;
            cg.blocksRaycasts = to > 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                cg.alpha = Mathf.Lerp(from, to, elapsed / duration);
                yield return null;
            }
            cg.alpha = to;
            cg.interactable = to > 0f;
            cg.blocksRaycasts = to > 0f;
        }

        static IEnumerator FadeAndHide(CanvasGroup cg, GameObject go, float duration)
        {
            if (cg == null)
            {
                if (go != null) go.SetActive(false);
                yield break;
            }

            yield return FadeCanvas(cg, cg.alpha, 0f, duration);
            SetCanvasVisible(cg, go, false);
        }

        // ── Audio ─────────────────────────────────────────────────────────────

        static readonly MethodInfo _audioPlay =
            System.Type.GetType("AudioManager, Assembly-CSharp")
                  ?.GetMethod("Play",
                              BindingFlags.Public | BindingFlags.Static,
                              null, new[] { typeof(string) }, null);

        static void PlayMenuSound(string name)
        {
            try { _audioPlay?.Invoke(null, new object[] { name }); }
            catch { }
        }
    }
}
