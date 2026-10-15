using UnityEngine;
using UnityEngine.UI;

namespace CupheadOnline.UI
{
    /// <summary>
    /// F1: a printed card listing every CupHeads key, so nobody has to dig
    /// through the README mid-session. Toggle with F1 again (it also closes on
    /// a scene change).
    /// </summary>
    public static class ShortcutsCard
    {
        static readonly string[,] Lines =
        {
            { "F1", "Show or hide this card" },
            { "F6", "Resync with your partner" },
            { "F7", "Boss health bars on / off" },
            { "F8", "Session card on / off" },
            { "F9", "Copy diagnostics" },
            { "F10", "Battle assist on / off" },
            { "F11", "Dev Lab" },
            // The menu font draws "+" as "&", so no plus signs here.
            { "HOLD C", "then 1-4 for quick messages" },
        };

        static GameObject _root;
        static CanvasGroup _group;
        static Image _card;
        static bool _visible;
        static string _scene = string.Empty;

        public static bool Visible => _visible;

        public static void Toggle()
        {
            EnsureUi();
            _visible = !_visible;
        }

        public static void Tick()
        {
            if (_root == null)
                return;

            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;
            if (scene != _scene)
            {
                _scene = scene;
                _visible = false;
            }

            NativeUiKit.ApplyCard(_card, true);
            _group.alpha = Mathf.MoveTowards(_group.alpha, _visible ? 1f : 0f, Time.unscaledDeltaTime * 8f);
        }

        static void EnsureUi()
        {
            if (_root != null)
                return;

            _root = NativeUiKit.MakeOverlayCanvas("CupHeads_Shortcuts", 175);
            _scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;

            int count = Lines.GetLength(0);
            const float rowStep = 40f;
            const float firstRow = 100f;
            // Rows, then room for the printed small print at the bottom.
            float height = firstRow + count * rowStep + 76f;
            _card = NativeUiKit.MakeCard(_root, "Card", Vector2.zero, new Vector2(760f, height), true, "Shortcuts");

            _group = _card.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            var title = NativeUiKit.MakeText(_card.gameObject, "Title", FontRole.Vogue, 34, NativeUiKit.Ink,
                TextAnchor.MiddleCenter, Vector2.zero, new Vector2(680f, 44f));
            NativeUiKit.TopAnchor(title.rectTransform, 0f, 38f);
            title.text = "CUPHEADS SHORTCUTS";

            // Two columns meeting at a gutter left of centre: keys right-aligned
            // into it, descriptions left-aligned out of it.
            const float gutter = -110f;
            for (int i = 0; i < count; i++)
            {
                float y = firstRow + i * rowStep;

                var key = NativeUiKit.MakeText(_card.gameObject, "Key", FontRole.Vogue, 26, NativeUiKit.Red,
                    TextAnchor.MiddleRight, Vector2.zero, new Vector2(180f, 34f));
                NativeUiKit.TopAnchor(key.rectTransform, gutter - 12f - 90f, y);
                key.horizontalOverflow = HorizontalWrapMode.Overflow;
                key.text = Lines[i, 0];

                var what = NativeUiKit.MakeText(_card.gameObject, "What", FontRole.Memphis, 22, NativeUiKit.Ink,
                    TextAnchor.MiddleLeft, Vector2.zero, new Vector2(400f, 34f));
                NativeUiKit.TopAnchor(what.rectTransform, gutter + 12f + 200f, y);
                what.horizontalOverflow = HorizontalWrapMode.Overflow;
                what.text = Lines[i, 1];
            }
        }
    }
}
