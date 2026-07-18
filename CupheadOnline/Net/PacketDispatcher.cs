using System.IO;
using CupheadOnline.Patches;
using CupheadOnline.Sync;
using CupheadOnline.UI;

namespace CupheadOnline.Net
{
    /// <summary>
    /// Routes incoming packets (already on main thread, called from NetManager.Poll)
    /// to the appropriate handler.
    /// </summary>
    public static class PacketDispatcher
    {
        public static void Dispatch(PacketType type, BinaryReader r, byte sourceParticipantId)
        {
            switch (type)
            {
                // ── Per-frame unreliable ──────────────────────────────────────
                case PacketType.PlayerState:
                {
                    var pkt = new PlayerStatePacket();
                    pkt.Read(r);
                    RemotePlayer.OnStateReceived(pkt);
                    break;
                }

                case PacketType.InputFrame:
                {
                    if (!MultiplayerSession.IsActive) break;
                    var pkt = new InputFramePacket();
                    pkt.Read(r);
                    byte participantId = sourceParticipantId == byte.MaxValue
                        ? (byte)MultiplayerSession.GetPrimaryRemoteGameplayId()
                        : sourceParticipantId;

                    if (participantId <= (byte)PlayerId.PlayerTwo
                     && !MultiplayerSession.IsNetworkControlledParticipant(participantId))
                        break;

                    RemoteInputDriver.Apply(participantId, pkt);
                    break;
                }

                case PacketType.EnemyState:
                {
                    // Only the CLIENT receives enemy states from the host
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new EnemyStatePacket();
                    pkt.Read(r);
                    EnemySyncManager.OnEnemyStateReceived(pkt);
                    break;
                }

                // ── Reliable events ───────────────────────────────────────────
                case PacketType.WeaponEvent:
                {
                    var pkt = new WeaponEventPacket();
                    pkt.Read(r);
                    RemoteWeaponReplicator.Apply(pkt);
                    break;
                }

                case PacketType.DamageEvent:
                {
                    var pkt = new DamageEventPacket();
                    pkt.Read(r);

                    if (MultiplayerSession.IsHost)
                    {
                        if (!Plugin.LatencyFriendlyDamage)
                            break;
                        if (!MultiplayerSession.IsNetworkControlledParticipant(pkt.TargetPlayerId))
                            break;
                        if (sourceParticipantId != byte.MaxValue && sourceParticipantId != pkt.TargetPlayerId)
                            break;

                        DamageAuthority.ApplyAuthorized(pkt);
                        break;
                    }

                    if (Plugin.LatencyFriendlyDamage
                     && pkt.TargetPlayerId <= (byte)PlayerId.PlayerTwo
                     && MultiplayerSession.IsLocalPlayer((PlayerId)pkt.TargetPlayerId))
                    {
                        break;
                    }

                    DamageAuthority.ApplyAuthorized(pkt);
                    break;
                }

                case PacketType.SceneChange:
                {
                    var pkt = new SceneChangePacket();
                    pkt.Read(r);
                    if (!System.Enum.IsDefined(typeof(Levels), pkt.LevelEnum))
                    {
                        Plugin.Log.LogWarning("[Dispatcher] Ignored invalid level change " + pkt.LevelEnum + ".");
                        break;
                    }

                    RngSync.SetSeed(pkt.RngSeed);
                    if (!MultiplayerSession.IsHost)
                    {
                        SceneSyncState.AllowNextClientLevelLoad();
                        SceneLoader.LoadLevel((Levels)pkt.LevelEnum, SceneLoader.Transition.Iris);
                    }
                    break;
                }

                case PacketType.MenuSceneChange:
                {
                    var pkt = new MenuSceneChangePacket();
                    pkt.Read(r);
                    if (!System.Enum.IsDefined(typeof(Scenes), pkt.SceneEnum))
                    {
                        Plugin.Log.LogWarning("[Dispatcher] Ignored invalid scene change " + pkt.SceneEnum + ".");
                        break;
                    }

                    RngSync.SetSeed(pkt.RngSeed);
                    if (!MultiplayerSession.IsHost)
                    {
                        SceneSyncState.AllowNextClientSceneLoad();
                        SceneLoader.LoadScene(
                            (Scenes)pkt.SceneEnum,
                            (SceneLoader.Transition)pkt.TransitionStart,
                            (SceneLoader.Transition)pkt.TransitionEnd,
                            (SceneLoader.Icon)pkt.Icon,
                            null);
                    }
                    break;
                }

                case PacketType.LobbySync:
                {
                    var pkt = new LobbySyncPacket();
                    pkt.Read(r);
                    LoadoutReplicator.Apply(pkt);
                    break;
                }

                case PacketType.SaveSlotSync:
                {
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new SaveSlotSyncPacket();
                    pkt.Read(r);
                    SaveSlotReplicator.Apply(pkt);
                    break;
                }

                case PacketType.SaveProfile:
                {
                    var pkt = new SaveProfilePacket();
                    pkt.Read(r);
                    SessionSync.ApplyRemoteSaveProfile(pkt);
                    break;
                }

                case PacketType.SessionSnapshot:
                {
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new SessionSnapshotPacket();
                    pkt.Read(r);
                    SessionSync.ApplyHostSnapshot(pkt);
                    break;
                }

                case PacketType.SessionSignal:
                {
                    var pkt = new SessionSignalPacket();
                    pkt.Read(r);
                    SessionSync.ApplySessionSignal(pkt);
                    break;
                }

                case PacketType.PlayerStatus:
                {
                    var pkt = new PlayerStatusPacket();
                    pkt.Read(r);
                    ParticipantStatusTracker.Apply(pkt);
                    break;
                }

                case PacketType.ReviveGrant:
                {
                    var pkt = new ReviveGrantPacket();
                    pkt.Read(r);
                    ParticipantReviveController.ApplyGrant(pkt);
                    break;
                }

                case PacketType.SessionStart:
                {
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new SessionStartPacket();
                    pkt.Read(r);
                    SessionSync.ApplySessionStart(pkt);
                    RngSync.SetSeed(pkt.RngSeed);
                    if (!MultiplayerSession.IsActive || MultiplayerSession.IsHost)
                        MultiplayerSession.StartAsClient();
                    if (pkt.IsInLevel && pkt.CurrentLevel >= 0)
                    {
                        if (!System.Enum.IsDefined(typeof(Levels), pkt.CurrentLevel))
                        {
                            Plugin.Log.LogWarning("[Dispatcher] Ignored invalid session-start level " + pkt.CurrentLevel + ".");
                            break;
                        }

                        SceneSyncState.AllowNextClientLevelLoad();
                        SceneLoader.LoadLevel((Levels)pkt.CurrentLevel, SceneLoader.Transition.Iris);
                    }
                    break;
                }

                case PacketType.SceneReady:
                {
                    if (!MultiplayerSession.IsHost) break;
                    var pkt = new SceneReadyPacket();
                    pkt.Read(r);
                    LevelReadyGate.OnGuestSceneReady(pkt);
                    break;
                }

                case PacketType.CommMessage:
                {
                    var pkt = new CommMessagePacket();
                    pkt.Read(r);
                    CommWheel.OnReceived(pkt);
                    break;
                }

                case PacketType.StatsReport:
                {
                    var pkt = new StatsReportPacket();
                    pkt.Read(r);
                    WinStatsCard.OnRemoteStats(pkt);
                    break;
                }

                case PacketType.StateHash:
                {
                    if (MultiplayerSession.IsHost) break;
                    var pkt = new StateHashPacket();
                    pkt.Read(r);
                    DesyncSentinel.OnHostSample(pkt);
                    break;
                }

                // Handshake packets are consumed by SteamNetManager before reaching here
                case PacketType.Hello:
                case PacketType.Welcome:
                case PacketType.Ready:
                case PacketType.VersionReject:
                case PacketType.Ping:
                case PacketType.Pong:
                case PacketType.ReviveRequest:
                    break;

                default:
                    Plugin.Log.LogWarning("[Dispatcher] Unknown packet type: " + type);
                    break;
            }
        }
    }
}
