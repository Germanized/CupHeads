using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace CupheadOnline.UI
{
    /// <summary>
    /// Injects a MULTIPLAYER entry into the title-screen menu.
    ///
    /// Pass in the titleAnimation GameObject retrieved from StartScreen via
    /// reflection.  We dump its full hierarchy so we can see exactly what
    /// components the existing buttons use, then clone one.
    /// </summary>
    public static class MultiplayerMenuInjector
    {
        private static bool _injected;

        public static void ResetOnSceneChange() { _injected = false; }

        // ── Entry point ───────────────────────────────────────────────────────

        public static void Inject(GameObject titleAnim)
        {
            if (_injected) return;
            _injected = true;

            // ── Hierarchy dump ────────────────────────────────────────────────
            if (titleAnim != null)
            {
                Plugin.Log.LogInfo("[UI] === titleAnimation hierarchy ===");
                LogHierarchy(titleAnim.transform, 0, 6);
                Plugin.Log.LogInfo("[UI] === end hierarchy ===");
            }
            else
            {
                Plugin.Log.LogWarning("[UI] titleAnim is null — dumping full scene instead.");
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                    if (t.parent == null) LogHierarchy(t, 0, 3);
            }

            // ── Find a button template ────────────────────────────────────────
            Transform root       = titleAnim != null ? titleAnim.transform : null;
            GameObject template  = null;
            Transform  menuParent = null;

            if (root != null)
            {
                // Search inside titleAnimation for known menu-item names
                string[] labels = { "EXIT", "DLC", "OPTIONS", "START",
                                    "Exit", "Dlc", "Options", "Start" };
                foreach (var label in labels)
                {
                    var found = FindChild(root, label);
                    if (found != null)
                    {
                        template   = found.gameObject;
                        menuParent = found.parent;
                        Plugin.Log.LogInfo("[UI] Found template '" + label
                                           + "' inside titleAnimation.");
                        goto GotTemplate;
                    }
                }

                // No exact name match — log every child of titleAnimation
                Plugin.Log.LogWarning("[UI] No known button name found inside titleAnimation. "
                                      + "Listing direct children:");
                foreach (Transform child in root)
                {
                    var comps = child.GetComponents<Component>();
                    var sb    = new StringBuilder();
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(c.GetType().Name);
                    }
                    Plugin.Log.LogInfo("[UI]   child: " + child.name + "  [" + sb + "]");
                }
            }

            // Fall back: look at the whole scene
            {
                var allT = UnityEngine.Object.FindObjectsOfType<Transform>();
                string[] labels = { "EXIT", "DLC", "OPTIONS", "START" };
                foreach (var t in allT)
                {
                    foreach (var label in labels)
                    {
                        if (string.Compare(t.name, label,
                                System.StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            template   = t.gameObject;
                            menuParent = t.parent;
                            Plugin.Log.LogInfo("[UI] Found template '"
                                               + t.name + "' via scene-wide scan.");
                            goto GotTemplate;
                        }
                    }
                }
            }

            Plugin.Log.LogWarning("[UI] No button template found — creating text-only label.");
            CreateDebugLabel(titleAnim);
            return;

            GotTemplate:
            CloneAndConfigure(template, menuParent);
        }

        // ── Clone a native button ─────────────────────────────────────────────

        static void CloneAndConfigure(GameObject template, Transform parent)
        {
            var mpGO = UnityEngine.Object.Instantiate(
                template,
                parent != null ? parent : template.transform.root);
            mpGO.name = "MULTIPLAYER";

            // ── Try every known text-component type ───────────────────────────

            // 1. UnityEngine.UI.Text
            var uiText = mpGO.GetComponentInChildren<Text>(true);
            if (uiText != null)
            {
                uiText.text = "MULTIPLAYER";
                Plugin.Log.LogInfo("[UI] Set UI.Text to MULTIPLAYER.");
            }

            // 2. TextMesh (3D world text)
            var tm = mpGO.GetComponentInChildren<TextMesh>(true);
            if (tm != null)
            {
                tm.text = "MULTIPLAYER";
                Plugin.Log.LogInfo("[UI] Set TextMesh to MULTIPLAYER.");
            }

            // 3. Any component named *Text* via reflection (handles custom types)
            foreach (var comp in mpGO.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var type = comp.GetType();
                var prop = type.GetProperty("text",
                               System.Reflection.BindingFlags.Public |
                               System.Reflection.BindingFlags.Instance);
                if (prop != null && prop.CanWrite &&
                    prop.PropertyType == typeof(string))
                {
                    prop.SetValue(comp, "MULTIPLAYER", null);
                    Plugin.Log.LogInfo("[UI] Set " + type.Name + ".text via reflection.");
                }
            }

            // ── Position below template's last sibling ────────────────────────
            if (parent != null)
                PositionBelow(mpGO, parent);

            // ── Hook click / selection (will be wired once we know the type) ──
            // For now log all components on the cloned root so we know what's there
            var cloneComps = mpGO.GetComponents<Component>();
            var sb = new StringBuilder();
            foreach (var c in cloneComps)
            {
                if (c == null) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(c.GetType().Name);
            }
            Plugin.Log.LogInfo("[UI] Cloned button components: [" + sb + "]");

            Plugin.Log.LogInfo("[UI] Multiplayer button injected (native clone).");
        }

        // ── Fallback: plain label so we know the system is running ────────────

        static void CreateDebugLabel(GameObject titleAnim)
        {
            var root = new GameObject("CupheadOnline_MPLabel");

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 300;
            root.AddComponent<CanvasScaler>().uiScaleMode =
                CanvasScaler.ScaleMode.ScaleWithScreenSize;
            root.AddComponent<GraphicRaycaster>();

            var go = new GameObject("Text");
            go.transform.SetParent(root.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.27f);
            rt.sizeDelta = new Vector2(400, 50);
            rt.anchoredPosition = Vector2.zero;

            var txt = go.AddComponent<Text>();
            txt.text       = "MULTIPLAYER (debug — awaiting button type info)";
            txt.color      = CupheadUiTheme.Gold;
            txt.fontSize   = 20;
            txt.alignment  = TextAnchor.MiddleCenter;
            txt.font       = CupheadUiTheme.MenuFont;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static Transform FindChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child;
                var deeper = FindChild(child, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        static void PositionBelow(GameObject item, Transform parent)
        {
            var rt = item.GetComponent<RectTransform>();
            if (rt != null && parent.childCount >= 2)
            {
                var prev = parent.GetChild(parent.childCount - 2)
                                 .GetComponent<RectTransform>();
                if (prev != null)
                {
                    rt.anchoredPosition = prev.anchoredPosition
                        - new Vector2(0, Mathf.Abs(prev.sizeDelta.y) + 8f);
                    return;
                }
            }
            if (parent.childCount >= 2)
            {
                var prev = parent.GetChild(parent.childCount - 2);
                item.transform.position = prev.position - new Vector3(0, 0.5f, 0);
            }
        }

        internal static void LogHierarchy(Transform t, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            var indent = new string(' ', depth * 2);
            var comps  = t.GetComponents<Component>();
            var sb     = new StringBuilder();
            foreach (var c in comps)
            {
                if (sb.Length > 0) sb.Append(", ");
                if (c == null) { sb.Append("(null)"); continue; }
                sb.Append(c.GetType().Name);
                if (c is TextMesh tm) sb.Append("(\"" + tm.text + "\")");
                var uiTxt = c as Text;
                if (uiTxt != null) sb.Append("(\"" + uiTxt.text + "\")");
            }
            Plugin.Log.LogInfo(indent + t.name + "  [" + sb + "]");
            for (int i = 0; i < t.childCount; i++)
                LogHierarchy(t.GetChild(i), depth + 1, maxDepth);
        }
    }
}
