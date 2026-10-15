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
        static Image _card;
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

            NativeUiKit.ApplyCard(_card, false);
            if (_body != null)
            {
                _body.text = Plugin.FailedPatchCount + " mod hook(s) failed to apply, so multiplayer may misbehave. "
                    + "A game update or an old mod build is the usual cause. Press F9 to copy a report.";
            }
        }

        static void EnsureUi()
        {
            if (_root != null)
                return;

            _root = NativeUiKit.MakeOverlayCanvas("CupHeads_PatchHealthNotice", 165);

            _card = NativeUiKit.MakeCard(_root, "Card", Vector2.zero, new Vector2(500f, 176f), false, "Notice");
            var rt = _card.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(28f, -28f);

            _group = _card.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            var title = NativeUiKit.MakeText(_card.gameObject, "Title", FontRole.Vogue, 26, NativeUiKit.Red,
                TextAnchor.MiddleCenter, Vector2.zero, new Vector2(460f, 32f));
            NativeUiKit.TopAnchor(title.rectTransform, 0f, 22f);
            title.text = "MOD HEALTH WARNING";

            _body = NativeUiKit.MakeText(_card.gameObject, "Body", FontRole.Memphis, 18, NativeUiKit.Ink,
                TextAnchor.UpperCenter, Vector2.zero, new Vector2(440f, 80f));
            NativeUiKit.TopAnchor(_body.rectTransform, 0f, 58f);
            _body.lineSpacing = 0.95f;
        }
    }
}
