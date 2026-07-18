using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using CupheadOnline.Patches;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Lets a knocked-out player steer their floating revive ghost sideways with
    /// their movement axis, so waiting for a parry-revive feels less passive and
    /// they can drift toward their partner.
    /// </summary>
    public static class GhostDriftController
    {
        const float DRIFT_SPEED = 190f;
        const float MAX_DRIFT = 320f;

        static readonly FieldInfo PlayerIdField = typeof(PlayerDeathEffect).GetField(
            "playerId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        sealed class GhostState
        {
            public PlayerDeathEffect Effect;
            public PlayerId OwnerId;
            public float OriginX;
        }

        static readonly List<GhostState> _ghosts = new List<GhostState>(2);

        static GhostDriftController()
        {
            MultiplayerSession.OnSessionEnded += Clear;
        }

        /// <summary>Called from the PlayerDeathEffect.Start postfix patch.</summary>
        public static void Register(PlayerDeathEffect effect)
        {
            if (!Plugin.EnableGhostDrift || effect == null || PlayerIdField == null)
                return;

            PlayerId ownerId;
            try { ownerId = (PlayerId)PlayerIdField.GetValue(effect); }
            catch { return; }

            if (ownerId != PlayerId.PlayerOne && ownerId != PlayerId.PlayerTwo)
                return;

            _ghosts.Add(new GhostState
            {
                Effect = effect,
                OwnerId = ownerId,
                OriginX = effect.transform.position.x,
            });
        }

        public static void Update()
        {
            if (_ghosts.Count == 0)
                return;

            float dt = Time.deltaTime;
            for (int i = _ghosts.Count - 1; i >= 0; i--)
            {
                var ghost = _ghosts[i];
                if (ghost.Effect == null || ghost.Effect.gameObject == null || !ghost.Effect.gameObject.activeInHierarchy)
                {
                    _ghosts.RemoveAt(i);
                    continue;
                }

                float axis = ReadOwnerAxis(ghost.OwnerId);
                if (Mathf.Abs(axis) < 0.25f)
                    continue;

                var pos = ghost.Effect.transform.position;
                pos.x = Mathf.Clamp(
                    pos.x + axis * DRIFT_SPEED * dt,
                    ghost.OriginX - MAX_DRIFT,
                    ghost.OriginX + MAX_DRIFT);
                ghost.Effect.transform.position = pos;
            }
        }

        static float ReadOwnerAxis(PlayerId ownerId)
        {
            if (MultiplayerSession.IsActive)
            {
                if (MultiplayerSession.IsNetworkControlledPlayer(ownerId))
                {
                    Net.InputFramePacket input;
                    if (RemoteInputDriver.TryGetCurrent(ownerId, out input))
                        return input.AxisX;
                    return 0f;
                }

                if (!MultiplayerSession.IsLocalPlayer(ownerId))
                    return 0f;
            }
            else if (ownerId != PlayerId.PlayerOne)
            {
                return 0f;
            }

            float value;
            UniversalInputRouter.TryGetLocalAxis(0, out value);
            return value;
        }

        static void Clear()
        {
            _ghosts.Clear();
        }
    }
}
