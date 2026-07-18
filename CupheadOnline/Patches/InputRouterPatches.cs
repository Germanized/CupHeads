using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CupheadOnline.Net;
using CupheadOnline.Sync;

namespace CupheadOnline.Patches
{
    internal static class UniversalInputRouter
    {
        static readonly MethodInfo GetPlayerInputMethod =
            AccessTools.Method(typeof(PlayerManager), "GetPlayerInput", new[] { typeof(PlayerId) });
        static readonly Type RewiredPlayerType = AccessTools.TypeByName("Rewired.Player");

        internal static readonly MethodInfo RewiredGetAxisMethod =
            RewiredPlayerType == null ? null : AccessTools.Method(RewiredPlayerType, "GetAxis", new[] { typeof(int) });
        internal static readonly MethodInfo RewiredGetButtonMethod =
            RewiredPlayerType == null ? null : AccessTools.Method(RewiredPlayerType, "GetButton", new[] { typeof(int) });
        internal static readonly MethodInfo RewiredGetButtonDownMethod =
            RewiredPlayerType == null ? null : AccessTools.Method(RewiredPlayerType, "GetButtonDown", new[] { typeof(int) });
        internal static readonly MethodInfo RewiredGetButtonUpMethod =
            RewiredPlayerType == null ? null : AccessTools.Method(RewiredPlayerType, "GetButtonUp", new[] { typeof(int) });

        static int _rawQueryDepth;
        static uint _nextInputTick = 1;

        static UniversalInputRouter()
        {
            MultiplayerSession.OnSessionStarted += ResetInputSequence;
            MultiplayerSession.OnSessionEnded += ResetInputSequence;
        }

        internal static bool IsRawQuery => _rawQueryDepth > 0;

        internal static object GetActions(PlayerId playerId)
        {
            if (GetPlayerInputMethod == null)
                return null;

            try { return GetPlayerInputMethod.Invoke(null, new object[] { playerId }); }
            catch { return null; }
        }

        internal static bool TryResolvePlayerId(object rewiredPlayer, out PlayerId playerId)
        {
            if (rewiredPlayer != null)
            {
                if (ReferenceEquals(rewiredPlayer, GetActions(PlayerId.PlayerOne)))
                {
                    playerId = PlayerId.PlayerOne;
                    return true;
                }

                if (ReferenceEquals(rewiredPlayer, GetActions(PlayerId.PlayerTwo)))
                {
                    playerId = PlayerId.PlayerTwo;
                    return true;
                }
            }

            playerId = PlayerId.None;
            return false;
        }

        internal static bool TryGetRemoteButton(PlayerId playerId, int actionId, bool down, bool up, out bool value)
        {
            value = false;
            if (!IsValidButton(actionId))
                return true;

            var button = (CupheadButton)actionId;
            var participantId = (byte)playerId;

            if (down)
                value = RemoteInputDriver.WasPressedThisFrame(participantId, button);
            else if (up)
                value = RemoteInputDriver.WasReleasedThisFrame(participantId, button);
            else
                value = RemoteInputDriver.IsPressed(participantId, button);

            return true;
        }

        internal static bool TryGetRemoteAxis(PlayerId playerId, int actionId, out float value)
        {
            value = 0f;
            InputFramePacket input;
            if (!RemoteInputDriver.TryGetCurrent(playerId, out input))
                return true;

            // MoveHorizontal/MoveVertical are actions 0/1; the menu navigation
            // axes MenuHorizontal/MenuVertical (22/23) mirror the same stick.
            switch (actionId)
            {
                case (int)CupheadButton.MoveHorizontal:
                case (int)CupheadButton.MenuHorizontal:
                    value = input.AxisX;
                    break;
                case (int)CupheadButton.MoveVertical:
                case (int)CupheadButton.MenuVertical:
                    value = input.AxisY;
                    break;
            }
            return true;
        }

        internal static bool TryGetLocalButton(int actionId, bool down, bool up, out bool value)
        {
            value = false;
            if (!IsValidButton(actionId))
                return true;

            MethodInfo method = down ? RewiredGetButtonDownMethod : up ? RewiredGetButtonUpMethod : RewiredGetButtonMethod;
            if (method == null)
                return false;

            object playerOne = GetActions(PlayerId.PlayerOne);
            object playerTwo = GetActions(PlayerId.PlayerTwo);

            bool one = RawBool(playerOne, method, actionId);
            bool two = RawBool(playerTwo, method, actionId);
            value = one || two;
            return true;
        }

