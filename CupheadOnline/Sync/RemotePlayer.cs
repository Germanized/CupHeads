using System;
using System.Collections.Generic;
using System.Reflection;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Manages visual state for network-controlled gameplay slots.
    ///
    /// Flow:
    ///   1. PacketDispatcher calls OnStateReceived() for incoming PlayerState packets.
    ///   2. Each slot buffers snapshots to absorb jitter.
    ///   3. PlayerMotorPatch consumes a snapshot for the slot it is proxying.
    ///   4. The patch applies position, look direction, and animation events.
    /// </summary>
    public static class RemotePlayer
    {
        const int MaxBuffer = 6;

        /// <summary>
        /// Jitter buffer depth scaled by measured ping: 1 snapshot on LAN-class
        /// connections, up to 4 on 150 ms+ links, so remote movement stays smooth
        /// on bad Wi-Fi without adding delay for everyone else.
        /// </summary>
        static int TargetBuffer
        {
            get
            {
                int latency = Plugin.Net != null ? Plugin.Net.Latency : 0;
                return UnityEngine.Mathf.Clamp(1 + latency / 50, 1, 4);
            }
        }

        sealed class RemotePlayerSlotState
        {
            public readonly Queue<PlayerStatePacket> Buffer = new Queue<PlayerStatePacket>(MaxBuffer);
            public PlayerStatePacket Last;
            public bool HasLast;
            public bool HasReceivedTick;
            public uint LastReceivedTick;
            public byte PrevFlags;
        }

        static readonly Dictionary<byte, RemotePlayerSlotState> _slotStates =
            new Dictionary<byte, RemotePlayerSlotState>(4);
        static readonly Dictionary<byte, PlayerStatePacket> _localAuthoritySnapshots =
            new Dictionary<byte, PlayerStatePacket>(2);
        static readonly Dictionary<byte, uint> _localAuthorityTicks =
            new Dictionary<byte, uint>(2);
        static readonly HashSet<string> _failedEvents =
            new HashSet<string>();

        // Reflection cache for raising VB.NET event backing delegates on LevelPlayerMotor.
        // VB compiles `Public Event Foo As Action` to a private field named `FooEvent`.
        static readonly FieldInfo FiGrounded;
        static readonly FieldInfo FiDashStart;
        static readonly FieldInfo FiDashEnd;
        static readonly FieldInfo FiHit;

        static RemotePlayer()
        {
            var t = typeof(LevelPlayerMotor);
            const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance;
            FiGrounded  = t.GetField("OnGroundedEventEvent",  bf) ?? t.GetField("OnGroundedEvent",  bf);
            FiDashStart = t.GetField("OnDashStartEventEvent", bf) ?? t.GetField("OnDashStartEvent", bf);
            FiDashEnd   = t.GetField("OnDashEndEventEvent",   bf) ?? t.GetField("OnDashEndEvent",   bf);
            FiHit       = t.GetField("OnHitEventEvent",       bf) ?? t.GetField("OnHitEvent",       bf);
            MultiplayerSession.OnSessionEnded += Reset;
        }

        public static void OnStateReceived(PlayerStatePacket pkt)
        {
            if (pkt.PlayerId > (byte)PlayerId.PlayerTwo)
            {
                var extraState = GetOrCreateState(pkt.PlayerId);
                if (extraState.HasReceivedTick && !NetTick.IsNewer(pkt.Tick, extraState.LastReceivedTick))
                    return;

                extraState.HasReceivedTick = true;
                extraState.LastReceivedTick = pkt.Tick;
                if (extraState.Buffer.Count >= MaxBuffer)
                    extraState.Buffer.Dequeue();

                extraState.Buffer.Enqueue(pkt);
                ExtraParticipantTracker.Apply(pkt);
                ExtraRemoteAvatarManager.NotifyState(pkt);
                return;
            }

            var playerId = (PlayerId)pkt.PlayerId;
            if (MultiplayerSession.IsHost && playerId <= PlayerId.PlayerTwo)
                return;

            if (MultiplayerSession.IsClient && MultiplayerSession.IsLocalPlayer(playerId))
            {
                StoreLocalAuthoritySnapshot(pkt);
                return;
            }

            if (!MultiplayerSession.IsNetworkControlledPlayer(playerId))
                return;

            var state = GetOrCreateState(playerId);
            if (state.HasReceivedTick && !NetTick.IsNewer(pkt.Tick, state.LastReceivedTick))
                return;

            state.HasReceivedTick = true;
            state.LastReceivedTick = pkt.Tick;
            if (state.Buffer.Count >= MaxBuffer)
                state.Buffer.Dequeue();

            state.Buffer.Enqueue(pkt);
        }

        /// <summary>
        /// Called by PlayerMotorPatch every FixedUpdate for the proxy motor.
        /// Returns null if the slot has no buffered snapshot.
        /// </summary>
        public static PlayerStatePacket? GetNextSnapshot(PlayerId playerId)
        {
            return GetNextSnapshot((byte)playerId);
        }

        public static PlayerStatePacket? GetNextSnapshot(byte participantId)
        {
            var state = GetOrCreateState(participantId);

            while (state.Buffer.Count > TargetBuffer + 2 && state.Buffer.Count > 1)
                state.Last = state.Buffer.Dequeue();

            if (state.Buffer.Count > 0)
            {
                state.Last = state.Buffer.Dequeue();
                state.HasLast = true;
                return state.Last;
            }

            return state.HasLast ? (PlayerStatePacket?)state.Last : null;
        }

        public static bool TryGetLocalAuthoritySnapshot(PlayerId playerId, out PlayerStatePacket snapshot)
        {
            return TryGetLocalAuthoritySnapshot((byte)playerId, out snapshot);
        }

        public static bool TryGetLocalAuthoritySnapshot(byte participantId, out PlayerStatePacket snapshot)
        {
            return _localAuthoritySnapshots.TryGetValue(participantId, out snapshot);
        }

        /// <summary>
        /// Detects transitions in the proxy player's motor flags and raises the
        /// corresponding events on LevelPlayerMotor so animation reacts normally.
        /// </summary>
        public static void UpdateStateTransitions(PlayerId playerId, LevelPlayerMotor motor, PlayerStatePacket snapshot)
        {
            UpdateStateTransitions((byte)playerId, motor, snapshot);
        }

        public static void UpdateStateTransitions(byte participantId, LevelPlayerMotor motor, PlayerStatePacket snapshot)
        {
            var state = GetOrCreateState(participantId);
            byte prev = state.PrevFlags;
            byte cur  = snapshot.Flags;

            bool wasGrounded = (prev & 1)  != 0;
            bool nowGrounded = (cur  & 1)  != 0;
            bool wasDashing  = (prev & 2)  != 0;
            bool nowDashing  = (cur  & 2)  != 0;
            bool wasHit      = (prev & 16) != 0;
            bool nowHit      = (cur  & 16) != 0;

            if (!wasGrounded && nowGrounded) RaiseEvent(motor, FiGrounded);
            if (!wasDashing  && nowDashing)  RaiseEvent(motor, FiDashStart);
            if (wasDashing   && !nowDashing) RaiseEvent(motor, FiDashEnd);
            if (!wasHit      && nowHit)      RaiseEvent(motor, FiHit);

            state.PrevFlags = cur;
        }

        public static void Reset()
        {
            _slotStates.Clear();
            _localAuthoritySnapshots.Clear();
            _localAuthorityTicks.Clear();
            _failedEvents.Clear();
        }

        public static void Reset(PlayerId playerId)
        {
            _slotStates.Remove((byte)playerId);
            _localAuthoritySnapshots.Remove((byte)playerId);
            _localAuthorityTicks.Remove((byte)playerId);
        }

        public static void Reset(byte participantId)
        {
            _slotStates.Remove(participantId);
            _localAuthoritySnapshots.Remove(participantId);
            _localAuthorityTicks.Remove(participantId);
        }

        static RemotePlayerSlotState GetOrCreateState(PlayerId playerId)
        {
            return GetOrCreateState((byte)playerId);
        }

        static RemotePlayerSlotState GetOrCreateState(byte participantId)
        {
            RemotePlayerSlotState state;
            if (!_slotStates.TryGetValue(participantId, out state))
            {
                state = new RemotePlayerSlotState();
                _slotStates[participantId] = state;
            }
            return state;
        }

        static void StoreLocalAuthoritySnapshot(PlayerStatePacket pkt)
        {
            uint lastTick;
            if (_localAuthorityTicks.TryGetValue(pkt.PlayerId, out lastTick)
             && !NetTick.IsNewer(pkt.Tick, lastTick))
            {
                return;
            }

            _localAuthorityTicks[pkt.PlayerId] = pkt.Tick;
            _localAuthoritySnapshots[pkt.PlayerId] = pkt;
        }

        static void RaiseEvent(LevelPlayerMotor motor, FieldInfo fi)
        {
            if (fi == null) return;
            if (_failedEvents.Contains(fi.Name)) return;

            try
            {
                var del = fi.GetValue(motor) as Action;
                if (del != null)
                    del();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[RemotePlayer] Event raise failed: " + ex.Message);
                _failedEvents.Add(fi.Name);
            }
        }
    }
}
