using HarmonyLib;
using CupheadOnline.Sync;
using UnityEngine;
using System.Collections.Generic;

namespace CupheadOnline.Patches
{
    static class PlayerAggregateMath
    {
        internal static bool TryBuildAggregate(
            out int count,
            out Vector2 center,
            out Vector2 cameraCenter,
            out Vector2 topPlayerPosition)
        {
            count = 0;
            center = Vector2.zero;
            cameraCenter = Vector2.zero;
            topPlayerPosition = Vector2.zero;

            if (!MultiplayerSession.IsActive)
                return false;

            Vector2 centerSum = Vector2.zero;
            Vector2 cameraSum = Vector2.zero;
            float topXSum = 0f;
            float topY = float.MinValue;
            bool hasTop = false;

            AppendBuiltIn(PlayerId.PlayerOne, ref count, ref centerSum, ref cameraSum, ref topXSum, ref topY, ref hasTop);
            AppendBuiltIn(PlayerId.PlayerTwo, ref count, ref centerSum, ref cameraSum, ref topXSum, ref topY, ref hasTop);

            int extraCount;
            Vector2 extraCenter;
            Vector2 extraCamera;
            Vector2 extraTop;
            if (ExtraParticipantTracker.TryGetAggregate(out extraCount, out extraCenter, out extraCamera, out extraTop))
            {
                centerSum += extraCenter * extraCount;
                cameraSum += extraCamera * extraCount;
                topXSum += extraTop.x * extraCount;
                if (!hasTop || extraTop.y > topY)
                    topY = extraTop.y;
                hasTop = true;
                count += extraCount;
            }

            if (count <= 0)
                return false;

            center = centerSum / count;
            cameraCenter = cameraSum / count;
            topPlayerPosition = new Vector2(topXSum / count, topY);
            return true;
        }

        static void AppendBuiltIn(
            PlayerId playerId,
            ref int count,
            ref Vector2 centerSum,
            ref Vector2 cameraSum,
            ref float topXSum,
            ref float topY,
            ref bool hasTop)
        {
            AbstractPlayerController player;
            try
            {
                player = PlayerManager.GetPlayer(playerId);
            }
            catch
            {
                return;
            }

            if (player == null || player.IsDead || player.gameObject == null || !player.gameObject.activeInHierarchy)
                return;

            count++;
            centerSum += new Vector2(player.center.x, player.center.y);
            cameraSum += new Vector2(player.CameraCenter.x, player.CameraCenter.y);
            topXSum += player.transform.position.x;
            if (!hasTop || player.transform.position.y > topY)
                topY = player.transform.position.y;
            hasTop = true;
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "get_Center")]
    public static class PlayerManagerCenterPatch
    {
        static void Postfix(ref Vector2 __result)
        {
            int count;
            Vector2 center;
            Vector2 cameraCenter;
            Vector2 top;
            if (PlayerAggregateMath.TryBuildAggregate(out count, out center, out cameraCenter, out top))
                __result = center;
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "get_CameraCenter")]
    public static class PlayerManagerCameraCenterPatch
    {
        static void Postfix(ref Vector2 __result)
        {
            int count;
            Vector2 center;
            Vector2 cameraCenter;
            Vector2 top;
            if (PlayerAggregateMath.TryBuildAggregate(out count, out center, out cameraCenter, out top))
                __result = cameraCenter;
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "get_TopPlayerPosition")]
    public static class PlayerManagerTopPlayerPositionPatch
    {
        static void Postfix(ref Vector2 __result)
        {
            int count;
            Vector2 center;
            Vector2 cameraCenter;
            Vector2 top;
            if (PlayerAggregateMath.TryBuildAggregate(out count, out center, out cameraCenter, out top))
                __result = top;
        }
    }

    static class PlayerSelectionMath
    {
        static readonly List<AbstractPlayerController> ScratchPlayers =
            new List<AbstractPlayerController>(6);
        static readonly Dictionary<int, AbstractPlayerController> ScratchPlayerMap =
            new Dictionary<int, AbstractPlayerController>(6);
        static int _nextSelectionCursor;

        internal static bool TryCollectPlayers(List<AbstractPlayerController> players)
        {
            if (players == null)
                return false;

            players.Clear();
            AppendBuiltIn(players, PlayerId.PlayerOne);
            AppendBuiltIn(players, PlayerId.PlayerTwo);
            ExtraRemoteAvatarManager.AppendTargetableControllers(players);
            return players.Count > 0;
        }

        internal static bool TryGetCurrent(ref AbstractPlayerController result)
        {
            if (!TryCollectPlayers(ScratchPlayers))
                return false;

            if (_nextSelectionCursor < 0 || _nextSelectionCursor >= ScratchPlayers.Count)
                _nextSelectionCursor = ResolveIndex(ScratchPlayers, result);

            if (_nextSelectionCursor < 0 || _nextSelectionCursor >= ScratchPlayers.Count)
                _nextSelectionCursor = 0;

            result = ScratchPlayers[_nextSelectionCursor];
            return true;
        }

