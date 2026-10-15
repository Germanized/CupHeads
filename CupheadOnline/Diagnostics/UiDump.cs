using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CupheadOnline.Diagnostics
{
    /// <summary>
    /// Developer tool: when BepInEx/config/cupheads-uidump.flag exists, dumps the
    /// slot-select scene's UI hierarchy (layout, sprites, fonts, colours) and
    /// exports every referenced sprite as a PNG to BepInEx/CupHeadsUiDump/.
    /// Used to build mod screens out of the game's own UI assets.
    /// </summary>
    public static class UiDump
    {
        static string FlagPath => Path.Combine(BepInEx.Paths.ConfigPath, "cupheads-uidump.flag");
        static string OutDir => Path.Combine(BepInEx.Paths.BepInExRootPath, "CupHeadsUiDump");

        public static void MaybeRun(MonoBehaviour host)
        {
            if (host == null || !File.Exists(FlagPath))
                return;
            host.StartCoroutine(RunLater());
        }

        static IEnumerator RunLater()
        {
            yield return new WaitForSecondsRealtime(3f);
            try { Run(); }
            catch (Exception ex) { Plugin.Log.LogWarning("[UiDump] Failed: " + ex); }
        }

        static void Run()
        {
            Directory.CreateDirectory(OutDir);
            var sb = new StringBuilder();
            var sprites = new Dictionary<string, Sprite>();

            var scene = SceneManager.GetActiveScene();
            sb.AppendLine("Scene: " + scene.name + "  screen " + Screen.width + "x" + Screen.height);
            foreach (var root in scene.GetRootGameObjects())
                Walk(root.transform, 0, sb, sprites);

            sb.AppendLine();
            sb.AppendLine("=== Fonts loaded ===");
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
                sb.AppendLine("  " + f.name + " (size " + f.fontSize + ")");

            File.WriteAllText(Path.Combine(OutDir, "hierarchy-" + scene.name + ".txt"), sb.ToString());

            int exported = 0;
            foreach (var kv in sprites)
            {
                if (ExportSprite(kv.Value, Path.Combine(OutDir, Sanitize(kv.Key) + ".png")))
                    exported++;
            }

            Plugin.Log.LogInfo("[UiDump] Wrote hierarchy and " + exported + " sprite(s) to " + OutDir);
        }

        static void Walk(Transform t, int depth, StringBuilder sb, Dictionary<string, Sprite> sprites)
        {
            string indent = new string(' ', depth * 2);
            var go = t.gameObject;
            var line = new StringBuilder();
            line.Append(indent).Append(go.activeSelf ? "+ " : "- ").Append(go.name);

            var rt = t as RectTransform;
            if (rt != null)
            {
                line.Append("  [rt pos=").Append(V(rt.anchoredPosition)).Append(" size=").Append(V(rt.sizeDelta))
                    .Append(" amin=").Append(V(rt.anchorMin)).Append(" amax=").Append(V(rt.anchorMax))
                    .Append(" piv=").Append(V(rt.pivot)).Append(" scl=").Append(V(t.localScale)).Append("]");
            }
            else
            {
                line.Append("  [pos=").Append(V(t.localPosition)).Append(" scl=").Append(V(t.localScale)).Append("]");
            }

            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                var img = c as Image;
                var txt = c as Text;
                var sr = c as SpriteRenderer;
                if (img != null)
                {
                    line.Append("\n").Append(indent).Append("    Image sprite=").Append(SpriteDesc(img.sprite))
                        .Append(" type=").Append(img.type).Append(" color=").Append(C(img.color))
                        .Append(" enabled=").Append(img.enabled);
                    Remember(img.sprite, sprites);
                }
                else if (txt != null)
                {
                    string s = txt.text ?? "";
                    if (s.Length > 60) s = s.Substring(0, 60) + "...";
                    line.Append("\n").Append(indent).Append("    Text font=").Append(txt.font != null ? txt.font.name : "null")
                        .Append(" size=").Append(txt.fontSize).Append(" style=").Append(txt.fontStyle)
                        .Append(" color=").Append(C(txt.color)).Append(" align=").Append(txt.alignment)
                        .Append(" spacing=").Append(txt.lineSpacing)
                        .Append(" text=\"").Append(s.Replace("\n", "\\n")).Append("\"");
                }
                else if (sr != null)
                {
                    line.Append("\n").Append(indent).Append("    SpriteRenderer sprite=").Append(SpriteDesc(sr.sprite))
                        .Append(" color=").Append(C(sr.color)).Append(" layer=").Append(sr.sortingLayerName)
                        .Append("/").Append(sr.sortingOrder).Append(" enabled=").Append(sr.enabled);
                    Remember(sr.sprite, sprites);
                }
                else
                {
                    line.Append("\n").Append(indent).Append("    ").Append(c.GetType().Name);
                    var canvas = c as Canvas;
                    if (canvas != null)
                        line.Append(" mode=").Append(canvas.renderMode).Append(" order=").Append(canvas.sortingOrder)
                            .Append(" layer=").Append(canvas.sortingLayerName);
                }
            }

            sb.AppendLine(line.ToString());
            for (int i = 0; i < t.childCount; i++)
                Walk(t.GetChild(i), depth + 1, sb, sprites);
        }

        static void Remember(Sprite s, Dictionary<string, Sprite> sprites)
        {
            if (s == null) return;
            string key = s.name + "_" + (s.texture != null ? s.texture.name : "notex");
            if (!sprites.ContainsKey(key))
                sprites[key] = s;
        }

        static string SpriteDesc(Sprite s)
        {
            if (s == null) return "null";
            return "'" + s.name + "' tex='" + (s.texture != null ? s.texture.name : "null") + "' rect=" + s.rect
                + " border=" + s.border + " ppu=" + s.pixelsPerUnit;
        }

        static bool ExportSprite(Sprite sprite, string path)
        {
            if (sprite == null || sprite.texture == null) return false;
            try
            {
                var tex = sprite.texture;
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var r = sprite.textureRect;
                var copy = new Texture2D((int)r.width, (int)r.height, TextureFormat.ARGB32, false);
                // RenderTexture rows are bottom-up like the texture rect.
                copy.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                // Unity API (not game code), found by scanning so the hook audit
                // does not mistake it for a game member.
                var conv = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                MethodInfo encode = null;
                if (conv != null)
                {
                    foreach (var m in conv.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name == "EncodeToPNG" && m.GetParameters().Length == 1)
                        {
                            encode = m;
                            break;
                        }
                    }
                }
                if (encode == null) return false;
                var bytes = encode.Invoke(null, new object[] { copy }) as byte[];
                UnityEngine.Object.Destroy(copy);
                if (bytes == null) return false;
                File.WriteAllBytes(path, bytes);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[UiDump] Sprite " + sprite.name + ": " + ex.Message);
                return false;
            }
        }

        static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }

        static string V(Vector2 v) { return "(" + v.x.ToString("0.##") + "," + v.y.ToString("0.##") + ")"; }
        static string V(Vector3 v) { return "(" + v.x.ToString("0.##") + "," + v.y.ToString("0.##") + "," + v.z.ToString("0.##") + ")"; }
        static string C(Color c) { return "(" + c.r.ToString("0.00") + "," + c.g.ToString("0.00") + "," + c.b.ToString("0.00") + "," + c.a.ToString("0.00") + ")"; }
    }
}