        internal static bool TryGetLocalAxis(int actionId, out float value)
        {
            value = 0f;
            if (RewiredGetAxisMethod == null)
                return false;

            float one = RawFloat(GetActions(PlayerId.PlayerOne), RewiredGetAxisMethod, actionId);
            float two = RawFloat(GetActions(PlayerId.PlayerTwo), RewiredGetAxisMethod, actionId);
            value = Mathf.Abs(two) > Mathf.Abs(one) ? two : one;
            return true;
        }

        internal static InputFramePacket BuildLocalInputFrame()
        {
            return BuildLocalInputFrameForPlayer(PlayerId.None);
        }

        internal static InputFramePacket BuildLocalInputFrameForPlayer(PlayerId playerId)
        {
            uint buttons = 0;
            Pack(playerId, CupheadButton.Jump, ref buttons);
            Pack(playerId, CupheadButton.Shoot, ref buttons);
            Pack(playerId, CupheadButton.Super, ref buttons);
            Pack(playerId, CupheadButton.SwitchWeapon, ref buttons);
            Pack(playerId, CupheadButton.Lock, ref buttons);
            Pack(playerId, CupheadButton.Dash, ref buttons);
            Pack(playerId, CupheadButton.Pause, ref buttons);
            Pack(playerId, CupheadButton.NextPage, ref buttons);
            Pack(playerId, CupheadButton.PreviousPage, ref buttons);
            Pack(playerId, CupheadButton.Accept, ref buttons);
            Pack(playerId, CupheadButton.Cancel, ref buttons);
            Pack(playerId, CupheadButton.EquipMenu, ref buttons);
            Pack(playerId, CupheadButton.MenuUp, ref buttons);
            Pack(playerId, CupheadButton.MenuLeft, ref buttons);
            Pack(playerId, CupheadButton.MenuDown, ref buttons);
            Pack(playerId, CupheadButton.MenuRight, ref buttons);
            Pack(playerId, CupheadButton.Swap, ref buttons);

            float axisX;
            float axisY;
            TryGetLocalAxis(playerId, 0, out axisX);
            TryGetLocalAxis(playerId, 1, out axisY);

            return new InputFramePacket
            {
                AxisX = axisX,
                AxisY = axisY,
                Buttons = buttons,
                Tick = NextInputTick(),
            };
        }

        static uint NextInputTick()
        {
            unchecked
            {
                uint next = _nextInputTick++;
                if (next == 0)
                {
                    _nextInputTick = 1;
                    next = _nextInputTick++;
                }
                return next;
            }
        }

        static void ResetInputSequence()
        {
            _nextInputTick = 1;
        }

        static void Pack(PlayerId playerId, CupheadButton button, ref uint buttons)
        {
            bool pressed;
            if (TryGetLocalButton(playerId, (int)button, false, false, out pressed) && pressed)
                buttons |= 1u << (int)button;
        }

        internal static bool TryGetLocalButton(PlayerId playerId, int actionId, bool down, bool up, out bool value)
        {
            if (playerId == PlayerId.None)
                return TryGetLocalButton(actionId, down, up, out value);

            value = false;
            if (!IsValidButton(actionId))
                return true;

            MethodInfo method = down ? RewiredGetButtonDownMethod : up ? RewiredGetButtonUpMethod : RewiredGetButtonMethod;
            if (method == null)
                return false;

            value = RawBool(GetActions(playerId), method, actionId);
            return true;
        }

        internal static bool TryGetLocalAxis(PlayerId playerId, int actionId, out float value)
        {
            if (playerId == PlayerId.None)
                return TryGetLocalAxis(actionId, out value);

            value = 0f;
            if (RewiredGetAxisMethod == null)
                return false;

            value = RawFloat(GetActions(playerId), RewiredGetAxisMethod, actionId);
            return true;
        }

        static bool RawBool(object player, MethodInfo method, int actionId)
        {
            if (player == null || method == null)
                return false;

            _rawQueryDepth++;
            try
            {
                object result = method.Invoke(player, new object[] { actionId });
                return result is bool && (bool)result;
            }
            catch
            {
                return false;
            }
            finally
            {
                _rawQueryDepth--;
            }
        }

        static float RawFloat(object player, MethodInfo method, int actionId)
        {
            if (player == null || method == null)
                return 0f;

            _rawQueryDepth++;
            try
            {
                object result = method.Invoke(player, new object[] { actionId });
                return result is float ? (float)result : 0f;
            }
            catch
            {
                return 0f;
            }
            finally
            {
                _rawQueryDepth--;
            }
        }

        static bool IsValidButton(int actionId)
        {
            return actionId >= 0 && actionId < 32;
        }
    }