        internal static bool TryGetNext(ref AbstractPlayerController result)
        {
            if (!TryCollectPlayers(ScratchPlayers))
                return false;

            if (_nextSelectionCursor < 0 || _nextSelectionCursor >= ScratchPlayers.Count)
                _nextSelectionCursor = ResolveIndex(ScratchPlayers, result);

            if (_nextSelectionCursor < 0 || _nextSelectionCursor >= ScratchPlayers.Count)
                _nextSelectionCursor = 0;

            result = ScratchPlayers[_nextSelectionCursor];
            _nextSelectionCursor = (_nextSelectionCursor + 1) % ScratchPlayers.Count;
            return true;
        }

        internal static bool TryGetRandom(ref AbstractPlayerController result)
        {
            if (!TryCollectPlayers(ScratchPlayers))
                return false;

            int index = Random.Range(0, ScratchPlayers.Count);
            result = ScratchPlayers[index];
            _nextSelectionCursor = (index + 1) % ScratchPlayers.Count;
            return true;
        }

        internal static bool TryGetFirst(ref AbstractPlayerController result)
        {
            if (!TryCollectPlayers(ScratchPlayers))
                return false;

            result = ScratchPlayers[0];
            if (ScratchPlayers.Count > 1)
                _nextSelectionCursor = 1;
            else
                _nextSelectionCursor = 0;
            return true;
        }

        internal static bool TryGetCount(out int count)
        {
            count = 0;
            if (!TryCollectPlayers(ScratchPlayers))
                return false;

            count = ScratchPlayers.Count;
            return true;
        }

        internal static bool TryGetAllPlayers(
            out Dictionary<int, AbstractPlayerController>.ValueCollection values)
        {
            values = null;
            if (!TryCollectPlayers(ScratchPlayers))
                return false;

            ScratchPlayerMap.Clear();
            for (int i = 0; i < ScratchPlayers.Count; i++)
                ScratchPlayerMap[i] = ScratchPlayers[i];

            values = ScratchPlayerMap.Values;
            return true;
        }

        static void AppendBuiltIn(List<AbstractPlayerController> players, PlayerId playerId)
        {
            AbstractPlayerController player;
            try
            {
                player = PlayerManager.GetPlayer(playerId);
            }
            catch
            {
                return;
            }

            if (player == null || player.IsDead || player.gameObject == null || !player.gameObject.activeInHierarchy)
                return;

            players.Add(player);
        }

        static int ResolveIndex(List<AbstractPlayerController> players, AbstractPlayerController controller)
        {
            if (players == null || players.Count == 0)
                return -1;

            if (controller != null)
            {
                int index = players.IndexOf(controller);
                if (index >= 0)
                    return index;
            }

            return 0;
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "GetNext")]
    public static class PlayerManagerGetNextPatch
    {
        static void Postfix(ref AbstractPlayerController __result)
        {
            if (!MultiplayerSession.IsActive)
                return;

            PlayerSelectionMath.TryGetNext(ref __result);
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "GetRandom")]
    public static class PlayerManagerGetRandomPatch
    {
        static void Postfix(ref AbstractPlayerController __result)
        {
            if (!MultiplayerSession.IsActive)
                return;

            PlayerSelectionMath.TryGetRandom(ref __result);
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "GetFirst")]
    public static class PlayerManagerGetFirstPatch
    {
        static void Postfix(ref AbstractPlayerController __result)
        {
            if (!MultiplayerSession.IsActive)
                return;

            PlayerSelectionMath.TryGetFirst(ref __result);
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "get_Current")]
    public static class PlayerManagerCurrentPatch
    {
        static void Postfix(ref AbstractPlayerController __result)
        {
            if (!MultiplayerSession.IsActive)
                return;

            PlayerSelectionMath.TryGetCurrent(ref __result);
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "get_Count")]
    public static class PlayerManagerCountPatch
    {
        static void Postfix(ref int __result)
        {
            if (!MultiplayerSession.IsActive)
                return;

            int count;
            if (PlayerSelectionMath.TryGetCount(out count))
                __result = count;
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "GetAllPlayers")]
    public static class PlayerManagerGetAllPlayersPatch
    {
        static void Postfix(ref Dictionary<int, AbstractPlayerController>.ValueCollection __result)
        {
            if (!MultiplayerSession.IsActive)
                return;

            Dictionary<int, AbstractPlayerController>.ValueCollection values;
            if (PlayerSelectionMath.TryGetAllPlayers(out values))
                __result = values;
        }
    }

    [HarmonyPatch(typeof(PlayerManager), "BothPlayersActive")]
    public static class PlayerManagerBothPlayersActivePatch
    {
        static void Postfix(ref bool __result)
        {
            if (!MultiplayerSession.IsActive)
                return;

            int count;
            if (PlayerSelectionMath.TryGetCount(out count))
                __result = count > 1;
        }
    }
}
