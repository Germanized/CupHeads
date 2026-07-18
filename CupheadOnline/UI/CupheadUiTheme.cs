using System;
using UnityEngine;
using UnityEngine.UI;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Shared visual language for every CupHeads overlay so the mod UI reads as
    /// part of Cuphead's 1930s print style instead of default engine UI.
    ///
    /// - MenuFont resolves one of the game's own loaded fonts (Vogue / Memphis /
    ///   Felix / Primer) at runtime, falling back to Arial only when nothing else
    ///   is loaded yet. Panels can call RefreshLabelFonts each frame cheaply; the
    ///   lookup result is cached.
    /// - Card sprites are generated once: an aged-paper or dark-sepia fill inside
    ///   a rounded double ink rule, 9-sliced so any panel size keeps crisp
    ///   borders.
    /// </summary>
    public static class CupheadUiTheme
    {
        // ── Palette ───────────────────────────────────────────────────────────
        public static readonly Color Ink        = new Color(0.13f, 0.09f, 0.06f, 1f);   // near-black brown
        public static readonly Color Paper      = new Color(0.93f, 0.87f, 0.73f, 0.97f); // aged cream
        public static readonly Color PaperShade = new Color(0.86f, 0.78f, 0.62f, 1f);
        public static readonly Color DarkFill   = new Color(0.10f, 0.07f, 0.05f, 0.88f); // sepia slate
        public static readonly Color Gold       = new Color(0.85f, 0.68f, 0.38f, 1f);
        public static readonly Color GoldSoft   = new Color(0.85f, 0.68f, 0.38f, 0.55f);
        public static readonly Color Cream      = new Color(0.96f, 0.92f, 0.80f, 1f);
        public static readonly Color CreamSoft  = new Color(0.88f, 0.84f, 0.72f, 0.92f);
        public static readonly Color PosterRed  = new Color(0.72f, 0.20f, 0.14f, 1f);

        // ── Font ──────────────────────────────────────────────────────────────
        static Font _menuFont;
        static float _nextFontLookupAt;

        static readonly string[] FontPreference =
        {
            "vogue-extrabold",
            "vogue-bold",
            "vogue",
            "memphis",
            "felix",
            "primer",
        };

        /// <summary>
        /// One of Cuphead's own fonts when available; Arial only as a last
        /// resort. Safe to call every frame — a real lookup runs at most once a
        /// second until a game font is found.
        /// </summary>
        public static Font MenuFont
        {
            get
            {
                if (_menuFont != null && IsGameFont(_menuFont))
                    return _menuFont;

                if (Time.unscaledTime >= _nextFontLookupAt)
                {
                    _nextFontLookupAt = Time.unscaledTime + 1f;
                    var found = FindGameFont();
                    if (found != null)
                        _menuFont = found;
                }

                if (_menuFont == null)
                    _menuFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

                return _menuFont;
            }
        }

        static bool IsGameFont(Font font)
        {
            return font != null && !font.name.Equals("Arial", StringComparison.OrdinalIgnoreCase);
        }

        static Font FindGameFont()
        {
            Font[] fonts;
            try { fonts = Resources.FindObjectsOfTypeAll<Font>(); }
            catch { return null; }

            if (fonts == null || fonts.Length == 0)
                return null;

            foreach (var wanted in FontPreference)
            {
                for (int i = 0; i < fonts.Length; i++)
                {
                    var font = fonts[i];
                    if (font == null || string.IsNullOrEmpty(font.name))
                        continue;

                    if (font.name.ToLowerInvariant().Contains(wanted))
                        return font;
                }
            }

            for (int i = 0; i < fonts.Length; i++)
            {
                var font = fonts[i];
                if (IsGameFont(font))
                    return font;
            }

            return null;
        }

        /// <summary>
        /// Re-applies the current MenuFont to every Text under root. Cheap when
        /// the font has not changed; lets panels created before the game fonts
        /// loaded upgrade themselves.
        /// </summary>
        public static void RefreshLabelFonts(GameObject root)
        {
            if (root == null)
                return;

            var font = MenuFont;
            if (font == null)
                return;

            var labels = root.GetComponentsInChildren<Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                if (labels[i] != null && labels[i].font != font)
                    labels[i].font = font;
            }
        }

        // ── Card sprites ──────────────────────────────────────────────────────
        static Sprite _darkCard;
        static Sprite _paperCard;

        public static Sprite DarkCard
        {
            get
            {
                if (_darkCard == null)
                    _darkCard = BuildCardSprite(
                        fill: DarkFill,
                        fillEdge: new Color(0.07f, 0.05f, 0.03f, 0.92f),
                        rule: Gold,
                        innerRule: GoldSoft);
                return _darkCard;
            }
        }

        public static Sprite PaperCard
        {
            get
            {
                if (_paperCard == null)
                    _paperCard = BuildCardSprite(
                        fill: Paper,
                        fillEdge: PaperShade,
                        rule: Ink,
                        innerRule: new Color(Ink.r, Ink.g, Ink.b, 0.45f));
                return _paperCard;
            }
        }

        /// <summary>Turns an Image into a vintage card panel.</summary>
        public static void StyleCard(Image image, bool dark = true)
        {
            if (image == null)
                return;

            image.sprite = dark ? DarkCard : PaperCard;
            image.type = Image.Type.Sliced;
            image.color = Color.white;
        }

        static Sprite BuildCardSprite(Color fill, Color fillEdge, Color rule, Color innerRule)
        {
            const int size = 64;
            const float cornerRadius = 13f;
            const float ruleInset = 3.5f;     // outer ink rule centreline
            const float ruleWidth = 2.1f;
            const float innerInset = 8.5f;    // second, thinner rule
            const float innerWidth = 1.2f;

            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.hideFlags = HideFlags.HideAndDontSave;

            var pixels = new Color[size * size];
            float half = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Signed distance to the rounded-rect edge (negative inside)
                    float px = Mathf.Abs(x + 0.5f - half) - (half - cornerRadius);
                    float py = Mathf.Abs(y + 0.5f - half) - (half - cornerRadius);
                    float outside = Mathf.Sqrt(
                        Mathf.Max(px, 0f) * Mathf.Max(px, 0f)
                        + Mathf.Max(py, 0f) * Mathf.Max(py, 0f));
                    float dist = outside + Mathf.Min(Mathf.Max(px, py), 0f) - cornerRadius;
                    float depth = -dist; // >0 inside, grows toward centre

                    Color colour;
                    if (depth <= 0f)
                    {
                        colour = new Color(rule.r, rule.g, rule.b, 0f);
                    }
                    else
                    {
                        // Edge shading: slightly darker paper near the border
                        float edgeBlend = Mathf.Clamp01((depth - 2f) / 10f);
                        colour = Color.Lerp(fillEdge, fill, edgeBlend);

                        float outerRule = Mathf.Clamp01(1f - (Mathf.Abs(depth - ruleInset) - ruleWidth * 0.5f) / 0.9f);
                        float innerRuleBlend = Mathf.Clamp01(1f - (Mathf.Abs(depth - innerInset) - innerWidth * 0.5f) / 0.9f);
                        if (outerRule > 0f)
                            colour = Color.Lerp(colour, rule, outerRule);
                        else if (innerRuleBlend > 0f)
                            colour = Color.Lerp(colour, innerRule, innerRuleBlend * innerRule.a);

                        // Anti-alias the outer silhouette
                        colour.a *= Mathf.Clamp01(depth / 1.2f);
                    }

                    pixels[y * size + x] = colour;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply(false, false);

            var sprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(20f, 20f, 20f, 20f));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        // ── Label helper ──────────────────────────────────────────────────────
        public static Text MakeLabel(
            GameObject parent,
            string content,
            int size,
            Color color,
            Vector2 anchoredPosition,
            Vector2 sizeDelta,
            TextAnchor alignment,
            Vector2 anchor)
        {
            var go = new GameObject("Label_" + content);
            go.transform.SetParent(parent.transform, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = sizeDelta;

            var text = go.AddComponent<Text>();
            text.text = content;
            text.font = MenuFont;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;

            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.55f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return text;
        }
    }
}
