using System;
using CupheadOnline.Net;
using CupheadOnline.UI;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Only the host may change scenes. When the guest's own game decides to
    /// change scene — the guest picked a difficulty on a level prompt, walked
    /// into the shop, chose Retry or Exit on the pause or game-over menu, or
    /// pressed on at the results screen — the load used to be silently
    /// blocked and wait for the host to happen to do the same; if the two
    /// screens had drifted, the guest's menu sat there forever. Those loads are
    /// now forwarded to the host, which validates and performs them, and the
    /// normal host scene sync brings both players along.
    ///
    /// A guest choosing Exit to Title leaves the session instead.
    /// </summary>
    public static class SceneRequestSync
    {
        const float RequestCooldown = 1f;

        static string _lastRequestKey = string.Empty;
        static float _lastRequestAt = -10f;

        public static bool IsGuest =>
            MultiplayerSession.IsClient
            && !LocalDevSession.IsActive
            && Plugin.Net != null
            && Plugin.Net.IsConnected;

        /// <summary>Scenes that mean "I'm leaving this co-op session".</summary>
        public static bool IsLeaveScene(Scenes scene)
        {
            return scene == Scenes.scene_title
                || scene == Scenes.scene_slot_select
                || scene == Scenes.scene_start;
        }

        // ── Guest ────────────────────────────────────────────────────────────

        public static void ForwardLevel(Levels level, SceneLoader.Transition transitionStart, SceneLoader.Icon icon, SceneLoader.Context context)
        {
            Send(new SceneRequestPacket
            {
                Kind = (byte)SceneRequestKind.LoadLevel,
                Target = (int)level,
                Mode = (byte)Level.CurrentMode,
                TransitionStart = (byte)transitionStart,
                Icon = (byte)icon,
                ContextKind = SceneContextCodec.Encode(context),
            }, "level:" + level, "Asked the host to start " + level + "...");
        }

        public static void ForwardScene(Scenes scene, SceneLoader.Transition transitionStart, SceneLoader.Transition transitionEnd, SceneLoader.Icon icon, SceneLoader.Context context)
        {
            Send(new SceneRequestPacket
            {
                Kind = (byte)SceneRequestKind.LoadScene,
                Target = (int)scene,
                Mode = (byte)Level.CurrentMode,
                TransitionStart = (byte)transitionStart,
                TransitionEnd = (byte)transitionEnd,
                Icon = (byte)icon,
                ContextKind = SceneContextCodec.Encode(context),
            }, "scene:" + scene, "Asked the host to continue...");
        }

        public static void ForwardRetry()
        {
            Send(new SceneRequestPacket { Kind = (byte)SceneRequestKind.Retry }, "retry", "Asked the host to retry...");
        }

        public static void ForwardExitToMap()
        {
            Send(new SceneRequestPacket { Kind = (byte)SceneRequestKind.ExitToMap }, "exit", "Asked the host to return to the map...");
        }

        /// <summary>The guest is heading to the title / slot screen: end the session cleanly.</summary>
        public static void LeaveSessionForLocalScene(Scenes scene)
        {
            Plugin.Log.LogInfo("[SceneRequest] Guest chose " + scene + " - leaving the session.");
            if (Plugin.Net != null)
                Plugin.Net.Shutdown();
        }

        static void Send(SceneRequestPacket pkt, string key, string hud)
        {
            if (!IsGuest)
                return;

            float now = Time.unscaledTime;
            if (key == _lastRequestKey && now - _lastRequestAt < RequestCooldown)
                return;

            _lastRequestKey = key;
            _lastRequestAt = now;
            Plugin.Net.SendSceneRequest(ref pkt);
            ConnectionHUD.Show(hud);
            Plugin.Log.LogInfo("[SceneRequest] Forwarded " + key + " to the host.");
        }

        // ── Host ─────────────────────────────────────────────────────────────

        public static void OnGuestRequest(SceneRequestPacket pkt)
        {
            if (!MultiplayerSession.IsHost || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (SceneLoader.CurrentlyLoading)
                return; // the host is already moving; the guest will follow

            var level = Level.Current;
            bool fightInProgress = level != null && !level.Ending && !Level.Won;

            try
            {
                switch (pkt.RequestKind)
                {
                    case SceneRequestKind.LoadLevel:
                        if (fightInProgress || !Enum.IsDefined(typeof(Levels), pkt.Target))
                            return;
                        ApplyMode(pkt.Mode);
                        Announce("Your partner started a level.");
                        SceneLoader.LoadLevel(
                            (Levels)pkt.Target,
                            ToTransition(pkt.TransitionStart, SceneLoader.Transition.Iris),
                            ToIcon(pkt.Icon),
                            SceneContextCodec.Decode(pkt.ContextKind));
                        return;

                    case SceneRequestKind.LoadScene:
                        if (fightInProgress || !Enum.IsDefined(typeof(Scenes), pkt.Target))
                            return;
                        var scene = (Scenes)pkt.Target;
                        if (IsLeaveScene(scene))
                            return;
                        ApplyMode(pkt.Mode);
                        Announce("Your partner moved on.");
                        SceneLoader.LoadScene(
                            scene,
                            ToTransition(pkt.TransitionStart, SceneLoader.Transition.Fade),
                            ToTransition(pkt.TransitionEnd, SceneLoader.Transition.Fade),
                            ToIcon(pkt.Icon),
                            SceneContextCodec.Decode(pkt.ContextKind));
                        return;

                    // Retry and Exit to Map are honoured even mid-fight: either
                    // player can pick them from the pause menu in couch co-op,
                    // and the guest's pause menu waits on the load.
                    case SceneRequestKind.Retry:
                        if (level == null)
                            return;
                        Announce("Your partner chose Retry.");
                        SceneLoader.ReloadLevel();
                        return;

                    case SceneRequestKind.ExitToMap:
                        if (level == null && Map.Current != null)
                            return;
                        Announce("Your partner chose to return to the map.");
                        SceneLoader.LoadLastMap();
                        return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[SceneRequest] Could not perform the guest's request: " + ex.Message);
            }
        }

        static void Announce(string message)
        {
            ConnectionHUD.Show(message);
            Plugin.Log.LogInfo("[SceneRequest] " + message);
        }

        static void ApplyMode(byte mode)
        {
            if (Enum.IsDefined(typeof(Level.Mode), (int)mode))
                Level.SetCurrentMode((Level.Mode)mode);
        }

        static SceneLoader.Transition ToTransition(byte raw, SceneLoader.Transition fallback)
        {
            return Enum.IsDefined(typeof(SceneLoader.Transition), (int)raw) ? (SceneLoader.Transition)raw : fallback;
        }

        static SceneLoader.Icon ToIcon(byte raw)
        {
            return Enum.IsDefined(typeof(SceneLoader.Icon), (int)raw) ? (SceneLoader.Icon)raw : SceneLoader.Icon.Hourglass;
        }
    }
}
