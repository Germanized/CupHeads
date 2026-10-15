using HarmonyLib;
using CupheadOnline.Net;
using CupheadOnline.Sync;
using UnityEngine;

namespace CupheadOnline.Patches
{
    internal static class SceneSyncState
    {
        internal static bool SuppressMenuSceneBroadcast;
        static int _clientSceneLoadAllowance;
        static float _clientSceneLoadAllowanceSetAt = -10f;
        static float _lastBlockedClientSceneLogAt = -10f;

        internal static void ResetTransientSyncState()
        {
            LevelReadyGate.OnSceneLoadStarted();
            LevelOutcomeSync.OnSceneChanged();
            PauseSync.OnSceneChanged();
            EnemySyncManager.Reset();
            DamageAuthority.ResetAll();
            ParticipantReviveController.Reset();
            EnemyRegistry.Clear();
            RemotePlayer.Reset();
            RemoteInputDriver.Reset();
            ExtraParticipantTracker.Reset();
            ExtraRemoteAvatarManager.Reset();
            ExtraParticipantDamageBridge.Reset();
            ExtraParticipantReviveVisuals.Reset();
            ParticipantStatusTracker.Reset();
            PlayerColorSync.Reset();
            UI.BossHealthBarOverlay.Reset();
            UI.BattleAssistHud.Reset();
        }

        internal static void AllowNextClientSceneLoad()
        {
            if (MultiplayerSession.IsClient)
            {
                _clientSceneLoadAllowance = System.Math.Max(_clientSceneLoadAllowance, 1);
                _clientSceneLoadAllowanceSetAt = Time.unscaledTime;
            }
        }

        internal static void AllowNextClientLevelLoad()
        {
            if (MultiplayerSession.IsClient)
            {
                _clientSceneLoadAllowance = System.Math.Max(_clientSceneLoadAllowance, 2);
                _clientSceneLoadAllowanceSetAt = Time.unscaledTime;
            }
        }

        internal static bool CanStartSceneLoadAsThisPeer(string label)
        {
            if (!MultiplayerSession.IsActive || MultiplayerSession.IsHost)
                return true;

            if (_clientSceneLoadAllowance > 0
             && Time.unscaledTime - _clientSceneLoadAllowanceSetAt <= 2f)
            {
                _clientSceneLoadAllowance--;
                return true;
            }

            _clientSceneLoadAllowance = 0;

            if (Time.unscaledTime - _lastBlockedClientSceneLogAt > 2f)
            {
                _lastBlockedClientSceneLogAt = Time.unscaledTime;
                Plugin.Log.LogInfo("[SceneSync] Blocked local client scene load: " + label + ". Waiting for host.");
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(SceneLoader), "LoadLevel",
        typeof(Levels), typeof(SceneLoader.Transition), typeof(SceneLoader.Icon), typeof(SceneLoader.Context))]
    public static class SceneLoaderLevelsPatch
    {
        static bool Prefix(
            Levels level,
            SceneLoader.Transition transitionStart,
            SceneLoader.Icon icon,
            SceneLoader.Context context,
            out bool __state)
        {
            __state = false;

            // The game ignores load requests while a load is already running;
            // do not broadcast or wipe sync state for a request that won't happen.
            if (SceneLoader.CurrentlyLoading)
                return true;

            if (!SceneSyncState.CanStartSceneLoadAsThisPeer("level " + level))
                return false;

            if (!MultiplayerSession.IsActive) return true;

            SceneSyncState.ResetTransientSyncState();

            if (!MultiplayerSession.IsHost) return true;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return true;

            SceneSyncState.SuppressMenuSceneBroadcast = true;
            __state = true;

            var pkt = new SceneChangePacket
            {
                LevelEnum       = (int)level,
                RngSeed         = RngSync.NextSeed(),
                TransitionStart = (byte)transitionStart,
                Icon            = (byte)icon,
                ContextKind     = SceneContextCodec.Encode(context),
            };
            Plugin.Net.SendSceneChange(ref pkt);
            return true;
        }

        static void Postfix(bool __state)
        {
            if (__state)
                SceneSyncState.SuppressMenuSceneBroadcast = false;
        }
    }

    [HarmonyPatch(typeof(SceneLoader), "LoadScene",
        typeof(Scenes), typeof(SceneLoader.Transition), typeof(SceneLoader.Transition), typeof(SceneLoader.Icon), typeof(SceneLoader.Context))]
    public static class SceneLoaderScenesPatch
    {
        static bool Prefix(
            Scenes scene,
            SceneLoader.Transition transitionStart,
            SceneLoader.Transition transitionEnd,
            SceneLoader.Icon icon,
            SceneLoader.Context context)
        {
            if (SceneLoader.CurrentlyLoading)
                return true;

            if (!SceneSyncState.CanStartSceneLoadAsThisPeer("scene " + scene))
                return false;

            if (!MultiplayerSession.IsActive) return true;

            SceneSyncState.ResetTransientSyncState();

            if (!MultiplayerSession.IsHost) return true;
            if (Plugin.Net == null || !Plugin.Net.IsConnected) return true;
            if (SceneSyncState.SuppressMenuSceneBroadcast) return true;

            var pkt = new MenuSceneChangePacket
            {
                SceneEnum       = (int)scene,
                TransitionStart = (byte)transitionStart,
                TransitionEnd   = (byte)transitionEnd,
                Icon            = (byte)icon,
                RngSeed         = RngSync.NextSeed(),
                CurrentLevel    = (int)SceneLoader.CurrentLevel,
                ContextKind     = SceneContextCodec.Encode(context),
            };
            Plugin.Net.SendMenuSceneChange(ref pkt);
            return true;
        }
    }
}
