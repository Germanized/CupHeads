using System;
using System.Collections.Generic;
using UnityEngine;

namespace CupheadOnline
{
    /// <summary>
    /// Central session state. This remains compatible with Cuphead's current two
    /// live gameplay slots, while exposing slot-aware helpers so the rest of the
    /// mod does not have to assume one magical "remote player" forever.
    /// </summary>
    public static class MultiplayerSession
    {
        static readonly PlayerId[] GameplayIds = { PlayerId.PlayerOne, PlayerId.PlayerTwo };
        static readonly Dictionary<byte, bool> _participantLocality = new Dictionary<byte, bool>(4);

        /// <summary>True while a network session (host OR client) is running.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>True when this instance is the authoritative host.</summary>
        public static bool IsHost { get; private set; }

        /// <summary>True when this instance is a connected client.</summary>
        public static bool IsClient => IsActive && !IsHost;

        /// <summary>Monotonically increasing FixedUpdate tick. Reset on session start.</summary>
        public static uint Tick { get; private set; }

        public static int GameplayCapacity => GameplayIds.Length;
        public static int ParticipantSlotCount => IsActive ? _participantLocality.Count : 0;

        // Current network role assignments still map onto Cuphead's two built-in slots.
        public static PlayerId LocalId  => IsHost ? PlayerId.PlayerOne : PlayerId.PlayerTwo;
        public static PlayerId RemoteId => IsHost ? PlayerId.PlayerTwo : PlayerId.PlayerOne;

        public static int ActivePlayerCount
        {
            get
            {
                int count = 0;
                if (HasPlayerSafe(PlayerId.PlayerOne)) count++;
                if (HasPlayerSafe(PlayerId.PlayerTwo)) count++;
                count += Sync.ExtraParticipantTracker.LiveCount;

                if (count > 0)
                    return count;

                bool multiplayer = false;
                try { multiplayer = PlayerManager.Multiplayer; }
                catch { }

                if (IsActive || multiplayer)
                    return Mathf.Max(GameplayCapacity, ParticipantSlotCount);

                return 1;
            }
        }

        public static event Action OnSessionStarted;
        public static event Action OnSessionEnded;

        public static void StartAsHost()
        {
            IsActive = true;
            IsHost   = true;
            Tick     = 0;
            RebuildParticipants();
            EnsureCupheadMultiplayerState();
            Plugin.Log.LogInfo("[Session] Started as HOST");
            OnSessionStarted?.Invoke();
        }

        public static void StartAsClient()
        {
            IsActive = true;
            IsHost   = false;
            Tick     = 0;
            RebuildParticipants();
            EnsureCupheadMultiplayerState();
            Plugin.Log.LogInfo("[Session] Started as CLIENT");
            OnSessionStarted?.Invoke();
        }

        public static void End()
        {
            if (!IsActive) return;
            IsActive = false;
            IsHost   = false;
            Tick     = 0;
            ClearParticipants();
            Plugin.Log.LogInfo("[Session] Ended");
            OnSessionEnded?.Invoke();
        }

        /// <summary>Called once per physics step from Plugin.FixedUpdate while a session is active.</summary>
        public static void IncrementTick() => Tick++;

        public static bool IsLocalPlayer(PlayerId id) => IsParticipant((byte)id, true);
        public static bool IsRemotePlayer(PlayerId id) => IsParticipant((byte)id, false);

        /// <summary>
        /// Alias for code that wants to know whether this slot is currently driven
        /// by network data rather than local gameplay input.
        /// </summary>
        public static bool IsNetworkControlledPlayer(PlayerId id) => IsRemotePlayer(id);
        public static bool IsNetworkControlledParticipant(byte participantId) => IsParticipant(participantId, false);

        public static bool IsAuthoritativePlayer(PlayerId id)
        {
            if (!IsActive || id == PlayerId.None)
                return false;

            if (IsHost && id <= PlayerId.PlayerTwo)
                return true;

            return IsLocalPlayer(id);
        }
        public static bool IsTrackedParticipant(byte participantId) => IsActive && _participantLocality.ContainsKey(participantId);

        /// <summary>
        /// Whose machine is the authority for this body's health, death and
        /// super meter. With latency-friendly damage (the default) every player
        /// owns their own body; in strict mode the host owns both.
        /// </summary>
        public static bool IsBodyOwnedLocally(PlayerId id)
        {
            if (!IsActive || id == PlayerId.None || id > PlayerId.PlayerTwo)
                return false;

            return Plugin.LatencyFriendlyDamage ? IsLocalPlayer(id) : IsHost;
        }

