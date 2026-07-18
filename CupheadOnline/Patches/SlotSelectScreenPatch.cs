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
    //    sentinel 99  → MULTIPLAYER (opens MP sub-menu)
    //    sentinel 100 → CREDITS     (opens Credits panel)
    //
    //  MultiplayerMenuChild
    //    ├─ Layout (VerticalLayoutGroup + ContentSizeFitter)
    //    │    ├─ HOST GAME  (sentinel 100)
    //    │    ├─ JOIN GAME  (sentinel 101)
    //    │    └─ BACK       (sentinel 102)
    //    ├─ StatusText    (animated dots while waiting)
    //    └─ PresenceText  (live lobby member list)
    //
    //  CreditsPanel
    //    └─ Layout (VerticalLayoutGroup)
    //         ├─ TitleText  ("CREDITS")
    //         ├─ BodyText   (content)
    //         └─ HintText   ("Press B / Back to return")
    //
    //  Prefix skips original UpdateMainMenu while InMpMenu or InCredits.
    // ────────────────────────────────────────────────────────────────────────────

    // ── MP state ─────────────────────────────────────────────────────────────────

    internal static class MpMenuState
    {
        internal const int HostIndex       = 0;
        internal const int JoinIndex       = 1;
        internal const int ColorIndex      = 2;
        internal const int InviteIndex     = 3;
        internal const int RetryIndex      = 4;
        internal const int CopyLobbyIndex  = 5;
        internal const int DiagnosticsIndex= 6;
        internal const int BackIndex       = 7;

        internal static bool             InMpMenu;
        internal static int              MpSelection;
        internal static int              SavedMainSel;
        internal static int              MainMenuMpIndex;
        internal static int              MainMenuCreditsIndex;
        internal static bool             InputLocked;        // blocks Accept during critical states

        internal static GameObject       MainContainer;
        internal static GameObject       MpContainer;
        internal static RectTransform    MpLayoutRoot;
        internal static CanvasGroup      MpCanvasGroup;
        internal static Text[]           MpItems;
        internal static Text             SteamBadgeText;
        internal static Text             StatusText;
        internal static Text             PresenceText;
        internal static Text             HintText;
        internal static Text             BackHintText;
        internal static Text             MainFooterText;
        internal static Text             ColorDetailText;
        internal static Text[]           ColorSwatches;
        internal static SlotSelectScreen ScreenInstance;

        // Status animation
        internal static string           StatusBase  = "";
        internal static bool             StatusAnimate;

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
        internal static readonly FieldInfo SlotSelectionField =
            typeof(SlotSelectScreen).GetField("_slotSelection", BF);
        internal static readonly FieldInfo SlotsField =
            typeof(SlotSelectScreen).GetField("slots", BF);
        internal static readonly FieldInfo SelectedColorField =
            typeof(SlotSelectScreen).GetField("mainMenuSelectedColor", BF);
        internal static readonly FieldInfo UnselectedColorField =
            typeof(SlotSelectScreen).GetField("mainMenuUnselectedColor", BF);
        internal static readonly MethodInfo SetStateMethod =
            typeof(SlotSelectScreen).GetMethod("SetState",
                BF, null, new[] { typeof(SlotSelectScreen.State) }, null);
        internal static readonly MethodInfo GetButtonDownMethod =
            typeof(SlotSelectScreen).GetMethod("GetButtonDown",
                BF, null, new[] { typeof(CupheadButton) }, null);

        internal static void Reset()
        {
            InMpMenu       = false;
            MpSelection    = 0;
            SavedMainSel   = 0;
            MainMenuMpIndex = -1;
            MainMenuCreditsIndex = -1;
            InputLocked    = false;
            MainContainer  = null;
            MpContainer    = null;
            MpLayoutRoot   = null;
            MpCanvasGroup  = null;
            MpItems        = null;
            SteamBadgeText = null;
            StatusText     = null;
            PresenceText   = null;
            HintText       = null;
            BackHintText   = null;
            MainFooterText = null;
            ColorDetailText = null;
            ColorSwatches = null;
            ScreenInstance = null;
            StatusBase     = "";
            StatusAnimate  = false;
        }

        internal static void SetStatus(string msg, bool animate = false)
        {
            StatusBase    = msg;
            StatusAnimate = animate;
            if (StatusText != null) StatusText.text = msg;
        }

        internal static SlotSelectScreen Resolve(SlotSelectScreen inst) => inst ?? ScreenInstance;

        internal static Color SelColor(SlotSelectScreen inst)
        {
            var target = Resolve(inst);
            if (SelectedColorField == null || target == null) return Color.white;

            try { return (Color)SelectedColorField.GetValue(target); }
            catch { return Color.white; }
        }

        internal static Color UnselColor(SlotSelectScreen inst)
        {
            var target = Resolve(inst);
            if (UnselectedColorField == null || target == null) return Color.grey;

            try { return (Color)UnselectedColorField.GetValue(target); }
            catch { return Color.grey; }
        }

        internal static bool Btn(SlotSelectScreen inst, CupheadButton b)
        {
            var target = Resolve(inst);
            if (GetButtonDownMethod == null || target == null) return false;

            try { return (bool)GetButtonDownMethod.Invoke(target, new object[] { b }); }
            catch { return false; }
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
    //  AWAKE PATCH — builds MP container + Credits panel, extends arrays
    // ────────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(SlotSelectScreen), "Awake")]
    public static class SlotSelectAwakePatch
    {
        static void Postfix(SlotSelectScreen __instance)
        {
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

            // ── Build MultiplayerMenuChild ────────────────────────────────────
            var mpRoot = BuildPanel("MultiplayerMenuChild", containerParent,
                                    MpMenuState.MainContainer.GetComponent<RectTransform>());
            var mpCg   = mpRoot.AddComponent<CanvasGroup>();
            mpCg.alpha = 0f;
            mpRoot.SetActive(false);
            MpMenuState.MpContainer   = mpRoot;
            MpMenuState.MpCanvasGroup = mpCg;

            var sepiaFill = new Color(0.08f, 0.07f, 0.05f, 0.58f);
            var sepiaOutline = new Color(0.78f, 0.62f, 0.34f, 0.42f);
            var softText = new Color(0.90f, 0.88f, 0.82f, 0.96f);
            var subtleText = new Color(0.72f, 0.70f, 0.66f, 0.88f);
            const float fullWidth = 820f;
            const float sideCardWidth = 398f;
            const float sideCardHeight = 400f;
            const float sideGap = 24f;
            const float sideContentWidth = 338f;
            float sideCardX = (sideCardWidth + sideGap) * 0.5f;

            var headerCard = BuildCard(mpRoot, "LobbyHeaderCard",
                new Vector2(0f, 228f), new Vector2(fullWidth, 68f),
                new Color(0.09f, 0.07f, 0.05f, 0.66f), sepiaOutline);
            var actionCard = BuildCard(mpRoot, "LobbyActionCard",
                new Vector2(-sideCardX, -12f), new Vector2(sideCardWidth, sideCardHeight),
                sepiaFill, sepiaOutline);
            var rosterCard = BuildCard(mpRoot, "LobbyRosterCard",
                new Vector2(sideCardX, -12f), new Vector2(sideCardWidth, sideCardHeight),
                sepiaFill, sepiaOutline);
            var statusCard = BuildCard(mpRoot, "LobbyStatusCard",
                new Vector2(0f, -290f), new Vector2(fullWidth, 92f),
                new Color(0.09f, 0.07f, 0.05f, 0.62f), sepiaOutline);

            var lobbyTitleGO = BuildText(headerCard, "LobbyTitle",
                new Vector2(-170f, 10f), new Vector2(420f, 34f),
                sample.font, sample.fontSize + 3,
                new Color(0.97f, 0.93f, 0.84f, 0.98f));
            var lobbyTitleText = lobbyTitleGO.GetComponent<Text>();
            if (lobbyTitleText != null)
            {
                lobbyTitleText.text = "MULTIPLAYER LOBBY";
                lobbyTitleText.alignment = TextAnchor.MiddleLeft;
            }

            var subtitleGO = BuildText(headerCard, "LobbySubtitle",
                new Vector2(-138f, -14f), new Vector2(484f, 24f),
                sample.font, Mathf.Max(12, sample.fontSize - 7),
                subtleText);
            var subtitleText = subtitleGO.GetComponent<Text>();
            if (subtitleText != null)
            {
                subtitleText.text = "Host, queue friends, sync saves, and launch together.";
                subtitleText.alignment = TextAnchor.MiddleLeft;
            }

            var badgeGO = BuildText(headerCard, "SteamBadgeText",
                new Vector2(222f, 10f), new Vector2(320f, 28f),
                sample.font, Mathf.Max(13, sample.fontSize - 5),
                new Color(0.95f, 0.85f, 0.40f, 0.95f));
            MpMenuState.SteamBadgeText = badgeGO.GetComponent<Text>();
            if (MpMenuState.SteamBadgeText != null)
                MpMenuState.SteamBadgeText.alignment = TextAnchor.MiddleRight;

            var actionTitleGO = BuildText(actionCard, "ActionTitle",
                new Vector2(0f, 162f), new Vector2(sideContentWidth, 24f),
                sample.font, Mathf.Max(14, sample.fontSize - 5), softText);
            var actionTitleText = actionTitleGO.GetComponent<Text>();
            if (actionTitleText != null)
            {
                actionTitleText.text = "ACTIONS";
                actionTitleText.alignment = TextAnchor.MiddleLeft;
            }

            var rosterTitleGO = BuildText(rosterCard, "RosterTitle",
                new Vector2(0f, 162f), new Vector2(sideContentWidth, 24f),
                sample.font, Mathf.Max(14, sample.fontSize - 5), softText);
            var rosterTitleText = rosterTitleGO.GetComponent<Text>();
            if (rosterTitleText != null)
            {
                rosterTitleText.text = "ROSTER";
                rosterTitleText.alignment = TextAnchor.MiddleLeft;
            }

            var mpLayout = BuildLayout(actionCard, new Vector2(0f, -50f), 2f, sideContentWidth);
            MpMenuState.MpLayoutRoot = mpLayout.GetComponent<RectTransform>();
            var hostGO     = CloneItem(exitGO, mpLayout, "HOST GAME");
            var joinGO     = CloneItem(exitGO, mpLayout, "JOIN GAME");
            var colorGO    = CloneItem(exitGO, mpLayout, PlayerColorSync.BuildLocalMenuLabel());
            var inviteGO   = CloneItem(exitGO, mpLayout, "INVITE FRIEND");
            var retryGO    = CloneItem(exitGO, mpLayout, "RETRY LAST");
            var copyLobbyGO= CloneItem(exitGO, mpLayout, "COPY LOBBY ID");
            var diagGO     = CloneItem(exitGO, mpLayout, "COPY DIAGNOSTICS");
            BuildSpacer(mpLayout, 12f);
            var backGO     = CloneItem(exitGO, mpLayout, "BACK");

            int actionFontSize = Mathf.Max(13, sample.fontSize - 8);
            StyleActionItem(hostGO, sample.font, actionFontSize);
            StyleActionItem(joinGO, sample.font, actionFontSize);
            StyleActionItem(colorGO, sample.font, actionFontSize);
            StyleActionItem(inviteGO, sample.font, actionFontSize);
            StyleActionItem(retryGO, sample.font, actionFontSize);
            StyleActionItem(copyLobbyGO, sample.font, actionFontSize);
            StyleActionItem(diagGO, sample.font, actionFontSize);
            StyleActionItem(backGO, sample.font, actionFontSize);

            MpMenuState.MpItems = new[]
            {
                hostGO.GetComponent<Text>(),
                joinGO.GetComponent<Text>(),
                colorGO.GetComponent<Text>(),
                inviteGO.GetComponent<Text>(),
                retryGO.GetComponent<Text>(),
                copyLobbyGO.GetComponent<Text>(),
                diagGO.GetComponent<Text>(),
                backGO.GetComponent<Text>(),
            };

            var colorTitleGO = BuildText(actionCard, "ColorTitle",
                new Vector2(0f, -138f), new Vector2(sideContentWidth, 20f),
                sample.font, Mathf.Max(12, sample.fontSize - 7), softText);
            var colorTitleText = colorTitleGO.GetComponent<Text>();
            if (colorTitleText != null)
            {
                colorTitleText.text = "SWATCHES";
                colorTitleText.alignment = TextAnchor.MiddleLeft;
            }

            var swatches = new Text[PlayerColorSync.SelectionCount];
            for (int i = 0; i < swatches.Length; i++)
            {
                float x = i == PlayerColorSync.AutoSelection
                    ? -122f
                    : -60f + ((i - 1) * 34f);
                float width = i == PlayerColorSync.AutoSelection ? 56f : 28f;
                var swatchGO = BuildText(actionCard, "ColorSwatch" + i,
                    new Vector2(x, -164f), new Vector2(width, 24f),
                    sample.font, i == PlayerColorSync.AutoSelection ? Mathf.Max(11, sample.fontSize - 8) : Mathf.Max(18, sample.fontSize - 3),
                    Color.white);
                var swatchText = swatchGO.GetComponent<Text>();
                if (swatchText != null)
                {
                    swatchText.text = i == PlayerColorSync.AutoSelection ? "AUTO" : "\u25A0";
                    swatchText.alignment = TextAnchor.MiddleCenter;
                    swatchText.alignByGeometry = true;
                    swatchText.lineSpacing = 1f;
                    var outline = swatchGO.GetComponent<Outline>() ?? swatchGO.AddComponent<Outline>();
                    outline.effectColor = new Color(0f, 0f, 0f, 0.32f);
                    outline.effectDistance = new Vector2(1f, -1f);
                }
                swatches[i] = swatchText;
            }
            MpMenuState.ColorSwatches = swatches;

            var colorDetailGO = BuildText(actionCard, "ColorDetailText",
                new Vector2(0f, -194f), new Vector2(sideContentWidth, 24f),
                sample.font, Mathf.Max(11, sample.fontSize - 8), subtleText);
            MpMenuState.ColorDetailText = colorDetailGO.GetComponent<Text>();
            if (MpMenuState.ColorDetailText != null)
            {
                MpMenuState.ColorDetailText.alignment = TextAnchor.MiddleLeft;
                MpMenuState.ColorDetailText.horizontalOverflow = HorizontalWrapMode.Wrap;
                MpMenuState.ColorDetailText.verticalOverflow = VerticalWrapMode.Truncate;
                MpMenuState.ColorDetailText.lineSpacing = 1f;
            }

            var presenceGO = BuildText(rosterCard, "PresenceText",
                new Vector2(0f, -10f), new Vector2(sideContentWidth, 296f),
                sample.font, Mathf.Max(13, sample.fontSize - 6),
                new Color(0.86f, 0.86f, 0.84f, 0.92f));
            MpMenuState.PresenceText = presenceGO.GetComponent<Text>();
            if (MpMenuState.PresenceText != null)
            {
                MpMenuState.PresenceText.alignment = TextAnchor.UpperLeft;
                MpMenuState.PresenceText.lineSpacing = 1.08f;
                MpMenuState.PresenceText.horizontalOverflow = HorizontalWrapMode.Wrap;
                MpMenuState.PresenceText.verticalOverflow = VerticalWrapMode.Truncate;
            }

            var statusGO = BuildText(statusCard, "StatusText",
                new Vector2(0f, 18f), new Vector2(760f, 34f),
                sample.font, Mathf.Max(13, sample.fontSize - 6),
                new Color(0.95f, 0.90f, 0.78f, 1f));
            MpMenuState.StatusText = statusGO.GetComponent<Text>();
            if (MpMenuState.StatusText != null)
                MpMenuState.StatusText.alignment = TextAnchor.MiddleLeft;

            var mpHintGO = BuildText(statusCard, "MpHintText",
                new Vector2(-112f, -16f), new Vector2(520f, 24f),
                sample.font, Mathf.Max(12, sample.fontSize - 7),
                new Color(0.66f, 0.66f, 0.63f, 0.88f));
            MpMenuState.HintText = mpHintGO.GetComponent<Text>();
            if (MpMenuState.HintText != null)
            {
                MpMenuState.HintText.alignment = TextAnchor.MiddleLeft;
                MpMenuState.HintText.horizontalOverflow = HorizontalWrapMode.Wrap;
                MpMenuState.HintText.verticalOverflow = VerticalWrapMode.Truncate;
            }

            var mpBackHintGO = BuildText(statusCard, "MpBackHintText",
                new Vector2(270f, -16f), new Vector2(220f, 24f),
                sample.font, Mathf.Max(12, sample.fontSize - 7),
                new Color(0.68f, 0.68f, 0.68f, 0.82f));
            MpMenuState.BackHintText = mpBackHintGO.GetComponent<Text>();
            if (MpMenuState.BackHintText != null)
            {
                MpMenuState.BackHintText.text = "Escape / Controller B: Back";
                MpMenuState.BackHintText.alignment = TextAnchor.MiddleRight;
            }

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
                    hostGO.GetComponent<Text>(),      "HOST GAME",
                    inviteGO.GetComponent<Text>(),    "INVITE FRIEND",
                    diagGO.GetComponent<Text>(),      "COPY DIAGNOSTICS",
                    backGO.GetComponent<Text>(),      "BACK",
                    titleT,                           "CREDITS"));
            }
            else
            {
                __instance.StartCoroutine(EnforceLabels(
                    mpLabelGO.GetComponent<Text>(),   "MULTIPLAYER",
                    hostGO.GetComponent<Text>(),      "HOST GAME",
                    inviteGO.GetComponent<Text>(),    "INVITE FRIEND",
                    diagGO.GetComponent<Text>(),      "COPY DIAGNOSTICS",
                    backGO.GetComponent<Text>(),      "BACK",
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

        static Sprite _whiteSprite;

        static Sprite GetWhiteSprite()
        {
            if (_whiteSprite != null)
                return _whiteSprite;

            var tex = Texture2D.whiteTexture;
            _whiteSprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f));
            _whiteSprite.name = "CupHeadsWhiteSprite";
            return _whiteSprite;
        }

        static GameObject BuildCard(
            GameObject parent,
            string name,
            Vector2 pos,
            Vector2 size,
            Color fill,
            Color outlineColor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            // Shared vintage card: fill/outline args are kept for call-site
            // compatibility, but the double-ruled card sprite carries the look.
            var image = go.AddComponent<Image>();
            CupheadUiTheme.StyleCard(image, dark: true);

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.18f);
            shadow.effectDistance = new Vector2(0f, -3f);

            return go;
        }

        static GameObject BuildLayout(GameObject parent, Vector2 offset, float spacing = 24f, float width = 304f)
        {
            var go   = new GameObject("Layout");
            go.transform.SetParent(parent.transform, false);
            var rt   = go.AddComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0.5f, 1f);
            rt.anchorMax        = new Vector2(0.5f, 1f);
            rt.pivot            = new Vector2(0.5f, 1f);
            rt.anchoredPosition = offset;
            rt.sizeDelta        = new Vector2(width, 0f);
            var vlg  = go.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment        = TextAnchor.UpperLeft;
            vlg.spacing               = spacing;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight= false;
            vlg.childControlWidth     = true;
            vlg.childControlHeight    = true;
            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            return go;
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

        static void StyleActionItem(GameObject go, Font font, int fontSize)
        {
            if (go == null)
                return;

            var text = go.GetComponent<Text>();
            if (text != null)
            {
                text.font = font;
                text.fontSize = fontSize;
                text.alignment = TextAnchor.MiddleLeft;
                text.resizeTextForBestFit = false;
                text.lineSpacing = 1f;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
            }

            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.enabled = true;
            le.ignoreLayout = false;
            le.preferredHeight = 30f;
            le.minHeight = 30f;
            le.flexibleHeight = 0f;
            le.preferredWidth = 332f;
            le.minWidth = 332f;

            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(le.preferredWidth, le.preferredHeight);
                rt.anchoredPosition = Vector2.zero;
            }
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

        static void BuildSpacer(GameObject parent, float height)
        {
            var go = new GameObject("Spacer");
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<RectTransform>();
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.preferredWidth  = 1f;
        }

        internal static void RefreshMpLayout()
        {
            Canvas.ForceUpdateCanvases();

            if (MpMenuState.MpLayoutRoot != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(MpMenuState.MpLayoutRoot);

            if (MpMenuState.MpContainer != null)
            {
                var rt = MpMenuState.MpContainer.GetComponent<RectTransform>();
                if (rt != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            }
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
        static bool      _joinOverlayReady;
        static bool      _waitingForInvite;
        static float     _lastOverlayOpenTime = -999f;
        static float     _lastBackTime        = -999f;
        static int       _joinGeneration;
        static Coroutine _mpFadeRoutine;
        static Coroutine _creditsFadeRoutine;
        static Coroutine _dotsRoutine;
        static string    _lastPresence;

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

        static void HideMpMenuImmediate()
        {
            MpMenuState.InMpMenu    = false;
            MpMenuState.InputLocked = false;
            _joinOverlayReady       = false;
            _waitingForInvite       = false;
            if (Plugin.Net != null) Plugin.Net.OnOverlayClosed -= HandleOverlayClosed;
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine);
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _dotsRoutine);
            SetCanvasVisible(MpMenuState.MpCanvasGroup, MpMenuState.MpContainer, false);
        }

        static void HandleOverlayClosed()
        {
            if (!_waitingForInvite) return;
            Plugin.Log.LogInfo("[Menu] Steam overlay closed — cancelling join wait.");
            CancelNetworkOperation("Overlay closed. Select an option.");
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

            if (LobbyScreen.Instance != null) return;
            UpdateMainFooterText();

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
            if (Plugin.Net == null)
            {
                MpMenuState.MainFooterText.text = footer;
                return;
            }

            if (Plugin.Net.IsConnected)
            {
                string sessionHint = SessionSync.GetFooterHint();
                if (!string.IsNullOrEmpty(sessionHint))
                    footer += "  |  " + sessionHint;
            }
            else if (Plugin.Net.IsInLobby)
            {
                string sessionHint = SessionSync.GetFooterHint();
                if (!string.IsNullOrEmpty(sessionHint))
                    footer += "  |  " + sessionHint;
            }
            else if (!Plugin.Net.IsSteamReady)
            {
                footer += "  |  Steam unavailable outside Steam";
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

            HideMpMenuImmediate();
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

        static bool TryGetClipboardLobbyId(out string rawText, out string lobbyId)
        {
            rawText = GUIUtility.systemCopyBuffer;
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

        static void SetMpItemLabel(int index, string label)
        {
            if (MpMenuState.MpItems == null || index < 0 || index >= MpMenuState.MpItems.Length)
                return;

            var item = MpMenuState.MpItems[index];
            if (item != null && item.text != label)
                item.text = label;
        }

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

        static string BuildLobbySaveSlotLabel()
        {
            return "SAVE SLOT: " + (GetLobbySaveSlotIndex() + 1);
        }

        static string BuildLobbyCharacterLabel()
        {
            return "LEAD: " + GetLobbyLeadCharacterName();
        }

        static void UpdateDynamicMenuLabels()
        {
            string clipboardRaw;
            string clipboardLobbyId;

            SetMpItemLabel(
                MpMenuState.HostIndex,
                Plugin.Net.IsConnected
                    ? (Plugin.Net.IsHost
                        ? BuildLobbySaveSlotLabel()
                        : (SessionSync.HasTrackedSave ? "HOST SAVE: " + (GetLobbySaveSlotIndex() + 1) : "WAIT FOR HOST"))
                    : "HOST GAME");
            SetMpItemLabel(
                MpMenuState.JoinIndex,
                Plugin.Net.IsConnected
                    ? (Plugin.Net.IsHost
                        ? BuildLobbyCharacterLabel()
                        : (SessionSync.HasTrackedSave ? "WAIT FOR HOST" : "REQUEST HOST SAVE"))
                    : _joinOverlayReady
                        ? "OPEN FRIENDS"
                        : TryGetClipboardLobbyId(out clipboardRaw, out clipboardLobbyId)
                            ? "JOIN CLIPBOARD"
                            : "JOIN GAME");
            SetMpItemLabel(MpMenuState.ColorIndex, PlayerColorSync.BuildLocalMenuLabel());
            SetMpItemLabel(
                MpMenuState.InviteIndex,
                Plugin.Net.IsConnected
                    ? (Plugin.Net.IsHost ? "SEND RESYNC" : "REQUEST RESYNC")
                    : "INVITE FRIEND");
            SetMpItemLabel(
                MpMenuState.RetryIndex,
                Plugin.Net.IsConnected && Plugin.Net.IsHost
                    ? "START GAME"
                    : Plugin.Net.GetRetryActionLabel());
            SetMpItemLabel(MpMenuState.CopyLobbyIndex, "COPY LOBBY ID");
            SetMpItemLabel(MpMenuState.DiagnosticsIndex, "EXPORT BUG REPORT");
            SetMpItemLabel(MpMenuState.BackIndex,
                Plugin.Net.IsConnected || Plugin.Net.IsInLobby ? "DISCONNECT" : "BACK");
        }

        static void UpdateSteamBadge()
        {
            if (MpMenuState.SteamBadgeText == null) return;

            string badge = Plugin.Net.GetSteamBadgeText();
            MpMenuState.SteamBadgeText.text = "[ " + badge + " ]";

            if (badge.IndexOf("OFFLINE", System.StringComparison.OrdinalIgnoreCase) >= 0
             || badge.IndexOf("NOT VIA STEAM", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MpMenuState.SteamBadgeText.color = new Color(0.92f, 0.42f, 0.35f, 0.95f);
            }
            else if (badge.IndexOf("OVERLAY", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MpMenuState.SteamBadgeText.color = new Color(0.95f, 0.72f, 0.30f, 0.95f);
            }
            else if (badge.IndexOf("CONNECTED", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MpMenuState.SteamBadgeText.color = new Color(0.50f, 0.90f, 0.60f, 0.95f);
            }
            else if (badge.IndexOf("IN LOBBY", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MpMenuState.SteamBadgeText.color = new Color(0.55f, 0.82f, 0.95f, 0.95f);
            }
            else
            {
                MpMenuState.SteamBadgeText.color = new Color(0.95f, 0.85f, 0.40f, 0.95f);
            }
        }

        static string GetSelectionHint()
        {
            string clipboardRaw;
            string clipboardLobbyId;

            switch (MpMenuState.MpSelection)
            {
                case MpMenuState.HostIndex:
                    if (Plugin.Net.IsConnected)
                    {
                        if (SessionSync.CompatibilitySeverity >= SessionIssueSeverity.Warning)
                            return SessionSync.CompatibilitySummary;

                        return Plugin.Net.IsHost
                            ? "Choose the save slot for this run here in the lobby. The host can start as soon as a save is selected."
                            : (SessionSync.HasTrackedSave
                                ? "The host picked the current save. You will follow when the host starts."
                                : "Connected. Wait for the host to choose a save slot.");
                    }
                    if (Plugin.Net.IsConnected || Plugin.Net.IsInLobby)
                        return "Leave the current session before starting a fresh host lobby.";
                    return "Create a friends-only Steam lobby for one guest.";

                case MpMenuState.JoinIndex:
                    if (Plugin.Net.IsConnected)
                    {
                        if (Plugin.Net.IsHost)
                            return "Choose whether the host starts as "
                                + GetLobbyLeadCharacterName()
                                + ". The guest automatically becomes "
                                + GetLobbyGuestCharacterName()
                                + ".";

                        if (SessionSync.DesyncSeverity >= SessionIssueSeverity.Warning)
                            return SessionSync.DesyncSummary;
                        if (SessionSync.CompatibilitySeverity >= SessionIssueSeverity.Error)
                            return SessionSync.CompatibilitySummary;

                        if (!SessionSync.HasTrackedSave)
                            return "Ask the host for the current save selection again. This fixes missed lobby sync packets.";

                        return "Waiting for the host to start the selected save.";
                    }
                    if (_joinOverlayReady)
                        return "Open Steam Friends and wait for the host invite.";
                    if (TryGetClipboardLobbyId(out clipboardRaw, out clipboardLobbyId))
                        return "Join lobby #" + clipboardLobbyId + " straight from the clipboard.";
                    if (Plugin.AutoOpenSteamFriends)
                        return "Wait for a Steam invite. The Friends overlay opens automatically.";
                    return "Wait for a Steam invite, or copy a lobby ID to the clipboard to join directly.";

                case MpMenuState.ColorIndex:
                    return "Use Left, Right, or Accept to choose your lobby swatch and in-game tint. Auto keeps the first two gameplay slots classic and gives extra participants stable colors.";

                case MpMenuState.InviteIndex:
                    if (Plugin.Net.IsConnected)
                        return Plugin.Net.IsHost
                            ? "Send a fresh sync bundle and boss-priority burst to the guest."
                            : "Ask the host to resend the current session state.";
                    return Plugin.Net.CanInviteFriend
                        ? "Open Steam's invite dialog for the current lobby."
                        : "Available once you host a lobby.";

                case MpMenuState.RetryIndex:
                    if (Plugin.Net.IsConnected && Plugin.Net.IsHost)
                    {
                        string startReason;
                        return SessionSync.CanHostStartRun(out startReason)
                            ? "Start the run directly from the multiplayer lobby with the selected save and character order."
                            : startReason;
                    }
                    return Plugin.Net.CanRetryLastAction
                        ? "Retry the last host or join action without leaving the menu."
                        : "Becomes available after a host or join attempt.";

                case MpMenuState.CopyLobbyIndex:
                    return Plugin.Net.CanCopyLobbyId
                        ? "Copy the current Steam lobby ID so someone can join from the clipboard."
                        : "Available once a lobby exists.";

                case MpMenuState.DiagnosticsIndex:
                    return "Export a bug report folder with diagnostics, logs, and config files.";

                default:
                    return Plugin.Net.IsConnected || Plugin.Net.IsInLobby
                        ? "Disconnect the current Steam session and return to the main menu."
                        : "Return to the main menu.";
            }
        }

        static void UpdateHintText()
        {
            if (MpMenuState.HintText == null) return;
            MpMenuState.HintText.text = GetSelectionHint();
        }

        static bool IsItemAvailable(int index)
        {
            switch (index)
            {
                case MpMenuState.HostIndex:
                    if (Plugin.Net.IsConnected)
                        return Plugin.Net.IsHost;
                    return !_waitingForInvite
                        && !Plugin.Net.IsInputLocked
                        && !Plugin.Net.IsConnected
                        && !Plugin.Net.IsInLobby;

                case MpMenuState.JoinIndex:
                    if (Plugin.Net.IsConnected)
                        return Plugin.Net.IsHost || !SessionSync.HasTrackedSave;
                    return !_waitingForInvite
                        && !Plugin.Net.IsInputLocked
                        && !Plugin.Net.IsConnected
                        && !Plugin.Net.IsInLobby;

                case MpMenuState.ColorIndex:
                    return true;

                case MpMenuState.InviteIndex:
                    return Plugin.Net.IsConnected ? Plugin.Net.CanRequestRecovery : Plugin.Net.CanInviteFriend;

                case MpMenuState.RetryIndex:
                    if (Plugin.Net.IsConnected)
                        return Plugin.Net.IsHost && SessionSync.HasTrackedSave;
                    return !_waitingForInvite
                        && !Plugin.Net.IsInputLocked
                        && !Plugin.Net.IsConnected
                        && Plugin.Net.CanRetryLastAction;

                case MpMenuState.CopyLobbyIndex:
                    return Plugin.Net.CanCopyLobbyId;

                case MpMenuState.DiagnosticsIndex:
                case MpMenuState.BackIndex:
                    return true;

                default:
                    return false;
            }
        }

        // ── MP menu loop ──────────────────────────────────────────────────────

        static void RunMpMenu(SlotSelectScreen inst)
        {
            if (MpMenuState.MpItems == null) return;

            // Sync InputLocked with live network state each frame.
            // Covers the case where SteamNetManager transitions to a new state
            // (e.g. invite arrives while _joinOverlayReady was pending).
            bool netLocked = Plugin.Net.IsInputLocked || Plugin.Net.IsConnected;
            if (netLocked)
                _waitingForInvite = false;

            bool desiredLock = _waitingForInvite || netLocked;
            if (desiredLock != MpMenuState.InputLocked)
            {
                MpMenuState.InputLocked = desiredLock;
                if (netLocked)
                    _joinOverlayReady = false;
                ApplyColors(inst);
            }

            if (Plugin.Net.IsConnected && Plugin.Net.IsHost && !SessionSync.HasTrackedSave)
                PreviewHostLobbySelection(inst, updateStatus: false);

            if (Plugin.Net.ShouldForceUnlockUi(Time.realtimeSinceStartup))
            {
                Plugin.Log.LogWarning("[Menu] Input-lock watchdog fired - force unlock.");
                CancelNetworkOperation("Operation timed out.\nPlease try again.");
                return;
            }

            // Watchdog: force-unlock if a non-indefinite state has stalled > 15 s.
            // Prevents rare soft-lock if a callback is never fired.
            if (false && MpMenuState.InputLocked
                && !Plugin.Net.IsWaitingIndefinitely
                && !Plugin.Net.IsConnected
                && Time.realtimeSinceStartup - Plugin.Net.StateEnteredTime > 15f)
            {
                Plugin.Log.LogWarning("[Menu] Input-lock watchdog fired — force unlock.");
                MpMenuState.InputLocked = false;
                MpMenuState.SetStatus("Operation timed out.\nPlease try again.", animate: false);
                ApplyColors(inst);
            }

            UpdateDynamicMenuLabels();
            UpdateSteamBadge();
            UpdatePresenceText();
            UpdateHintText();

            int count = MpMenuState.MpItems.Length;
            int prev  = MpMenuState.MpSelection;

            if (MpMenuState.Btn(inst, CupheadButton.MenuDown))
                MpMenuState.MpSelection = (MpMenuState.MpSelection + 1) % count;
            if (MpMenuState.Btn(inst, CupheadButton.MenuUp))
            {
                MpMenuState.MpSelection--;
                if (MpMenuState.MpSelection < 0) MpMenuState.MpSelection = count - 1;
            }

            if (MpMenuState.MpSelection != prev)
            {
                ApplyColors(inst);
                UpdateHintText();
                PlayMenuSound("level_menu_move");
            }

            bool cycleSaveLeft = Plugin.Net.IsConnected
                && Plugin.Net.IsHost
                && MpMenuState.MpSelection == MpMenuState.HostIndex
                && MpMenuState.Btn(inst, CupheadButton.MenuLeft);
            bool cycleSaveRight = Plugin.Net.IsConnected
                && Plugin.Net.IsHost
                && MpMenuState.MpSelection == MpMenuState.HostIndex
                && MpMenuState.Btn(inst, CupheadButton.MenuRight);
            if (cycleSaveLeft || cycleSaveRight)
            {
                OnCycleHostSaveSlot(inst, cycleSaveRight ? 1 : -1);
                PlayMenuSound("level_menu_move");
            }

            bool cycleLeadLeft = Plugin.Net.IsConnected
                && Plugin.Net.IsHost
                && MpMenuState.MpSelection == MpMenuState.JoinIndex
                && MpMenuState.Btn(inst, CupheadButton.MenuLeft);
            bool cycleLeadRight = Plugin.Net.IsConnected
                && Plugin.Net.IsHost
                && MpMenuState.MpSelection == MpMenuState.JoinIndex
                && MpMenuState.Btn(inst, CupheadButton.MenuRight);
            if (cycleLeadLeft || cycleLeadRight)
            {
                OnToggleHostLeadCharacter(inst);
                PlayMenuSound("level_menu_move");
            }

            bool cycleLeft = MpMenuState.MpSelection == MpMenuState.ColorIndex
                && MpMenuState.Btn(inst, CupheadButton.MenuLeft);
            bool cycleRight = MpMenuState.MpSelection == MpMenuState.ColorIndex
                && MpMenuState.Btn(inst, CupheadButton.MenuRight);
            if (cycleLeft || cycleRight)
            {
                OnCyclePlayerColor(cycleRight ? 1 : -1);
                PlayMenuSound("level_menu_move");
            }

            bool cancel = BackPressed(inst);
            bool accept = MpMenuState.Btn(inst, CupheadButton.Accept);

            if (cancel || (accept && MpMenuState.MpSelection == MpMenuState.BackIndex))
            {
                // Debounce: ignore rapid double-presses (prevents double-shutdown)
                if (Time.time - _lastBackTime < 0.2f) return;
                _lastBackTime = Time.time;

                PlayMenuSound("level_menu_confirm");
                ExitMpMenu(inst);
                return;
            }

            if (accept)
            {
                PlayMenuSound("level_menu_confirm");
                switch (MpMenuState.MpSelection)
                {
                    case MpMenuState.HostIndex:
                        if (IsItemAvailable(MpMenuState.HostIndex))
                        {
                            if (Plugin.Net.IsConnected && Plugin.Net.IsHost)
                                OnCycleHostSaveSlot(inst, 1);
                            else
                                OnHostGame();
                        }
                        else
                            MpMenuState.SetStatus(
                                Plugin.Net.IsConnected
                                    ? (Plugin.Net.IsHost
                                        ? "Only the host can choose the multiplayer save."
                                        : "Waiting for the host to choose a save slot.")
                                    : Plugin.Net.IsInLobby
                                        ? "Leave the current session before hosting again."
                                    : "Steam is still busy. Please wait.",
                                animate: false);
                        break;

                    case MpMenuState.JoinIndex:
                        if (IsItemAvailable(MpMenuState.JoinIndex))
                        {
                            if (Plugin.Net.IsConnected)
                            {
                                if (Plugin.Net.IsHost)
                                    OnToggleHostLeadCharacter(inst);
                                else if (!SessionSync.HasTrackedSave)
                                {
                                    string status;
                                    Plugin.Net.TryRequestRecovery(out status);
                                    MpMenuState.SetStatus(status, animate: false);
                                }
                                else
                                    MpMenuState.SetStatus("Waiting for the host to start.", animate: false);
                            }
                            else if (!HandleJoinAccept())
                            {
                                OnJoinGame(inst);
                            }
                        }
                        else
                        {
                            MpMenuState.SetStatus(
                                Plugin.Net.IsConnected
                                    ? (Plugin.Net.IsHost
                                        ? (SessionSync.HasTrackedSave
                                            ? "Start Game is available now."
                                            : "Pick a save first.")
                                        : "Waiting for the host to start.")
                                : _waitingForInvite
                                    ? "Waiting for a Steam invite..."
                                    : Plugin.Net.IsInLobby || Plugin.Net.IsConnected
                                        ? "Leave the current session before joining another lobby."
                                        : "Steam is still busy. Please wait.",
                                animate: false);
                        }
                        break;

                    case MpMenuState.ColorIndex:
                        OnCyclePlayerColor(1);
                        break;

                    case MpMenuState.InviteIndex:
                        if (Plugin.Net.IsConnected)
                        {
                            string status;
                            Plugin.Net.TryRequestRecovery(out status);
                            MpMenuState.SetStatus(status, animate: false);
                        }
                        else
                        {
                            OnInviteFriend();
                        }
                        break;

                    case MpMenuState.RetryIndex:
                        if (Plugin.Net.IsConnected && Plugin.Net.IsHost)
                            OnStartGame(inst);
                        else
                            OnRetryLast();
                        break;

                    case MpMenuState.CopyLobbyIndex:
                        OnCopyLobbyId();
                        break;

                    case MpMenuState.DiagnosticsIndex:
                        OnExportBugReport();
                        break;
                }
            }
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
            MpMenuState.MpSelection  = 0;
            MpMenuState.InMpMenu     = true;
            MpMenuState.InputLocked  = false;
            _joinOverlayReady        = false;
            _waitingForInvite        = false;
            _lastPresence            = null;   // force fresh presence render on entry
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine);
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _dotsRoutine);
            MpMenuState.SetStatus(
                Plugin.Net.IsConnected
                    ? (Plugin.Net.IsHost
                        ? "Guest connected.\nChoose SAVE SLOT and LEAD, then press START GAME."
                        : "Connected.\nWait for the host to choose a save and start.")
                    : Plugin.Net.IsSteamReady ? "Select an option." : Plugin.Net.SteamUnavailableStatus,
                animate: false);
            if (Plugin.Net != null)
                Plugin.Net.NotifyLocalAppearanceChanged();
            if (MpMenuState.PresenceText != null) MpMenuState.PresenceText.text = "";
            if (Plugin.Net.IsConnected && Plugin.Net.IsHost && !SessionSync.HasTrackedSave)
                PreviewHostLobbySelection(inst, updateStatus: false);
            UpdateDynamicMenuLabels();
            UpdateSteamBadge();
            UpdateHintText();
            SlotSelectAwakePatch.RefreshMpLayout();

            ApplyColors(inst);

            if (MpMenuState.ScreenInstance != null)
            {
                StartTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine,
                    FadeCanvas(MpMenuState.MpCanvasGroup, 0f, 1f, 0.2f));
                StartTrackedCoroutine(MpMenuState.ScreenInstance, ref _dotsRoutine, AnimateDots());
            }

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
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _dotsRoutine);
            StopTrackedCoroutine(MpMenuState.ScreenInstance, ref _mpFadeRoutine);

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

            LobbyScreen.Hide();
            if (!preserveConnection)
                Plugin.Net.Shutdown();
            Plugin.Log.LogInfo("[Menu] Exited MP menu.");
        }

        static void OpenHostSaveSelect(SlotSelectScreen inst)
        {
            if (!Plugin.Net.CanOpenSaveSlot)
            {
                MpMenuState.SetStatus("Connect a guest before opening the save slots.", animate: false);
                return;
            }

            ExitMpMenu(inst, preserveConnection: true, restoreMainMenuSelection: false, showMainMenu: false);

            try
            {
                int slotSelection = Mathf.Clamp(PlayerData.CurrentSaveFileIndex, 0, 2);
                if (MpMenuState.SlotSelectionField != null)
                    MpMenuState.SlotSelectionField.SetValue(inst, slotSelection);

                if (MpMenuState.SetStateMethod != null)
                    MpMenuState.SetStateMethod.Invoke(inst, new object[] { SlotSelectScreen.State.SlotSelect });

                var slots = MpMenuState.SlotsField != null
                    ? MpMenuState.SlotsField.GetValue(inst) as SlotSelectScreenSlot[]
                    : null;
                if (slots != null)
                {
                    for (int i = 0; i < slots.Length; i++)
                    {
                        if (slots[i] != null)
                            slots[i].Init(i);
                    }
                }

                Plugin.Log.LogInfo("[Menu] Host opened save slot select.");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Menu] Failed to open save slot select: " + ex.Message);
                if (MpMenuState.MainContainer != null)
                    MpMenuState.MainContainer.SetActive(true);
                MpMenuState.SetStatus("Could not open the save slots.", animate: false);
            }
        }

        /// <summary>
        /// Cancels the current network operation but stays in the MP sub-menu.
        /// Called when Back is pressed while a network op is in flight.
        /// Order matters: invalidate coroutines first, then shutdown, then unlock UI.
        /// </summary>
        static void CancelNetworkOperation(string status = "Select an option.")
        {
            _joinGeneration++;        // invalidates any running JoinTimeoutRoutine
            _joinOverlayReady = false;
            _waitingForInvite = false;
            Plugin.Net.Shutdown();
            MpMenuState.InputLocked = false;
            _lastPresence           = null;   // force presence refresh after cancel
            ApplyColors(null);
            MpMenuState.SetStatus(status, animate: false);
            if (MpMenuState.PresenceText != null) MpMenuState.PresenceText.text = "";
            UpdateDynamicMenuLabels();
            UpdateSteamBadge();
            UpdateHintText();
            Plugin.Log.LogInfo("[Menu] Network operation cancelled.");
        }

        // ── Colors ────────────────────────────────────────────────────────────

        static void ApplyColors(SlotSelectScreen inst)
        {
            if (MpMenuState.MpItems == null) return;
            Color sel    = MpMenuState.SelColor(inst);
            Color unsel  = MpMenuState.UnselColor(inst);
            Color locked = new Color(unsel.r, unsel.g, unsel.b, unsel.a * 0.45f);
            Color dimSel = new Color(sel.r, sel.g, sel.b, sel.a * 0.6f);

            for (int i = 0; i < MpMenuState.MpItems.Length; i++)
            {
                var t = MpMenuState.MpItems[i];
                if (t == null) continue;
                bool available = IsItemAvailable(i);
                t.color = !available
                    ? (i == MpMenuState.MpSelection ? dimSel : locked)
                    : (i == MpMenuState.MpSelection ? sel : unsel);
                t.fontStyle = i == MpMenuState.MpSelection ? FontStyle.Bold : FontStyle.Normal;
            }

            UpdateColorPickerVisuals(inst);
        }

        static void UpdateColorPickerVisuals(SlotSelectScreen inst)
        {
            if (MpMenuState.ColorSwatches == null || MpMenuState.ColorSwatches.Length == 0)
                return;

            int preferredSelection = Plugin.PreferredPlayerColorSelection;
            bool colorMenuFocused = MpMenuState.MpSelection == MpMenuState.ColorIndex;
            Color focusColor = MpMenuState.SelColor(inst);
            Color subtleOutline = new Color(0f, 0f, 0f, 0.18f);

            for (int i = 0; i < MpMenuState.ColorSwatches.Length; i++)
            {
                var swatch = MpMenuState.ColorSwatches[i];
                if (swatch == null)
                    continue;

                bool selected = i == preferredSelection;
                Color baseColor = i == PlayerColorSync.AutoSelection
                    ? new Color(0.90f, 0.86f, 0.76f, selected ? 0.98f : 0.76f)
                    : PlayerColorSync.GetPaletteColor(i);

                if (i != PlayerColorSync.AutoSelection)
                    baseColor.a = selected ? 0.98f : 0.72f;

                swatch.color = baseColor;
                swatch.fontStyle = selected ? FontStyle.Bold : FontStyle.Normal;
                swatch.transform.localScale = selected ? new Vector3(1.08f, 1.08f, 1f) : Vector3.one;

                var outline = swatch.GetComponent<Outline>();
                if (outline != null)
                    outline.effectColor = selected
                        ? (colorMenuFocused ? focusColor : new Color(0.92f, 0.86f, 0.64f, 0.76f))
                        : subtleOutline;
            }

            if (MpMenuState.ColorDetailText != null)
            {
                string detail = "Selected: "
                    + PlayerColorSync.GetSelectionName(preferredSelection).ToUpperInvariant();
                MpMenuState.ColorDetailText.text = detail;
                MpMenuState.ColorDetailText.color = colorMenuFocused
                    ? new Color(0.90f, 0.88f, 0.82f, 0.94f)
                    : new Color(0.72f, 0.70f, 0.66f, 0.88f);
            }
        }

        // ── Presence display ──────────────────────────────────────────────────

        static void UpdatePresenceText()
        {
            if (MpMenuState.PresenceText == null) return;
            string p = Plugin.Net.GetLobbyPresence();
            if (string.IsNullOrEmpty(p))
            {
                string peerName = Plugin.Net.CurrentPeerName;
                if (!string.IsNullOrEmpty(peerName) && peerName != "Unknown Player")
                    p = "Peer: " + peerName + "\nState: " + Plugin.Net.CurrentStateName;
            }

            string peerSummary = Plugin.Net.CurrentPeerSummary;
            if (!string.IsNullOrEmpty(peerSummary))
                p = string.IsNullOrEmpty(p) ? peerSummary : p + "\n" + peerSummary;

            string sessionSummary = SessionSync.GetMenuPresenceSummary();
            if (!string.IsNullOrEmpty(sessionSummary))
                p = string.IsNullOrEmpty(p) ? sessionSummary : p + "\n" + sessionSummary;

            if (string.IsNullOrEmpty(p))
            {
                p = Plugin.Net.IsSteamReady
                    ? "No lobby yet.\n\nHost a game to create a Steam lobby, or join one to see the full party roster here."
                    : "Steam is not ready.\n\nLaunch Cuphead through Steam to populate the lobby roster and use invites.";
            }

            if (p == _lastPresence) return;   // only assign when string actually changes
            _lastPresence = p;
            MpMenuState.PresenceText.text = p;
        }

        // ── Actions ──────────────────────────────────────────────────────────

        static void RefreshLobbySelectionUi(SlotSelectScreen inst)
        {
            _lastPresence = null;
            UpdateDynamicMenuLabels();
            UpdatePresenceText();
            UpdateHintText();
            SlotSelectAwakePatch.RefreshMpLayout();
            ApplyColors(inst);
        }

        static bool PreviewHostLobbySelection(SlotSelectScreen inst, bool updateStatus)
        {
            int slotIndex = GetLobbySaveSlotIndex();
            bool player1IsMugman = GetLobbyPlayer1IsMugman();

            SaveSlotSyncPacket packet;
            string reason;
            if (!SlotSelectEnterGamePatch.TryBroadcastLobbySelection(inst, slotIndex, player1IsMugman, out packet, out reason))
            {
                if (updateStatus)
                    MpMenuState.SetStatus(reason, animate: false);
                return false;
            }

            RefreshLobbySelectionUi(inst);

            if (updateStatus)
            {
                MpMenuState.SetStatus(
                    "Selected save slot "
                    + (slotIndex + 1)
                    + ". The host can start when ready.",
                    animate: false);
            }

            return true;
        }

        static void OnCycleHostSaveSlot(SlotSelectScreen inst, int direction)
        {
            if (!Plugin.Net.IsConnected || !Plugin.Net.IsHost)
            {
                MpMenuState.SetStatus("Connect a guest before choosing the multiplayer save.", animate: false);
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
                MpMenuState.SetStatus("Only the host can choose the character order.", animate: false);
                return;
            }

            var data = GetLobbySaveData();
            if (data == null)
            {
                MpMenuState.SetStatus("That save slot is unavailable right now.", animate: false);
                return;
            }

            data.isPlayer1Mugman = !data.isPlayer1Mugman;
            if (!PreviewHostLobbySelection(inst, updateStatus: false))
                return;

            MpMenuState.SetStatus(
                "Host will play "
                + GetLobbyLeadCharacterName()
                + ". Guest will play "
                + GetLobbyGuestCharacterName()
                + ".",
                animate: false);
        }

        static void OnStartGame(SlotSelectScreen inst)
        {
            if (!Plugin.Net.IsConnected || !Plugin.Net.IsHost)
            {
                MpMenuState.SetStatus("Only the host can start the run.", animate: false);
                return;
            }

            string reason;
            if (!SlotSelectEnterGamePatch.TryStartFromLobbySelection(
                    inst,
                    GetLobbySaveSlotIndex(),
                    GetLobbyPlayer1IsMugman(),
                    out reason))
            {
                MpMenuState.SetStatus(reason, animate: false);
                return;
            }

            MpMenuState.InMpMenu = false;
            MpMenuState.InputLocked = false;
        }

        static void OnCyclePlayerColor(int direction)
        {
            int nextSelection = PlayerColorSync.GetNextSelection(Plugin.PreferredPlayerColorSelection, direction);
            Plugin.SetPreferredPlayerColorSelection(nextSelection);
            if (Plugin.Net != null)
                Plugin.Net.NotifyLocalAppearanceChanged();
            _lastPresence = null;
            UpdateDynamicMenuLabels();
            UpdatePresenceText();
            UpdateHintText();
            SlotSelectAwakePatch.RefreshMpLayout();
            ApplyColors(null);
        }

        static void OnHostGame()
        {
            if (!Plugin.Net.StartHost())
            {
                _waitingForInvite     = false;
                MpMenuState.InputLocked = false;
                ApplyColors(null);
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus, animate: false);
                return;
            }

            MpMenuState.InputLocked = true;
            ApplyColors(null);
        }

        static void OnJoinGame(SlotSelectScreen inst)
        {
            string clipboardRaw;
            string clipboardLobbyId;
            if (!Plugin.Net.IsSteamReady)
            {
                _waitingForInvite      = false;
                MpMenuState.InputLocked = false;
                ApplyColors(null);
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus, animate: false);
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
                    ApplyColors(null);
                    MpMenuState.SetStatus(status, animate: false);
                }
                else
                {
                    MpMenuState.InputLocked = false;
                    ApplyColors(null);
                    MpMenuState.SetStatus(status, animate: false);
                }
                return;
            }

            _joinGeneration++;   // invalidate any previous JoinTimeoutRoutine
            _waitingForInvite = true;
            MpMenuState.InputLocked = true;
            ApplyColors(null);

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
                    ApplyColors(null);
                    MpMenuState.SetStatus(overlayStatus, animate: false);
                    return;
                }
            }
            else
            {
                MpMenuState.SetStatus(
                    "Waiting for a Steam invite...\n"
                    + "Press Shift+Tab to open Steam overlay.",
                    animate: true);
            }

            if (MpMenuState.ScreenInstance != null)
                MpMenuState.ScreenInstance.StartCoroutine(JoinTimeoutRoutine(_joinGeneration));
        }

        static void OnInviteFriend()
        {
            string status;
            Plugin.Net.OpenInviteDialog(out status);
            MpMenuState.SetStatus(status, animate: false);
        }

        static void OnRetryLast()
        {
            string status;
            if (Plugin.Net.TryRetryLastAction(out status))
            {
                _waitingForInvite = false;
                _joinOverlayReady = false;
                MpMenuState.InputLocked = true;
                ApplyColors(null);
                MpMenuState.SetStatus(status, animate: false);
            }
            else
            {
                MpMenuState.SetStatus(status, animate: false);
            }
        }

        static void OnCopyLobbyId()
        {
            string status;
            if (!Plugin.Net.TryCopyLobbyId(out status))
            {
                MpMenuState.SetStatus(status, animate: false);
                return;
            }

            MpMenuState.SetStatus(status, animate: false);
        }

        static void OnExportBugReport()
        {
            string folder = BugReportExporter.Export();
            GUIUtility.systemCopyBuffer = folder;
            MpMenuState.SetStatus("Bug report exported to:\n" + folder, animate: false);
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
            ApplyColors(null);
            MpMenuState.SetStatus(
                "No invite received yet.\n"
                + "Press Join Game again to open your Friends list.",
                animate: false);
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
                ApplyColors(null);
                MpMenuState.SetStatus(Plugin.Net.SteamUnavailableStatus, animate: false);
                return true;
            }

            // Spam guard: don't re-open overlay within 2 s
            if (Time.time - _lastOverlayOpenTime < 2f) return true; // swallow input, no re-open

            _joinOverlayReady     = false;
            _lastOverlayOpenTime  = Time.time;
            _waitingForInvite     = true;
            MpMenuState.InputLocked = true;
            ApplyColors(null);
            string status;
            if (Plugin.Net.OpenFriendsOverlay(out status))
                MpMenuState.SetStatus(status, animate: true);
            else
            {
                _waitingForInvite = false;
                MpMenuState.InputLocked = false;
                ApplyColors(null);
                MpMenuState.SetStatus(status, animate: false);
            }
            return true;
        }

        // ── Coroutines ────────────────────────────────────────────────────────

        static IEnumerator AnimateDots()
        {
            string[] suffixes = { "", ".", "..", "..." };
            int i = 0;
            while (MpMenuState.InMpMenu)
            {
                yield return new WaitForSecondsRealtime(0.35f);
                if (!MpMenuState.InMpMenu) yield break;
                if (MpMenuState.StatusText == null) yield break;

                if (MpMenuState.StatusAnimate)
                {
                    i = (i + 1) % suffixes.Length;
                    MpMenuState.StatusText.text = MpMenuState.StatusBase + suffixes[i];
                }
                else
                {
                    i = 0;
                    MpMenuState.StatusText.text = MpMenuState.StatusBase;
                }
            }
        }

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
