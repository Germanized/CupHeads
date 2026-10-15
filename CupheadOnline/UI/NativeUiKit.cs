using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CupheadOnline.UI
{
    /// <summary>Which of the game's typefaces a label uses.</summary>
    public enum FontRole
    {
        /// <summary>CupheadVogue ExtraBold — menu items, names, headings.</summary>
        Vogue,
        /// <summary>CupheadMemphis Medium — subtitles, prompts, small print.</summary>
        Memphis,
    }

    /// <summary>
    /// Builds mod screens out of Cuphead's own UI assets so they read as part of
    /// the game: the printed paper cards from the options / pause menus, the
    /// slot-select box, the character heads, the Vogue and Memphis typefaces and
    /// the exact ink / red colours the game's menus use.
    ///
    /// The card textures carry their small print ("MDHR, INC., Options") baked
    /// in. A clean copy is made once (that line painted over with the paper
    /// above it) and 9-sliced, and screens print their own small print on it.
    /// Everything falls back to the generated CupheadUiTheme look when an asset
    /// is not loaded.
    /// </summary>
    public static class NativeUiKit
    {
        // Colours sampled from the game's menus (OptionsGUI, slot select).
        public static readonly Color Ink = new Color(0.21f, 0.21f, 0.21f, 1f);
        public static readonly Color InkSoft = new Color(0.21f, 0.21f, 0.21f, 0.72f);
        public static readonly Color InkFaint = new Color(0.21f, 0.21f, 0.21f, 0.36f);
        public static readonly Color Red = new Color(0.68f, 0.11f, 0.11f, 1f);
        public static readonly Color RedFaint = new Color(0.68f, 0.11f, 0.11f, 0.45f);
        public static readonly Color GoodInk = new Color(0.20f, 0.42f, 0.20f, 1f);
        public static readonly Color WarnInk = new Color(0.62f, 0.38f, 0.05f, 1f);

        // ── Fonts ─────────────────────────────────────────────────────────────

        static Font _vogue;
        static Font _memphis;
        static float _nextFontLookup;

        /// <summary>The menu typeface (CupheadVogue ExtraBold).</summary>
        public static Font Vogue
        {
            get
            {
                ResolveFonts();
                return _vogue ?? CupheadUiTheme.MenuFont;
            }
        }

        /// <summary>The slot-subtitle / prompt typeface (CupheadMemphis Medium).</summary>
        public static Font Memphis
        {
            get
            {
                ResolveFonts();
                return _memphis ?? Vogue;
            }
        }

        static void ResolveFonts()
        {
            if (_vogue != null && _memphis != null)
                return;
            if (Time.unscaledTime < _nextFontLookup)
                return;
            _nextFontLookup = Time.unscaledTime + 1f;

            Font[] fonts;
            try { fonts = Resources.FindObjectsOfTypeAll<Font>(); }
            catch { return; }

            if (_vogue == null)
                _vogue = Pick(fonts, "CupheadVogue-ExtraBold-merged", "CupheadVogue-ExtraBold", "CupheadVogue-Bold-merged", "CupheadVogue-Bold");
            if (_memphis == null)
                _memphis = Pick(fonts, "CupheadMemphis-Medium-merged", "CupheadMemphis-Medium");
        }

        public static Font FontFor(FontRole role)
        {
            return role == FontRole.Vogue ? Vogue : Memphis;
        }

        static bool RoleResolved(FontRole role)
        {
            return role == FontRole.Vogue ? _vogue != null : _memphis != null;
        }

        // Labels created before the game's fonts were loaded (overlays are built
        // on the title screen) get their real typeface as soon as it appears.
        static readonly List<KeyValuePair<Text, FontRole>> _pendingFonts = new List<KeyValuePair<Text, FontRole>>();

        /// <summary>Per frame (cheap when nothing is pending).</summary>
        public static void Tick()
        {
            if (_pendingFonts.Count == 0)
                return;

            ResolveFonts();
            for (int i = _pendingFonts.Count - 1; i >= 0; i--)
            {
                var entry = _pendingFonts[i];
                if (entry.Key == null)
                {
                    _pendingFonts.RemoveAt(i);
                    continue;
                }
                if (!RoleResolved(entry.Value))
                    continue;
                entry.Key.font = FontFor(entry.Value);
                _pendingFonts.RemoveAt(i);
            }
        }

        static Font Pick(Font[] fonts, params string[] names)
        {
            foreach (var name in names)
            {
                for (int i = 0; i < fonts.Length; i++)
                {
                    if (fonts[i] != null && string.Equals(fonts[i].name, name, StringComparison.OrdinalIgnoreCase))
                        return fonts[i];
                }
            }
            return null;
        }

        // ── Sprites ───────────────────────────────────────────────────────────

        static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        static readonly Dictionary<string, float> _spriteMissAt = new Dictionary<string, float>();

        /// <summary>A loaded game sprite by exact name, or null (a miss is retried at most every 2 s).</summary>
        public static Sprite FindSprite(string name)
        {
            Sprite cached;
            if (_spriteCache.TryGetValue(name, out cached) && cached != null)
                return cached;

            float missAt;
            if (_spriteMissAt.TryGetValue(name, out missAt) && Time.unscaledTime - missAt < 2f)
                return null;
            _spriteMissAt[name] = Time.unscaledTime;

            Sprite[] all;
            try { all = Resources.FindObjectsOfTypeAll<Sprite>(); }
            catch { return null; }

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == name)
                {
                    _spriteCache[name] = all[i];
                    return all[i];
                }
            }
            return null;
        }

        static Sprite _bigCard;
        static Sprite _smallCard;

        /// <summary>The options card (595 x 524 native) without its small print, 9-sliced.</summary>
        public static Sprite BigCard
        {
            get
            {
                if (_bigCard == null)
                    _bigCard = BuildCleanCard("options_bg", eraseX: 24, eraseY: 30, eraseW: 544, eraseH: 42, border: 46f);
                return _bigCard;
            }
        }

        /// <summary>The pause card (512 x 296 native) without its small print, 9-sliced.</summary>
        public static Sprite SmallCard
        {
            get
            {
                if (_smallCard == null)
                    _smallCard = BuildCleanCard("pause_menu", eraseX: 20, eraseY: 24, eraseW: 472, eraseH: 36, border: 40f);
                return _smallCard;
            }
        }

        // ── Save-slot boxes ───────────────────────────────────────────────────
        // Read from the game's own SlotSelectScreenSlot so the seats match the
        // save screen exactly: the plain ruled box, and the selected box in
        // Cuphead red / Mugman green with its film-noise overlay.

        static bool _slotLookResolved;
        static Sprite _slotPlain, _slotCuphead, _slotMugman;
        static Sprite _noisePlain, _noiseCuphead, _noiseMugman;
        static Color _slotSelectedText = new Color(0.96f, 0.93f, 0.86f, 1f);

        public static Sprite SlotBox(bool occupied, bool mugman)
        {
            ResolveSlotLook();
            if (!occupied) return _slotPlain;
            return mugman ? (_slotMugman ?? _slotCuphead) : _slotCuphead;
        }

        public static Sprite SlotNoise(bool occupied, bool mugman)
        {
            ResolveSlotLook();
            if (!occupied) return _noisePlain;
            return mugman ? (_noiseMugman ?? _noiseCuphead) : _noiseCuphead;
        }

        /// <summary>Text colour on a selected (filled) save box.</summary>
        public static Color SlotSelectedText
        {
            get { ResolveSlotLook(); return _slotSelectedText; }
        }

        static void ResolveSlotLook()
        {
            if (_slotLookResolved)
                return;

            SlotSelectScreenSlot slot = null;
            try
            {
                var all = Resources.FindObjectsOfTypeAll<SlotSelectScreenSlot>();
                if (all != null && all.Length > 0)
                    slot = all[0];
            }
            catch { }
            if (slot == null)
                return;

            _slotPlain = Field<Sprite>(slot, "unselectedBoxSprite");
            _slotCuphead = Field<Sprite>(slot, "selectedBoxSprite");
            _slotMugman = Field<Sprite>(slot, "selectedBoxSpriteMugman");
            _noisePlain = Field<Sprite>(slot, "unselectedNoise");
            _noiseCuphead = Field<Sprite>(slot, "selectedNoise");
            _noiseMugman = Field<Sprite>(slot, "selectedNoiseMugman");
            var selectedText = Field<Color>(slot, "selectedTextColor");
            if (selectedText.a > 0f)
                _slotSelectedText = selectedText;
            _slotLookResolved = _slotPlain != null || _slotCuphead != null;
        }

        static T Field<T>(object target, string name)
        {
            try
            {
                var field = HarmonyLib.AccessTools.Field(target.GetType(), name);
                if (field != null)
                {
                    object value = field.GetValue(target);
                    if (value is T)
                        return (T)value;
                }
            }
            catch { }
            return default(T);
        }

        /// <summary>The character-select head for Cuphead or Mugman.</summary>
        public static Sprite Head(bool mugman)
        {
            return FindSprite(mugman ? "player_select_mugman_idle_0001" : "player_select_cuphead_idle_0001");
        }

        /// <summary>
        /// Copies a standalone card texture, paints over its printed small print
        /// with the paper just above it (feathered so no seam shows) and returns
        /// a 9-sliced sprite. Null if the card is not loaded.
        /// </summary>
        static Sprite BuildCleanCard(string spriteName, int eraseX, int eraseY, int eraseW, int eraseH, float border)
        {
            var source = FindSprite(spriteName);
            if (source == null || source.texture == null)
                return null;

            try
            {
                var tex = source.texture;
                var rect = source.textureRect;
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                var previous = RenderTexture.active;
                RenderTexture.active = rt;

                int w = (int)rect.width;
                int h = (int)rect.height;
                var copy = new Texture2D(w, h, TextureFormat.ARGB32, false);
                copy.ReadPixels(new Rect(rect.x, rect.y, w, h), 0, 0);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);

                // Texture rows run bottom-up: the small print sits eraseY..eraseY+eraseH.
                int donorY = eraseY + eraseH + 6;
                const int feather = 6;
                var donor = copy.GetPixels(eraseX, donorY, eraseW, eraseH);
                var original = copy.GetPixels(eraseX, eraseY, eraseW, eraseH);
                for (int y = 0; y < eraseH; y++)
                {
                    float edge = Mathf.Min(y, eraseH - 1 - y);
                    float t = Mathf.Clamp01(edge / feather);
                    for (int x = 0; x < eraseW; x++)
                    {
                        int i = y * eraseW + x;
                        original[i] = Color.Lerp(original[i], donor[i], t);
                    }
                }
                copy.SetPixels(eraseX, eraseY, eraseW, eraseH, original);
                copy.wrapMode = TextureWrapMode.Clamp;
                copy.filterMode = FilterMode.Bilinear;
                copy.Apply(false, true);
                copy.hideFlags = HideFlags.HideAndDontSave;

                var sprite = Sprite.Create(copy, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f, 0,
                    SpriteMeshType.FullRect, new Vector4(border, border, border, border));
                sprite.name = spriteName + "_clean";
                sprite.hideFlags = HideFlags.HideAndDontSave;
                return sprite;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[NativeUi] Could not prepare card '" + spriteName + "': " + ex.Message);
                return null;
            }
        }

        // ── Builders ──────────────────────────────────────────────────────────

        public static RectTransform MakeRect(GameObject parent, string name, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>
        /// A printed paper card with its small print reading
        /// "(c) 1930,  MDHR, INC., {word}  PRINTED IN U.S.A.".
        /// </summary>
        public static Image MakeCard(GameObject parent, string name, Vector2 pos, Vector2 size, bool big, string word)
        {
            var rt = MakeRect(parent, name, pos, size);
            var image = rt.gameObject.AddComponent<Image>();
            var sprite = big ? BigCard : SmallCard;
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
                image.color = Color.white;
            }
            else
            {
                CupheadUiTheme.StyleCard(image, dark: false);
            }

            if (!string.IsNullOrEmpty(word))
                PrintSmallPrint(rt.gameObject, word, size.x, big ? 52f : 41f);
            return image;
        }

        static void PrintSmallPrint(GameObject card, string word, float width, float baselineFromBottom)
        {
            var color = new Color(Ink.r, Ink.g, Ink.b, 0.85f);
            float half = width * 0.5f;

            // Anchored to the card's bottom edge, so it stays put if the card grows.
            var left = MakeText(card, "SmallPrintLeft", FontRole.Memphis, 14, color, TextAnchor.MiddleLeft,
                Vector2.zero, new Vector2(160f, 20f));
            BottomAnchor(left, new Vector2(-half + 40f + 80f, baselineFromBottom));
            left.text = "© 1930,";

            // A narrow card has no room for the "printed in" line; the maker's
            // line then sits right of centre, clear of the date.
            bool narrow = width < 520f;
            var centre = MakeText(card, "SmallPrintCentre", FontRole.Memphis, 15, color, TextAnchor.MiddleCenter,
                Vector2.zero, new Vector2(width - 260f, 20f));
            BottomAnchor(centre, new Vector2(narrow ? 28f : 0f, baselineFromBottom));
            centre.text = "MDHR, INC., " + word;

            if (narrow)
                return;

            var right = MakeText(card, "SmallPrintRight", FontRole.Memphis, 7, color, TextAnchor.MiddleRight,
                Vector2.zero, new Vector2(160f, 20f));
            BottomAnchor(right, new Vector2(half - 40f - 80f, baselineFromBottom));
            right.text = "P R I N T E D   I N   U .  S .  A .";
        }

        static void BottomAnchor(Text text, Vector2 pos)
        {
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = text.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
        }

        /// <summary>Anchors a rect to the top centre of its parent, offset downwards.</summary>
        public static void TopAnchor(RectTransform rt, float x, float yFromTop)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(x, -yFromTop);
        }

        public static Text MakeText(GameObject parent, string name, FontRole role, int size, Color color,
            TextAnchor alignment, Vector2 pos, Vector2 sizeDelta)
        {
            var rt = MakeRect(parent, name, pos, sizeDelta);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = FontFor(role);
            if (!RoleResolved(role))
                _pendingFonts.Add(new KeyValuePair<Text, FontRole>(text, role));
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.supportRichText = true;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        public static Image MakeImage(GameObject parent, string name, Sprite sprite, Vector2 pos, Vector2 size)
        {
            var rt = MakeRect(parent, name, pos, size);
            var image = rt.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.raycastTarget = false;
            if (sprite != null && sprite.border != Vector4.zero)
                image.type = Image.Type.Sliced;
            return image;
        }

        /// <summary>
        /// Puts the native card on an Image if it is loaded yet. Overlays built
        /// before the card art was available call this from Update, so they pick
        /// it up as soon as it is (a cheap reference check otherwise).
        /// </summary>
        public static void ApplyCard(Image image, bool big)
        {
            if (image == null) return;
            var sprite = big ? _bigCard : _smallCard;
            if (sprite == null)
                sprite = big ? BigCard : SmallCard;
            if (sprite == null || image.sprite == sprite) return;
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.color = Color.white;
        }

        /// <summary>Three rising bars, like a telegraph signal meter.</summary>
        public static Image[] MakeSignal(GameObject parent, Vector2 pos, float scale = 1f)
        {
            var bars = new Image[3];
            for (int i = 0; i < 3; i++)
            {
                float h = (6f + i * 5f) * scale;
                var bar = MakeImage(parent, "SignalBar" + i, null,
                    pos + new Vector2((i * 7f - 7f) * scale, (h - 16f * scale) * 0.5f),
                    new Vector2(5f * scale, h));
                bar.color = InkFaint;
                bars[i] = bar;
            }
            return bars;
        }

        /// <summary>Colours the bars for a ping: 3 green, 2 ochre, 1 red, none while measuring.</summary>
        public static void SetSignal(Image[] bars, int pingMs, bool connected)
        {
            if (bars == null) return;
            int lit = !connected ? 0 : pingMs <= 0 ? 0 : pingMs < 80 ? 3 : pingMs < 150 ? 2 : 1;
            Color on = lit == 3 ? GoodInk : lit == 2 ? WarnInk : Red;
            for (int i = 0; i < bars.Length; i++)
            {
                if (bars[i] != null)
                    bars[i].color = i < lit ? on : InkFaint;
            }
        }

        /// <summary>"great" / "okay" / "laggy" for a ping.</summary>
        public static string PingWord(int pingMs)
        {
            return pingMs < 80 ? "great" : pingMs < 150 ? "okay" : "laggy";
        }

        /// <summary>A screen-space overlay canvas laid out at 1920 x 1080.</summary>
        public static GameObject MakeOverlayCanvas(string name, int sortingOrder)
        {
            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return go;
        }

        /// <summary>A solid swatch with a thin ink edge, for player colours.</summary>
        public static Image MakeSwatch(GameObject parent, string name, Vector2 pos, float size)
        {
            var edge = MakeImage(parent, name, null, pos, new Vector2(size, size));
            edge.color = Ink;
            var fill = MakeImage(edge.gameObject, "Fill", null, Vector2.zero, new Vector2(size - 4f, size - 4f));
            fill.color = Color.white;
            return fill;
        }
    }
}
