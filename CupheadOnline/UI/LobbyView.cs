using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CupheadOnline.UI
{
    /// <summary>One line of the lobby menu.</summary>
    public struct LobbyRow
    {
        public int Id;
        public string Label;
        /// <summary>Set for a left/right picker ("LABEL: VALUE"), null for an action.</summary>
        public string Value;
        public bool Enabled;
        /// <summary>Optional colour chip drawn after a picker's value.</summary>
        public Color? Swatch;
        /// <summary>Extra gap above this row (separates pickers from actions).</summary>
        public bool GapBefore;

        public static LobbyRow Action(int id, string label, bool enabled = true)
        {
            return new LobbyRow { Id = id, Label = label, Enabled = enabled };
        }

        public static LobbyRow Picker(int id, string label, string value, Color? swatch = null)
        {
            return new LobbyRow { Id = id, Label = label, Value = value, Enabled = true, Swatch = swatch };
        }
    }

    /// <summary>What a player seat shows.</summary>
    public struct LobbySeat
    {
        public bool Occupied;
        public bool Mugman;
        public string Title;     // character name, or OPEN SEAT
        public string Name;      // Steam name / "(you)"
        public string Detail;    // role, ping, waiting...
        public Color DetailColor;
        public Color? Swatch;
    }

    /// <summary>
    /// The multiplayer lobby, laid out like Cuphead's own options card: a
    /// printed paper card holding two player seats (styled after the save-slot
    /// boxes), a status line, the menu (Vogue, ink / red selection, pickers
    /// laid out "LABEL: VALUE" like the Visual options) and a hint line.
    /// Pure view: the menu logic lives in SlotSelectScreenPatch.
    /// </summary>
    public sealed class LobbyView
    {
        const float CardWidth = 800f;
        const float CardHeight = 600f;
        const float RowStep = 39f;
        const float RowsTop = 56f;
        const float RowsCentre = -44f;
        const int MaxRows = 7;
        const int RowFontSize = 30;

        public GameObject Root { get; private set; }

        readonly SeatView[] _seats = new SeatView[2];
        readonly RowView[] _rows = new RowView[MaxRows];
        Text _status;
        Text _hint;
        GameObject _rowsRoot;

        sealed class SeatView
        {
            public CanvasGroup Group;
            public Image Box;
            public Image Noise;
            public Image Head;
            public Text Mystery;
            public Text Title;
            public Text Name;
            public Text Detail;
            public Image Swatch;
        }

        sealed class RowView
        {
            public GameObject Go;
            public Text Action;
            public Text Label;
            public Text Value;
            public Image Swatch;
        }

        public static LobbyView Build(Transform parent, RectTransform copyLayoutFrom)
        {
            var view = new LobbyView();
            view.BuildInternal(parent, copyLayoutFrom);
            return view;
        }

        void BuildInternal(Transform parent, RectTransform layout)
        {
            Root = new GameObject("CupHeadsLobby");
            Root.transform.SetParent(parent, false);
            var rootRt = Root.AddComponent<RectTransform>();
            if (layout != null)
            {
                rootRt.anchorMin = layout.anchorMin;
                rootRt.anchorMax = layout.anchorMax;
                rootRt.pivot = layout.pivot;
                rootRt.anchoredPosition = layout.anchoredPosition;
                rootRt.sizeDelta = layout.sizeDelta;
            }

            // The card sits where the options card does, centred on screen.
            var card = NativeUiKit.MakeCard(Root, "Card", new Vector2(0f, 8f), new Vector2(CardWidth, CardHeight), true, "Multiplayer");
            var cardGo = card.gameObject;

            for (int i = 0; i < 2; i++)
                _seats[i] = BuildSeat(cardGo, i == 0 ? -186f : 186f);

            _status = NativeUiKit.MakeText(cardGo, "Status", FontRole.Memphis, 22, NativeUiKit.Ink,
                TextAnchor.MiddleCenter, new Vector2(0f, 112f), new Vector2(CardWidth - 120f, 52f));
            _status.lineSpacing = 0.95f;

            _rowsRoot = NativeUiKit.MakeRect(cardGo, "Rows", Vector2.zero, new Vector2(CardWidth, CardHeight)).gameObject;
            for (int i = 0; i < MaxRows; i++)
                _rows[i] = BuildRow(_rowsRoot, i);

            _hint = NativeUiKit.MakeText(cardGo, "Hint", FontRole.Memphis, 19, NativeUiKit.InkSoft,
                TextAnchor.MiddleCenter, new Vector2(0f, -206f), new Vector2(CardWidth - 140f, 50f));
            _hint.lineSpacing = 0.95f;
        }

        SeatView BuildSeat(GameObject card, float x)
        {
            var seat = new SeatView();
            var rt = NativeUiKit.MakeRect(card, "Seat", new Vector2(x, 196f), new Vector2(352f, 118f));
            seat.Group = rt.gameObject.AddComponent<CanvasGroup>();
            seat.Group.interactable = false;
            seat.Group.blocksRaycasts = false;

            seat.Box = rt.gameObject.AddComponent<Image>();
            seat.Box.raycastTarget = false;
            if (NativeUiKit.SlotBox(false, false) == null)
            {
                // No save-slot art loaded: a plain inked frame instead.
                seat.Box.color = new Color(0f, 0f, 0f, 0f);
                var outline = rt.gameObject.AddComponent<Outline>();
                outline.effectColor = NativeUiKit.Ink;
                outline.effectDistance = new Vector2(1.5f, -1.5f);
            }

            seat.Noise = NativeUiKit.MakeImage(rt.gameObject, "Noise", null, Vector2.zero, new Vector2(352f, 118f));

            seat.Head = NativeUiKit.MakeImage(rt.gameObject, "Head", NativeUiKit.Head(false),
                new Vector2(-118f, 0f), new Vector2(92f, 92f));
            seat.Head.preserveAspect = true;

            seat.Mystery = NativeUiKit.MakeText(rt.gameObject, "Mystery", FontRole.Vogue, 64, NativeUiKit.InkFaint,
                TextAnchor.MiddleCenter, new Vector2(-118f, 2f), new Vector2(92f, 92f));
            seat.Mystery.text = "?";

            seat.Title = NativeUiKit.MakeText(rt.gameObject, "Title", FontRole.Vogue, 27, NativeUiKit.Ink,
                TextAnchor.MiddleLeft, new Vector2(48f, 28f), new Vector2(232f, 34f));
            seat.Title.horizontalOverflow = HorizontalWrapMode.Overflow;

            seat.Name = NativeUiKit.MakeText(rt.gameObject, "Name", FontRole.Memphis, 20, NativeUiKit.Ink,
                TextAnchor.MiddleLeft, new Vector2(48f, -2f), new Vector2(232f, 26f));
            seat.Name.horizontalOverflow = HorizontalWrapMode.Overflow;

            seat.Detail = NativeUiKit.MakeText(rt.gameObject, "Detail", FontRole.Memphis, 17, NativeUiKit.InkSoft,
                TextAnchor.MiddleLeft, new Vector2(48f, -28f), new Vector2(232f, 24f));
            seat.Detail.horizontalOverflow = HorizontalWrapMode.Overflow;

            seat.Swatch = NativeUiKit.MakeSwatch(rt.gameObject, "Swatch", new Vector2(150f, 28f), 16f);
            return seat;
        }

        static RowView BuildRow(GameObject parent, int index)
        {
            var row = new RowView();
            var rt = NativeUiKit.MakeRect(parent, "Row" + index, Vector2.zero, new Vector2(CardWidth - 80f, RowStep));
            row.Go = rt.gameObject;

            row.Action = NativeUiKit.MakeText(row.Go, "Action", FontRole.Vogue, RowFontSize, NativeUiKit.Ink,
                TextAnchor.MiddleCenter, Vector2.zero, new Vector2(CardWidth - 80f, RowStep));
            row.Action.horizontalOverflow = HorizontalWrapMode.Overflow;

            // Pickers: right-aligned label ending at the colon column, value to its right.
            row.Label = NativeUiKit.MakeText(row.Go, "Label", FontRole.Vogue, RowFontSize, NativeUiKit.Ink,
                TextAnchor.MiddleRight, new Vector2(-150f, 0f), new Vector2(300f, RowStep));
            row.Label.horizontalOverflow = HorizontalWrapMode.Overflow;
            row.Value = NativeUiKit.MakeText(row.Go, "Value", FontRole.Vogue, RowFontSize, NativeUiKit.Ink,
                TextAnchor.MiddleLeft, new Vector2(156f, 0f), new Vector2(300f, RowStep));
            row.Value.horizontalOverflow = HorizontalWrapMode.Overflow;

            row.Swatch = NativeUiKit.MakeSwatch(row.Go, "Swatch", Vector2.zero, 22f);
            row.Go.SetActive(false);
            return row;
        }

        // ── Updates ───────────────────────────────────────────────────────────

        public void SetStatus(string text, Color color)
        {
            if (_status == null) return;
            if (_status.text != text) _status.text = text;
            _status.color = color;
        }

        public void SetHint(string text)
        {
            if (_hint != null && _hint.text != text)
                _hint.text = text ?? string.Empty;
        }

        public void SetSeat(int index, LobbySeat info)
        {
            var seat = _seats[index];
            if (seat == null) return;

            // Occupied seats look like a selected save (Cuphead red / Mugman
            // green, cream text); an empty seat is the plain ruled box.
            var box = NativeUiKit.SlotBox(info.Occupied, info.Mugman);
            if (box != null && seat.Box.sprite != box)
            {
                seat.Box.sprite = box;
                seat.Box.type = box.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
                seat.Box.color = Color.white;
            }
            var noise = NativeUiKit.SlotNoise(info.Occupied, info.Mugman);
            seat.Noise.enabled = noise != null;
            if (noise != null && seat.Noise.sprite != noise)
                seat.Noise.sprite = noise;

            var head = NativeUiKit.Head(info.Mugman);
            if (head != null && seat.Head.sprite != head)
                seat.Head.sprite = head;
            seat.Head.enabled = info.Occupied && seat.Head.sprite != null;
            seat.Mystery.enabled = !info.Occupied;

            Color text = info.Occupied ? NativeUiKit.SlotSelectedText : NativeUiKit.InkSoft;
            Color soft = new Color(text.r, text.g, text.b, text.a * 0.82f);
            Set(seat.Title, info.Title, text);
            Set(seat.Name, info.Name, text);
            Set(seat.Detail, info.Detail, info.Occupied ? soft : (info.DetailColor.a > 0f ? info.DetailColor : NativeUiKit.InkSoft));

            bool swatch = info.Swatch.HasValue && info.Occupied;
            seat.Swatch.transform.parent.gameObject.SetActive(swatch);
            if (swatch)
            {
                seat.Swatch.color = info.Swatch.Value;
                float titleWidth = seat.Title.preferredWidth;
                var edge = (RectTransform)seat.Swatch.transform.parent;
                edge.anchoredPosition = new Vector2(48f - 116f + titleWidth + 16f, 28f);
            }
        }

        public void SetRows(IList<LobbyRow> rows, int selected)
        {
            int count = rows == null ? 0 : Mathf.Min(rows.Count, MaxRows);

            // Centre the block between the status line and the hint, so a short
            // menu does not leave a hole under it.
            float height = 0f;
            for (int i = 0; i < count; i++)
                height += RowStep + (rows[i].GapBefore && i > 0 ? RowStep * 0.45f : 0f);
            float y = Mathf.Min(RowsTop, RowsCentre + height * 0.5f - RowStep * 0.5f);
            for (int i = 0; i < MaxRows; i++)
            {
                var view = _rows[i];
                if (i >= count)
                {
                    if (view.Go.activeSelf) view.Go.SetActive(false);
                    continue;
                }

                var row = rows[i];
                if (row.GapBefore && i > 0)
                    y -= RowStep * 0.45f;

                if (!view.Go.activeSelf) view.Go.SetActive(true);
                ((RectTransform)view.Go.transform).anchoredPosition = new Vector2(0f, y);
                y -= RowStep;

                bool isSelected = i == selected;
                Color main = !row.Enabled
                    ? (isSelected ? NativeUiKit.RedFaint : NativeUiKit.InkFaint)
                    : (isSelected ? NativeUiKit.Red : NativeUiKit.Ink);

                bool picker = row.Value != null;
                view.Action.gameObject.SetActive(!picker);
                view.Label.gameObject.SetActive(picker);
                view.Value.gameObject.SetActive(picker);

                if (!picker)
                {
                    Set(view.Action, row.Label, main);
                    view.Swatch.transform.parent.gameObject.SetActive(false);
                    continue;
                }

                // As in the Visual options: the label stays ink, the value turns red.
                Set(view.Label, row.Label + ":", row.Enabled ? NativeUiKit.Ink : NativeUiKit.InkFaint);
                Set(view.Value, row.Value, main);

                bool swatch = row.Swatch.HasValue;
                var edge = view.Swatch.transform.parent.gameObject;
                edge.SetActive(swatch);
                if (swatch)
                {
                    view.Swatch.color = row.Swatch.Value;
                    float valueLeft = 156f - 150f;
                    ((RectTransform)edge.transform).anchoredPosition =
                        new Vector2(valueLeft + view.Value.preferredWidth + 22f, 1f);
                }
            }
        }

        static void Set(Text text, string value, Color color)
        {
            if (text == null) return;
            value = value ?? string.Empty;
            if (text.text != value) text.text = value;
            if (text.color != color) text.color = color;
        }
    }
}