        public static void EnsureCupheadMultiplayerState()
        {
            if (!IsActive)
                return;

            try
            {
                PlayerManager.Multiplayer = true;
                PlayerManager.SetPlayerCanJoin(PlayerId.PlayerTwo, false, false);
                PlayerManager.SetPlayerCanSwitch(PlayerId.PlayerOne, false);
                PlayerManager.SetPlayerCanSwitch(PlayerId.PlayerTwo, false);
            }
            catch
            {
                // PlayerManager is not ready during very early boot. Scene patches
                // and Plugin.Update call this again once the game objects exist.
            }
        }

        public static bool TryGetParticipantIsLocal(PlayerId id, out bool isLocal)
        {
            return _participantLocality.TryGetValue((byte)id, out isLocal);
        }

        public static bool TryGetParticipantIsLocal(byte participantId, out bool isLocal)
        {
            return _participantLocality.TryGetValue(participantId, out isLocal);
        }

        public static void RegisterRemoteParticipant(byte participantId)
        {
            if (!IsActive)
                return;

            if (participantId <= (byte)PlayerId.PlayerTwo)
                return;

            _participantLocality[participantId] = false;
        }

        public static void UnregisterParticipant(byte participantId)
        {
            if (participantId <= (byte)PlayerId.PlayerTwo)
                return;

            _participantLocality.Remove(participantId);
        }

        public static PlayerId[] GetGameplayPlayerIds()
        {
            return (PlayerId[])GameplayIds.Clone();
        }

        public static PlayerId GetPrimaryRemoteGameplayId()
        {
            return RemoteId;
        }

        /// <summary>
        /// Safe way to get a player controller - returns null if not yet spawned.
        /// </summary>
        public static LevelPlayerController GetController(PlayerId id)
        {
            try
            {
                var player = PlayerManager.GetPlayer(id);
                return player as LevelPlayerController;
            }
            catch
            {
                return null;
            }
        }

        public static LevelPlayerController GetLocalController()
        {
            return GetController(LocalId);
        }

        public static LevelPlayerController GetRemoteController()
        {
            return GetController(RemoteId);
        }

        public static string GetPrimaryCharacterName()
        {
            return GetCharacterName(PlayerId.PlayerOne);
        }

        public static string GetSecondaryCharacterName()
        {
            return GetCharacterName(PlayerId.PlayerTwo);
        }

        public static string GetLocalCharacterName()
        {
            return GetCharacterName(IsActive ? LocalId : PlayerId.PlayerOne);
        }

        public static string GetRemoteCharacterName()
        {
            return GetCharacterName(IsActive ? RemoteId : PlayerId.PlayerTwo);
        }

        public static string GetCharacterName(PlayerId id)
        {
            try
            {
                var player = PlayerManager.GetPlayer(id);
                if (player != null && player.stats != null && player.stats.isChalice)
                    return "Ms. Chalice";

                if (PlayerData.Data != null && PlayerData.Data.Loadouts != null)
                {
                    var loadout = PlayerData.Data.Loadouts.GetPlayerLoadout(id);
                    if (loadout.charm == Charm.charm_chalice)
                        return "Ms. Chalice";
                }
            }
            catch
            {
            }

            bool playerOneIsMugman = PlayerManager.player1IsMugman;
            if (id == PlayerId.PlayerOne)
                return playerOneIsMugman ? "Mugman" : "Cuphead";
            if (id == PlayerId.PlayerTwo)
                return playerOneIsMugman ? "Cuphead" : "Mugman";
            return "Unknown";
        }

        static void RebuildParticipants()
        {
            _participantLocality.Clear();
            if (!IsActive)
                return;

            RegisterParticipant(LocalId, true);
            RegisterParticipant(RemoteId, false);
        }

        static void ClearParticipants()
        {
            _participantLocality.Clear();
        }

        static void RegisterParticipant(PlayerId id, bool isLocal)
        {
            _participantLocality[(byte)id] = isLocal;
        }

        static bool IsParticipant(byte participantId, bool localState)
        {
            bool isLocal;
            return IsActive
                && _participantLocality.TryGetValue(participantId, out isLocal)
                && isLocal == localState;
        }

        static bool HasPlayerSafe(PlayerId id)
        {
            try
            {
                return PlayerManager.GetPlayer(id) != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