    internal static class ClientInputFramePump
    {
        internal static void Update()
        {
            if (!MultiplayerSession.IsActive)
                return;
            if (Plugin.Net == null || !Plugin.Net.IsConnected)
                return;

            var packet = UniversalInputRouter.BuildLocalInputFrame();
            Plugin.Net.SendInputFrame(ref packet);
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetAxisPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetAxisMethod;
        }

        static void Postfix(object __instance, int actionId, ref float __result)
        {
            if (UniversalInputRouter.IsRawQuery || !MultiplayerSession.IsActive)
                return;

            PlayerId owner;
            if (!UniversalInputRouter.TryResolvePlayerId(__instance, out owner))
                return;

            float value;
            if (MultiplayerSession.IsNetworkControlledPlayer(owner))
            {
                if (UniversalInputRouter.TryGetRemoteAxis(owner, actionId, out value))
                    __result = value;
                return;
            }

            if (MultiplayerSession.IsLocalPlayer(owner)
             && (LocalDevSession.IsActive
                    ? UniversalInputRouter.TryGetLocalAxis(owner, actionId, out value)
                    : UniversalInputRouter.TryGetLocalAxis(actionId, out value)))
            {
                __result = value;
            }
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetButtonPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetButtonMethod;
        }

        static void Postfix(object __instance, int actionId, ref bool __result)
        {
            RouteButton(__instance, actionId, false, false, ref __result);
        }

        internal static void RouteButton(object rewiredPlayer, int actionId, bool down, bool up, ref bool result)
        {
            if (UniversalInputRouter.IsRawQuery || !MultiplayerSession.IsActive)
                return;

            PlayerId owner;
            if (!UniversalInputRouter.TryResolvePlayerId(rewiredPlayer, out owner))
                return;

            bool value;
            if (MultiplayerSession.IsNetworkControlledPlayer(owner))
            {
                if (UniversalInputRouter.TryGetRemoteButton(owner, actionId, down, up, out value))
                    result = value;
                return;
            }

            if (MultiplayerSession.IsLocalPlayer(owner)
             && (LocalDevSession.IsActive
                    ? UniversalInputRouter.TryGetLocalButton(owner, actionId, down, up, out value)
                    : UniversalInputRouter.TryGetLocalButton(actionId, down, up, out value)))
            {
                result = result || value;
            }
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetButtonDownPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetButtonDownMethod;
        }

        static void Postfix(object __instance, int actionId, ref bool __result)
        {
            RewiredPlayerGetButtonPatch.RouteButton(__instance, actionId, true, false, ref __result);
        }
    }

    [HarmonyPatch]
    public static class RewiredPlayerGetButtonUpPatch
    {
        static MethodBase TargetMethod()
        {
            return UniversalInputRouter.RewiredGetButtonUpMethod;
        }

        static void Postfix(object __instance, int actionId, ref bool __result)
        {
            RewiredPlayerGetButtonPatch.RouteButton(__instance, actionId, false, true, ref __result);
        }
    }

    [HarmonyPatch(typeof(CupheadInput), nameof(CupheadInput.InputDisplayForButton))]
    public static class CupheadInputDisplayForButtonPatch
    {
        static bool _insideFallback;

        static void Postfix(CupheadButton button, int rewiredPlayerId, ref Localization.Translation __result)
        {
            if (_insideFallback || HasText(__result))
                return;

            if (rewiredPlayerId != 0)
            {
                _insideFallback = true;
                try
                {
                    __result = CupheadInput.InputDisplayForButton(button, 0);
                }
                catch
                {
                }
                finally
                {
                    _insideFallback = false;
                }

                if (HasText(__result))
                    return;
            }

            __result = new Localization.Translation { text = FallbackText(button) };
        }

        static bool HasText(Localization.Translation translation)
        {
            return !string.IsNullOrEmpty(translation.text);
        }

        static string FallbackText(CupheadButton button)
        {
            switch (button)
            {
                case CupheadButton.Accept: return "Z";
                case CupheadButton.Cancel: return "ESC";
                case CupheadButton.EquipMenu: return "SHIFT";
                case CupheadButton.Pause: return "ESC";
                case CupheadButton.MenuUp: return "UP";
                case CupheadButton.MenuDown: return "DOWN";
                case CupheadButton.MenuLeft: return "LEFT";
                case CupheadButton.MenuRight: return "RIGHT";
                case CupheadButton.Jump: return "Z";
                case CupheadButton.Dash: return "SHIFT";
                case CupheadButton.Shoot: return "X";
                case CupheadButton.Super: return "V";
                default: return "Z";
            }
        }
    }
}
