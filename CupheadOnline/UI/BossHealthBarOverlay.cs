using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Optional battle overlay that reads Cuphead's boss property objects when
    /// available, then falls back to boss-like DamageReceiver HP fields.
    /// </summary>
    public sealed class BossHealthBarOverlay : MonoBehaviour
    {
        private sealed class BarView
        {
            public GameObject Root;
            public RectTransform RootRect;
            public RectTransform TrackRect;
            public RectTransform NameRect;
            public RectTransform ValueRect;
            public Text Name;
            public Text Value;
            public Image Fill;
            public Image LagFill;
            public RectTransform FillRect;
            public RectTransform LagFillRect;
            public float DisplayedRatio;
            public float LagRatio;
            public float LastLoggedRatio = -1f;
            public float LastLoggedAt = -10f;
        }

        private struct BossSnapshot
        {
            public int Key;
            public string Name;
            public float Current;
            public float Total;
        }

        private sealed class PropertyHandle
        {
            public int Key;
            public object Owner;
            public string Name;
            public FieldInfo TotalField;
            public PropertyInfo CurrentProperty;
        }

        private sealed class DamageReceiverHandle
        {
            public int Key;
            public DamageReceiver Receiver;
            public object HpOwner;
            public string Name;
            public FieldInfo HpField;
        }

        public static BossHealthBarOverlay Instance { get; private set; }

        private const int MaxBars = 3;
        private const float ScanInterval = 0.45f;
        private const float DefeatedHoldSeconds = 1.35f;
        private const float MinInterestingHealth = 8f;
        private const float BaseBarWidth = 700f;
        private const float BaseTrackWidth = 664f;
        private const float MinBarWidth = 360f;
        private const float AssistGapPixels = 24f;
        private const float ScreenEdgeGapPixels = 24f;

        private static readonly BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Color Cream = new Color(0.96f, 0.90f, 0.72f, 1f);
        private static readonly Color MutedCream = new Color(0.84f, 0.75f, 0.55f, 0.95f);
        private static readonly Color Backing = new Color(0.08f, 0.045f, 0.025f, 0.78f);
        private static readonly Color BarBack = new Color(0.18f, 0.08f, 0.04f, 0.90f);
        private static readonly Color BarLag = new Color(0.95f, 0.58f, 0.28f, 0.58f);
        private static readonly Color BarFill = new Color(0.82f, 0.13f, 0.10f, 0.96f);
        private static readonly Color BarFillLow = new Color(0.98f, 0.80f, 0.28f, 0.98f);
        private static readonly Color Border = new Color(0.82f, 0.62f, 0.30f, 0.72f);

        private static readonly string[] BossKeywords =
        {
            "boss",
            "baroness",
            "dragon",
            "robot",
            "saltbaker",
            "dice",
            "devil",
            "pirate",
            "train",
            "genie",
            "clown",
            "flower",
            "blimp",
            "bee",
            "queen",
            "king",
            "knight",
            "rook",
            "bishop",
            "pawn",
            "moon",
            "mermaid",
            "briney",
            "howling",
            "glumstone",
            "mortimer",
            "esther",
            "chef",
            "salt",
            "veggies",
            "potato",
            "onion",
            "carrot",
        };

        private static readonly string[] IgnoredHealthOwners =
        {
            "projectile",
            "bullet",
            "tear",
            "parry",
            "fx",
        };

        private static readonly Dictionary<Type, FieldInfo[]> PropertyFieldCache =
            new Dictionary<Type, FieldInfo[]>(128);
        private static readonly Dictionary<Type, FieldInfo> HpFieldCache =
            new Dictionary<Type, FieldInfo>(64);
        private static readonly Dictionary<Type, bool> TimelineBackedHealthCache =
            new Dictionary<Type, bool>(64);
        private static readonly List<BossSnapshot> ScratchSnapshots =
            new List<BossSnapshot>(16);
        private static readonly List<PropertyHandle> ScratchProperties =
            new List<PropertyHandle>(16);
        private static readonly List<DamageReceiverHandle> ScratchDamageReceivers =
            new List<DamageReceiverHandle>(16);
        private static readonly Dictionary<int, float> FallbackMaxHealth =
            new Dictionary<int, float>(32);
        private static readonly Dictionary<int, float> DefeatedUntil =
            new Dictionary<int, float>(32);

        private readonly BarView[] _bars = new BarView[MaxBars];
        private RectTransform _panel;
        private float _nextScanAt;

        public static void Tick()
        {
            if (!Plugin.ShowBossHealthBars || !IsBattleActive())
            {
                Hide();
                return;
            }

            Ensure();
            if (Instance != null)
                Instance.Refresh();
        }

        public static void Reset()
        {
            FallbackMaxHealth.Clear();
            DefeatedUntil.Clear();
            if (Instance != null)
                Instance._nextScanAt = 0f;
        }

        public static void Hide()
        {
            if (Instance == null)
                return;

            Destroy(Instance.gameObject);
            Instance = null;
        }

        private static void Ensure()
        {
            if (Instance != null)
                return;

            var go = new GameObject("CupHeads_BossHealthBars");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<BossHealthBarOverlay>();
        }

        private void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 138;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            gameObject.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("BossHealthPanel");
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.AddComponent<RectTransform>();
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 1f);
            _panel.pivot = new Vector2(0.5f, 1f);
            _panel.anchoredPosition = new Vector2(0f, -22f);
            _panel.sizeDelta = new Vector2(720f, 136f);

            for (int i = 0; i < _bars.Length; i++)
                _bars[i] = CreateBar(i);
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Refresh()
        {
            if (Time.unscaledTime >= _nextScanAt)
            {
                ScanBosses();
                _nextScanAt = Time.unscaledTime + ScanInterval;
            }

            ReadBossSnapshots(ScratchSnapshots);
            int count = Mathf.Min(MaxBars, ScratchSnapshots.Count);
            UpdateResponsiveLayout(count);
            for (int i = 0; i < _bars.Length; i++)
            {
                if (i >= count)
                {
                    _bars[i].Root.SetActive(false);
                    continue;
                }

                ApplyBar(_bars[i], ScratchSnapshots[i]);
            }
        }

        private BarView CreateBar(int index)
        {
            var root = new GameObject("BossBar_" + index);
            root.transform.SetParent(_panel, false);
            var rootRt = root.AddComponent<RectTransform>();
            rootRt.anchorMin = rootRt.anchorMax = new Vector2(0.5f, 1f);
            rootRt.pivot = new Vector2(0.5f, 1f);
            rootRt.anchoredPosition = new Vector2(0f, -index * 42f);
            rootRt.sizeDelta = new Vector2(BaseBarWidth, 34f);

            var bg = root.AddComponent<Image>();
            bg.color = Backing;
            var outline = root.AddComponent<Outline>();
            outline.effectColor = Border;
            outline.effectDistance = new Vector2(1f, -1f);

            var barBgGo = new GameObject("Track");
            barBgGo.transform.SetParent(root.transform, false);
            var barBgRt = barBgGo.AddComponent<RectTransform>();
            barBgRt.anchorMin = barBgRt.anchorMax = new Vector2(0.5f, 0.5f);
            barBgRt.pivot = new Vector2(0.5f, 0.5f);
            barBgRt.anchoredPosition = new Vector2(0f, -6f);
            barBgRt.sizeDelta = new Vector2(BaseTrackWidth, 12f);
            var barBg = barBgGo.AddComponent<Image>();
            barBg.color = BarBack;

            var lagGo = new GameObject("LagFill");
            lagGo.transform.SetParent(barBgGo.transform, false);
            var lagRt = lagGo.AddComponent<RectTransform>();
            lagRt.anchorMin = new Vector2(0f, 0f);
            lagRt.anchorMax = new Vector2(1f, 1f);
            lagRt.pivot = new Vector2(0f, 0.5f);
            lagRt.offsetMin = Vector2.zero;
            lagRt.offsetMax = Vector2.zero;
            var lagFill = lagGo.AddComponent<Image>();
            lagFill.type = Image.Type.Simple;
            lagFill.color = BarLag;

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(barBgGo.transform, false);
            var fillRt = fillGo.AddComponent<RectTransform>();
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(1f, 1f);
            fillRt.pivot = new Vector2(0f, 0.5f);
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            var fill = fillGo.AddComponent<Image>();
            fill.type = Image.Type.Simple;
            fill.color = BarFill;

            RectTransform nameRect;
            RectTransform valueRect;
            var name = MakeText(root, "Name", "BOSS", 13, Cream, new Vector2(-326f, 8f), new Vector2(450f, 20f), TextAnchor.MiddleLeft, out nameRect);
            var value = MakeText(root, "Value", "", 11, MutedCream, new Vector2(266f, 8f), new Vector2(130f, 20f), TextAnchor.MiddleRight, out valueRect);

            root.SetActive(false);
            return new BarView
            {
                Root = root,
                RootRect = rootRt,
                TrackRect = barBgRt,
                NameRect = nameRect,
                ValueRect = valueRect,
                Name = name,
                Value = value,
                Fill = fill,
                LagFill = lagFill,
                FillRect = fillRt,
                LagFillRect = lagRt,
                DisplayedRatio = 1f,
                LagRatio = 1f,
            };
        }

        private static Text MakeText(
            GameObject parent,
            string name,
            string content,
            int size,
            Color color,
            Vector2 position,
            Vector2 sizeDelta,
            TextAnchor anchor,
            out RectTransform rt)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);

            rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = position;
            rt.sizeDelta = sizeDelta;

            var text = go.AddComponent<Text>();
            text.text = content;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = size;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private void UpdateResponsiveLayout(int activeBarCount)
        {
            float barWidth = BaseBarWidth;
            float xOffset = 0f;

            Rect assistRect;
            if (activeBarCount > 0 && BattleAssistHud.TryGetPanelScreenRect(out assistRect))
            {
                var canvas = _panel == null ? null : _panel.GetComponentInParent<Canvas>();
                float scale = canvas == null || canvas.scaleFactor <= 0.001f ? 1f : canvas.scaleFactor;
                float baseWidthPixels = BaseBarWidth * scale;
                float minWidthPixels = MinBarWidth * scale;
                float centerX = Screen.width * 0.5f;
                float reservedLeft = assistRect.xMax + AssistGapPixels;
                float centeredMaxWidthPixels = Mathf.Max(0f, Screen.width - (reservedLeft * 2f));
                float widthPixels = Mathf.Min(baseWidthPixels, centeredMaxWidthPixels);

                if (widthPixels < minWidthPixels)
                {
                    widthPixels = minWidthPixels;
                    float left = centerX - widthPixels * 0.5f;
                    float right = centerX + widthPixels * 0.5f;
                    float neededShift = Mathf.Max(0f, reservedLeft - left);
                    float availableShift = Mathf.Max(0f, Screen.width - ScreenEdgeGapPixels - right);
                    xOffset = Mathf.Min(neededShift, availableShift) / scale;
                }

                barWidth = Mathf.Clamp(widthPixels / scale, MinBarWidth, BaseBarWidth);
            }

            if (_panel != null)
            {
                _panel.anchoredPosition = new Vector2(xOffset, -22f);
                _panel.sizeDelta = new Vector2(barWidth + 20f, 136f);
            }

            for (int i = 0; i < _bars.Length; i++)
                ResizeBar(_bars[i], barWidth);
        }

        private static void ResizeBar(BarView bar, float width)
        {
            if (bar == null)
                return;

            width = Mathf.Clamp(width, MinBarWidth, BaseBarWidth);
            float trackWidth = Mathf.Max(80f, width - 36f);
            if (bar.RootRect != null)
                bar.RootRect.sizeDelta = new Vector2(width, 34f);
            if (bar.TrackRect != null)
                bar.TrackRect.sizeDelta = new Vector2(trackWidth, 12f);

            if (bar.ValueRect != null)
            {
                bar.ValueRect.sizeDelta = new Vector2(126f, 20f);
                bar.ValueRect.anchoredPosition = new Vector2(trackWidth * 0.5f - 63f, 8f);
            }

            if (bar.NameRect != null)
            {
                float nameWidth = Mathf.Max(96f, trackWidth - 152f);
                bar.NameRect.sizeDelta = new Vector2(nameWidth, 20f);
                bar.NameRect.anchoredPosition = new Vector2(-trackWidth * 0.5f + 6f + nameWidth * 0.5f, 8f);
            }
        }

        private static void ApplyBar(BarView bar, BossSnapshot snapshot)
        {
            float ratio = snapshot.Total <= 0f ? 0f : Mathf.Clamp01(snapshot.Current / snapshot.Total);
            if (Plugin.VerboseLoggingEnabled
             && (Mathf.Abs(ratio - bar.LastLoggedRatio) >= 0.01f || Time.unscaledTime - bar.LastLoggedAt >= 4f))
            {
                bar.LastLoggedRatio = ratio;
                bar.LastLoggedAt = Time.unscaledTime;
                Plugin.LogVerbose(
                    "[BossBar] "
                    + snapshot.Name
                    + " "
                    + Mathf.CeilToInt(Mathf.Max(0f, snapshot.Current))
                    + "/"
                    + Mathf.CeilToInt(Mathf.Max(1f, snapshot.Total))
                    + " ratio="
                    + ratio.ToString("0.000"));
            }

            if (!bar.Root.activeSelf)
            {
                bar.DisplayedRatio = ratio;
                bar.LagRatio = ratio;
                bar.Root.SetActive(true);
            }

            bar.DisplayedRatio = Mathf.Lerp(bar.DisplayedRatio, ratio, Mathf.Min(1f, 18f * Time.unscaledDeltaTime));
            bar.LagRatio = Mathf.Lerp(bar.LagRatio, ratio, Mathf.Min(1f, 5f * Time.unscaledDeltaTime));
            if (ratio > bar.LagRatio)
                bar.LagRatio = ratio;

            bar.Name.text = snapshot.Name;
            bar.Value.text = Mathf.CeilToInt(Mathf.Max(0f, snapshot.Current)).ToString()
                + " / "
                + Mathf.CeilToInt(Mathf.Max(1f, snapshot.Total)).ToString();
            bar.Fill.fillAmount = bar.DisplayedRatio;
            bar.LagFill.fillAmount = Mathf.Max(bar.DisplayedRatio, bar.LagRatio);
            SetFillWidth(bar.FillRect, bar.DisplayedRatio);
            SetFillWidth(bar.LagFillRect, Mathf.Max(bar.DisplayedRatio, bar.LagRatio));
            bar.Fill.color = ratio <= 0.25f ? BarFillLow : BarFill;
        }

        private static void SetFillWidth(RectTransform rect, float ratio)
        {
            if (rect == null)
                return;

            ratio = Mathf.Clamp01(ratio);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(ratio, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void ScanBosses()
        {
            ScratchProperties.Clear();
            ScratchDamageReceivers.Clear();

            ScanPropertyBosses(ScratchProperties);
            ScanDamageReceiverBosses(ScratchDamageReceivers);
        }

        private static void ReadBossSnapshots(List<BossSnapshot> target)
        {
            target.Clear();

            for (int i = 0; i < ScratchDamageReceivers.Count; i++)
            {
                BossSnapshot snapshot;
                var handle = ScratchDamageReceivers[i];
                if (handle == null || !TryReadDamageReceiverSnapshot(handle, out snapshot))
                    continue;
                if (ShouldShowSnapshot(snapshot, snapshot.Name))
                    target.Add(snapshot);
            }

            if (target.Count == 0)
            {
                for (int i = 0; i < ScratchProperties.Count; i++)
                {
                    BossSnapshot snapshot;
                    var handle = ScratchProperties[i];
                    if (handle == null || !TryReadPropertySnapshot(handle, out snapshot))
                        continue;
                    if (ShouldShowSnapshot(snapshot, handle.Name))
                        target.Add(snapshot);
                }
            }

            target.Sort(CompareBossSnapshots);
        }

        private static void ScanPropertyBosses(List<PropertyHandle> target)
        {
            var behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null || behaviour.gameObject == null || !behaviour.gameObject.activeInHierarchy)
                    continue;

                var fields = GetPropertyFields(behaviour.GetType());
                for (int f = 0; f < fields.Length; f++)
                {
                    object properties = null;
                    try { properties = fields[f].GetValue(behaviour); }
                    catch { }
                    if (properties == null)
                        continue;

                    var handle = BuildPropertyHandle(behaviour, fields[f], properties);
                    if (handle == null)
                        continue;

                    BossSnapshot snapshot;
                    if (!TryReadPropertySnapshot(handle, out snapshot))
                        continue;
                    if (!ShouldShowSnapshot(snapshot, handle.Name))
                        continue;

                    target.Add(handle);
                }
            }
        }

        private static void ScanDamageReceiverBosses(List<DamageReceiverHandle> target)
        {
            var receivers = UnityEngine.Object.FindObjectsOfType<DamageReceiver>();
            for (int i = 0; i < receivers.Length; i++)
            {
                var receiver = receivers[i];
                if (receiver == null
                 || receiver.type != DamageReceiver.Type.Enemy
                 || receiver.gameObject == null
                 || !receiver.gameObject.activeInHierarchy)
                {
                    continue;
                }

                AddDamageReceiverHandles(receiver, receiver.gameObject, target);
                if (receiver.transform != null && receiver.transform.parent != null)
                    AddDamageReceiverHandles(receiver, receiver.transform.parent.gameObject, target);
            }
        }

        private static void AddDamageReceiverHandles(
            DamageReceiver receiver,
            GameObject ownerObject,
            List<DamageReceiverHandle> target)
        {
            if (ownerObject == null)
                return;

            MonoBehaviour[] behaviours;
            try { behaviours = ownerObject.GetComponents<MonoBehaviour>(); }
            catch { return; }
            if (behaviours == null)
                return;

            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null)
                    continue;

                Type type = behaviour.GetType();
                FieldInfo hpField = FindHpField(type);
                if (hpField == null)
                    continue;

                string descriptor = ownerObject.name + " " + type.FullName;
                if (IsIgnoredHealthOwner(descriptor))
                    continue;

                bool timelineBackedHealth = HasTimelineBackedDamageEvent(type);
                if (!timelineBackedHealth)
                    continue;

                float hp;
                try { hp = (float)hpField.GetValue(behaviour); }
                catch { continue; }
                if (hp <= 0f)
                    continue;

                if (hp < MinInterestingHealth && !timelineBackedHealth)
                    continue;

                var handle = new DamageReceiverHandle
                {
                    Key = RuntimeHelpers.GetHashCode(behaviour),
                    Receiver = receiver,
                    HpOwner = behaviour,
                    Name = BuildDamageReceiverDisplayName(type, ownerObject),
                    HpField = hpField,
                };

                BossSnapshot snapshot;
                if (!TryReadDamageReceiverSnapshot(handle, out snapshot))
                    continue;
                if (!ShouldShowSnapshot(snapshot, descriptor))
                    continue;

                target.Add(handle);
            }
        }

        private static bool TryReadDamageReceiverSnapshot(DamageReceiverHandle handle, out BossSnapshot snapshot)
        {
            snapshot = new BossSnapshot();
            if (handle == null
             || handle.Receiver == null
             || handle.Receiver.gameObject == null
             || !handle.Receiver.gameObject.activeInHierarchy
             || handle.HpOwner == null
             || handle.HpField == null)
            {
                return false;
            }

            float hp;
            try { hp = (float)handle.HpField.GetValue(handle.HpOwner); }
            catch { return false; }

            try
            {
                int key = handle.Key;
                float max;
                if (!FallbackMaxHealth.TryGetValue(key, out max) || hp > max)
                {
                    max = Mathf.Max(1f, hp);
                    FallbackMaxHealth[key] = max;
                }

                snapshot = new BossSnapshot
                {
                    Key = key,
                    Name = handle.Name,
                    Current = Mathf.Max(0f, hp),
                    Total = Mathf.Max(1f, max),
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static PropertyHandle BuildPropertyHandle(MonoBehaviour owner, FieldInfo field, object properties)
        {
            Type type = properties.GetType();
            var totalField = type.GetField("TotalHealth", AnyInstance);
            var currentProperty = type.GetProperty("CurrentHealth", AnyInstance);
            if (totalField == null
             || totalField.FieldType != typeof(float)
             || currentProperty == null
             || currentProperty.PropertyType != typeof(float)
             || !currentProperty.CanRead)
            {
                return null;
            }

            string descriptor = (owner.GetType().FullName ?? string.Empty)
                + " "
                + owner.gameObject.name
                + " "
                + field.Name
                + " "
                + (type.FullName ?? string.Empty);

            float total;
            try { total = (float)totalField.GetValue(properties); }
            catch { return null; }

            if (total < MinInterestingHealth && !IsBossLike(descriptor))
                return null;

            return new PropertyHandle
            {
                Key = RuntimeHelpers.GetHashCode(properties),
                Owner = properties,
                Name = BuildDisplayName(owner, field, type),
                TotalField = totalField,
                CurrentProperty = currentProperty,
            };
        }

        private static bool TryReadPropertySnapshot(PropertyHandle handle, out BossSnapshot snapshot)
        {
            snapshot = new BossSnapshot();
            try
            {
                float total = (float)handle.TotalField.GetValue(handle.Owner);
                float current = (float)handle.CurrentProperty.GetValue(handle.Owner, null);
                if (total <= 0f)
                    return false;

                snapshot = new BossSnapshot
                {
                    Key = handle.Key,
                    Name = handle.Name,
                    Current = Mathf.Max(0f, current),
                    Total = Mathf.Max(1f, total),
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldShowSnapshot(BossSnapshot snapshot, string descriptor)
        {
            if (snapshot.Total <= 0f)
                return false;

            if (snapshot.Current > 0f)
            {
                if (DefeatedUntil.ContainsKey(snapshot.Key))
                    DefeatedUntil.Remove(snapshot.Key);
                return snapshot.Total >= MinInterestingHealth || IsBossLike(descriptor);
            }

            float until;
            if (!DefeatedUntil.TryGetValue(snapshot.Key, out until))
            {
                until = Time.unscaledTime + DefeatedHoldSeconds;
                DefeatedUntil[snapshot.Key] = until;
            }

            return Time.unscaledTime <= until;
        }

        private static FieldInfo[] GetPropertyFields(Type type)
        {
            FieldInfo[] cached;
            if (PropertyFieldCache.TryGetValue(type, out cached))
                return cached;

            var found = new List<FieldInfo>();
            var fields = type.GetFields(AnyInstance);
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field != null && IsLevelPropertiesType(field.FieldType))
                    found.Add(field);
            }

            cached = found.ToArray();
            PropertyFieldCache[type] = cached;
            return cached;
        }

        private static bool IsLevelPropertiesType(Type type)
        {
            while (type != null)
            {
                if (type.IsGenericType
                 && type.GetGenericTypeDefinition() == typeof(AbstractLevelProperties<,,>))
                {
                    return true;
                }

                type = type.BaseType;
            }

            return false;
        }

        private static FieldInfo FindHpField(Type type)
        {
            FieldInfo cached;
            if (HpFieldCache.TryGetValue(type, out cached))
                return cached;

            string[] names = { "hp", "HP", "health", "_hp", "currentHp", "currentHealth" };
            for (int i = 0; i < names.Length; i++)
            {
                var field = type.GetField(names[i], AnyInstance);
                if (field != null && field.FieldType == typeof(float))
                {
                    HpFieldCache[type] = field;
                    return field;
                }
            }

            HpFieldCache[type] = null;
            return null;
        }

        private static bool HasTimelineBackedDamageEvent(Type type)
        {
            if (type == null)
                return false;

            bool cached;
            if (TimelineBackedHealthCache.TryGetValue(type, out cached))
                return cached;

            bool found = false;
            Type current = type;
            while (current != null)
            {
                if (current.GetEvent("OnDamageTakenEvent", AnyInstance) != null)
                {
                    found = true;
                    break;
                }

                current = current.BaseType;
            }

            TimelineBackedHealthCache[type] = found;
            return found;
        }

        private static bool IsBattleActive()
        {
            try
            {
                return Level.Current != null && Level.Current.LevelType == Level.Type.Battle;
            }
            catch
            {
                return false;
            }
        }

        private static int CompareBossSnapshots(BossSnapshot left, BossSnapshot right)
        {
            int total = right.Total.CompareTo(left.Total);
            if (total != 0)
                return total;
            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIgnoredHealthOwner(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string name = value.ToLowerInvariant();
            for (int i = 0; i < IgnoredHealthOwners.Length; i++)
            {
                if (name.Contains(IgnoredHealthOwners[i]))
                    return true;
            }

            return false;
        }

        private static string BuildDisplayName(MonoBehaviour owner, FieldInfo field, Type propertyType)
        {
            string candidate = field.Name;
            if (string.Equals(candidate, "properties", StringComparison.OrdinalIgnoreCase)
             || string.Equals(candidate, "_properties", StringComparison.OrdinalIgnoreCase)
             || candidate.Length <= 2)
            {
                candidate = owner == null ? string.Empty : owner.GetType().Name;
                if (IsGenericDisplayName(candidate) && owner != null && owner.gameObject != null)
                    candidate = owner.gameObject.name;
            }

            if (IsGenericDisplayName(candidate))
                candidate = CurrentLevelName();
            if (IsGenericDisplayName(candidate))
                candidate = propertyType.Name;

            return CleanName(candidate);
        }

        private static string BuildDamageReceiverDisplayName(Type ownerType, GameObject ownerObject)
        {
            string candidate = ownerType == null ? string.Empty : ownerType.Name;
            if (IsGenericDisplayName(candidate) && ownerObject != null)
                candidate = ownerObject.name;
            if (IsGenericDisplayName(candidate))
                candidate = CurrentLevelName();
            return CleanName(candidate);
        }

        private static string CleanName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "BOSS";

            string text = value;
            int namespaceIndex = text.LastIndexOf('.');
            if (namespaceIndex >= 0 && namespaceIndex < text.Length - 1)
                text = text.Substring(namespaceIndex + 1);

            text = text.Replace("(Clone)", string.Empty)
                .Replace("_", " ")
                .Replace("-", " ")
                .Trim();

            text = StripLevelAffixes(text);
            text = HumanizeIdentifier(text);

            while (text.Contains("  "))
                text = text.Replace("  ", " ");

            text = text.Trim();
            if (string.IsNullOrEmpty(text) || IsGenericDisplayName(text))
                text = "BOSS";

            if (text.Length > 34)
                text = text.Substring(0, 31) + "...";

            return text.ToUpperInvariant();
        }

        private static string StripLevelAffixes(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            int levelIndex = text.IndexOf("Level", StringComparison.Ordinal);
            if (levelIndex > 0)
            {
                string before = text.Substring(0, levelIndex);
                string after = text.Substring(levelIndex + "Level".Length);
                if (!string.IsNullOrEmpty(after)
                 && !string.Equals(after, "Properties", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(after, "Property", StringComparison.OrdinalIgnoreCase))
                {
                    return after;
                }

                return before;
            }

            if (text.EndsWith("Boss", StringComparison.OrdinalIgnoreCase) && text.Length > 4)
                text = text.Substring(0, text.Length - 4);

            return text;
        }

        private static string HumanizeIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var chars = new List<char>(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (i > 0
                 && c >= 'A'
                 && c <= 'Z'
                 && chars.Count > 0
                 && chars[chars.Count - 1] != ' '
                 && (char.IsLower(text[i - 1]) || char.IsDigit(text[i - 1])))
                {
                    chars.Add(' ');
                }

                chars.Add(c);
            }

            return new string(chars.ToArray());
        }

        private static bool IsGenericDisplayName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return true;

            string text = value.Trim();
            return string.Equals(text, "Level", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "GameObject", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "Properties", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "Boss", StringComparison.OrdinalIgnoreCase);
        }

        private static string CurrentLevelName()
        {
            try
            {
                if (Level.Current != null)
                    return Level.Current.CurrentLevel.ToString();
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool IsBossLike(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string name = value.ToLowerInvariant();
            for (int i = 0; i < BossKeywords.Length; i++)
            {
                if (name.Contains(BossKeywords[i]))
                    return true;
            }

            return false;
        }
    }
}
