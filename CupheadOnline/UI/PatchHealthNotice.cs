using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CupheadOnline.UI
{
    /// <summary>
    /// In-game warning card shown on menu scenes when any Harmony hook failed to
    /// apply — a half-patched install used to fail silently and freeze co-op.
    /// </summary>
    public static class PatchHealthNotice
    {
        static GameObject _root;
        static CanvasGroup _group;
        static Text _body;

        public static void Tick()
        {
            if (Plugin.FailedPatchCount == 0)
                return;

            EnsureUi();
            if (_group == null)
                return;

            string scene;
            try { scene = SceneManager.GetActiveScene().name ?? string.Empty; }
            catch { scene = string.Empty; }

            bool menuScene = scene.Equals("scene_title", System.StringComparison.OrdinalIgnoreCase)
                          || scene.Equals("scene_slot_select", System.StringComparison.OrdinalIgnoreCase)
                          || scene.Equals("scene_start", System.StringComparison.OrdinalIgnoreCase);

            _group.alpha = menuScene ? 1f : 0f;
            if (!menuScene)
                return;

            CupheadUiTheme.RefreshLabelFonts(_root);

            if (_body != null)
            {
                _body.text = Plugin.FailedPatchCount + " MOD HOOK(S) FAILED TO APPLY\n"
                    + "Multiplayer may misbehave. A game update or a stale\n"
                    + "mod build is the usual cause. Press F9 to copy a report.";
            }
        }

        static void EnsureUi()
        {
            if (_root != null)
                return;

            _root = new GameObject("CupHeads_PatchHealthNotice");
            Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 165;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var card = new GameObject("Card");
            card.transform.SetParent(_root.transform, false);
            var rt = card.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(18f, 18f);
            rt.sizeDelta = new Vector2(430f, 110f);
            CupheadUiTheme.StyleCard(card.AddComponent<Image>(), dark: true);

            _group = card.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            CupheadUiTheme.MakeLabel(
                card, "! MOD HEALTH WARNING !", 15, CupheadUiTheme.PosterRed,
                new Vector2(0f, 36f), new Vector2(400f, 22f),
                TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f));

            _body = CupheadUiTheme.MakeLabel(
                card, string.Empty, 12, CupheadUiTheme.Cream,
                new Vector2(0f, -14f), new Vector2(400f, 62f),
                TextAnchor.UpperCenter, new Vector2(0.5f, 0.5f));
            _body.lineSpacing = 1.1f;
        }
    }
}
