using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;
using CupheadOnline.Sync;
using CupheadOnline.UI;

namespace CupheadOnline.Net
{
    // ────────────────────────────────────────────────────────────────────────────
    //  HANDSHAKE STATE MACHINE
    //
    //  HOST side:
    //    Idle
    //    → CreatingLobby   (CreateLobby call in flight)
    //    → WaitingInLobby  (lobby ready, invite overlay shown, waiting for knock)
    //    → WaitingHello    (P2PSessionRequest accepted)
    //    → WaitingReady    (Welcome sent, waiting for Ready)
    //    → Connected       (Ready received, game can sync)
    //
    //  CLIENT side:
    //    Idle
    //    → JoiningLobby    (JoinLobby call in flight)
    //    → WaitingWelcome  (Hello sent, waiting for Welcome)
    //    → Connected       (Ready sent and first game-packet ACKed)
    //
    //  Any failure/timeout at any state → Error state, status text updated.
    //  Disconnect while Connected → back to WaitingInLobby (host) or Error (client).
    // ────────────────────────────────────────────────────────────────────────────

    public sealed class SteamNetManager
    {
        // ── Public ────────────────────────────────────────────────────────────
        public bool IsSteamReady => _steamReady;
        public bool IsConnected  => _state == NetState.Connected;
        public bool IsHost       => _isHost;
        public int  Latency      { get; private set; }
        public string SteamUnavailableStatus => _steamUnavailableStatus;
        public string LastStatusMessage => _lastStatusMessage;
        public string LastFailureReason => _lastFailureReason;
        public string CurrentStateName => _state.ToString();
        public string CurrentPeerName => FriendName(_peerId);
        public string CurrentLobbyId => _lobbyId == CSteamID.Nil ? string.Empty : _lobbyId.m_SteamID.ToString();
        public int LobbyMemberCount => GetLobbyMemberCount();
        public int ConnectedPeerCount => _isHost ? CountHostPeers(HostPeerStage.Connected) : (_state == NetState.Connected && _peerId != CSteamID.Nil ? 1 : 0);
        public int PendingPeerCount => _isHost ? CountPendingHostPeers() : 0;
        public string CurrentPeerSummary => BuildCurrentPeerSummary();

        public bool CanInviteFriend => _steamReady && _isHost && _lobbyId != CSteamID.Nil;
        public bool CanCopyLobbyId => _steamReady && _lobbyId != CSteamID.Nil;
        public bool CanRetryLastAction => _lastRetryIntent != RetryIntent.None;
        public bool CanOpenSaveSlot => _steamReady && _state == NetState.Connected && _isHost;
        public bool CanRequestRecovery => _steamReady && _state == NetState.Connected;

        /// <summary>True while a critical async operation is in flight (host/join pending).</summary>
        public bool IsInputLocked => _state == NetState.CreatingLobby
                                  || _state == NetState.JoiningLobby
                                  || _state == NetState.WaitingHello
                                  || _state == NetState.WaitingWelcome
                                  || _state == NetState.WaitingReady;

        /// <summary>True when waiting in lobby indefinitely (no handshake timeout applies).</summary>
        public bool IsWaitingIndefinitely => _state == NetState.WaitingInLobby;

        /// <summary>True when we are in a Steam lobby (host or guest).</summary>
        public bool IsInLobby => _lobbyId != CSteamID.Nil;

        /// <summary>When the current state was entered (Time.realtimeSinceStartup).</summary>
        public float StateEnteredTime => _stateEnteredTime;

        public bool ShouldForceUnlockUi(float now)
        {
            if (!_steamReady) return false;

            switch (_state)
            {
                case NetState.CreatingLobby:
                case NetState.JoiningLobby:
                    return now - _stateEnteredTime > LOBBY_TIMEOUT + 5f;

                case NetState.WaitingHello:
                case NetState.WaitingWelcome:
                case NetState.WaitingReady:
                    return now - _stateEnteredTime > HANDSHAKE_TIMEOUT + 5f;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns a multi-line string with lobby member names and connection state,
        /// suitable for the presence panel in the MP menu.
        /// Returns empty string when not in a lobby.
        /// </summary>
        public string GetLobbyPresence()
        {
            if (!_steamReady || _lobbyId == CSteamID.Nil) return string.Empty;
            int n = GetLobbyMemberCount();
            if (n <= 0) return string.Empty;

            CSteamID localId = SteamUser.GetSteamID();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("LOBBY #" + _lobbyId.m_SteamID);
            if (_state == NetState.Connected)
            {
                sb.AppendLine(Latency > 0 ? "PING " + Latency + "ms" : "PING measuring...");
                if (SessionSync.HasTrackedSave)
                    sb.AppendLine("SAVE SYNCED - READY TO START");
            }
            for (int i = 0; i < n; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(_lobbyId, i);
                string line = BuildLobbyPresenceLine(member, localId);
                if (!string.IsNullOrEmpty(line))
                    sb.AppendLine(line);
            }
            return sb.ToString().TrimEnd();
        }

        public void NotifyLocalAppearanceChanged()
        {
            PublishLocalLobbyMemberData();
            if (_isHost)
                RefreshHostLobbyRoster();
        }

        public int GetParticipantColorSelection(byte participantId)
        {
            if (participantId == INVALID_PARTICIPANT_ID)
                return PlayerColorSync.AutoSelection;

            if (MultiplayerSession.IsActive && participantId == (byte)MultiplayerSession.LocalId)
                return PlayerColorSync.NormalizeSelection(Plugin.PreferredPlayerColorSelection);

            CSteamID member;
            if (TryGetLobbyMemberForParticipant(participantId, out member))
                return GetLobbyMemberPreferredColorSelection(member);

            if (_peerId != CSteamID.Nil)
            {
                if (_isHost && participantId == (byte)PlayerId.PlayerTwo)
                    return GetLobbyMemberPreferredColorSelection(_peerId);
                if (!_isHost && participantId == (byte)PlayerId.PlayerOne)
                    return GetLobbyMemberPreferredColorSelection(_peerId);
            }

            return PlayerColorSync.AutoSelection;
        }

        /// <summary>Fired on the main thread with human-readable status.</summary>
        public Action<string> OnStatusChanged;

        /// <summary>Fired on the main thread when the Steam overlay is closed by the user.</summary>
        public Action OnOverlayClosed;

        public string GetSteamBadgeText()
        {
            if (!_steamReady)
            {
                if (_steamUnavailableStatus.IndexOf("steam_appid.txt", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "NOT VIA STEAM";
                return "STEAM OFFLINE";
            }

            if (!IsOverlayEnabled())
                return "OVERLAY OFF";
            if (_state == NetState.Connected)
                return "CONNECTED";
            if (_lobbyId != CSteamID.Nil)
                return _isHost ? "HOSTING LOBBY" : "IN LOBBY";
            return "STEAM READY";
        }

        public string GetRetryActionLabel()
        {
            switch (_lastRetryIntent)
            {
                case RetryIntent.Host:
                    return _lastDisconnectWasConnected ? "REOPEN LOBBY" : "RETRY HOST";
                case RetryIntent.JoinLobby:
                    if (_lastDisconnectWasConnected)
                        return _lastRetryLobbyId == CSteamID.Nil ? "RECONNECT" : "REJOIN RUN";
                    return _lastRetryLobbyId == CSteamID.Nil ? "RETRY JOIN" : "REJOIN LOBBY";
                default:
                    return "RETRY LAST";
            }
        }

        public bool OpenInviteDialog(out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }
            if (!_isHost || _lobbyId == CSteamID.Nil)
            {
                status = "Host a lobby first, then invite a friend.";
                return false;
            }
            if (!IsOverlayEnabled())
            {
                status = "Steam overlay is unavailable.\nEnable the overlay and try again.";
                return false;
            }

            SteamFriends.ActivateGameOverlayInviteDialog(_lobbyId);
            status = "Invite dialog opened for lobby #" + _lobbyId.m_SteamID + ".";
            Plugin.LogVerbose("[SteamNet] Invite dialog opened.");
            return true;
        }

        public bool OpenFriendsOverlay(out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }
            if (!IsOverlayEnabled())
            {
                status = "Steam overlay is unavailable.\nEnable the overlay and try again.";
                return false;
            }

            SteamFriends.ActivateGameOverlay("Friends");
            status = "Steam overlay opened.\nWaiting for invite...";
            Plugin.LogVerbose("[SteamNet] Friends overlay opened.");
            return true;
        }

        public bool TryRetryLastAction(out string status)
        {
            status = string.Empty;

            switch (_lastRetryIntent)
            {
                case RetryIntent.Host:
                    if (StartHost())
                    {
                        status = "Retrying host setup...";
                        return true;
                    }
                    status = _steamUnavailableStatus;
                    return false;

                case RetryIntent.JoinLobby:
                    if (_lastRetryLobbyId == CSteamID.Nil)
                    {
                    status = "No previous lobby is available to rejoin yet.";
                    return false;
                    }
                    if (JoinLobby(_lastRetryLobbyId))
                    {
                        status = "Retrying lobby join...";
                        return true;
                    }
                    status = _steamUnavailableStatus;
                    return false;

                default:
                    status = "No previous action is available to retry.";
                    return false;
            }
        }

        public bool TryJoinLobbyById(string rawLobbyId, out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }

            CSteamID lobbyId;
            if (!TryParseLobbyId(rawLobbyId, out lobbyId))
            {
                status = "No Steam lobby ID was found in the clipboard.";
                return false;
            }

            if (JoinLobby(lobbyId))
            {
                status = "Joining lobby #" + lobbyId.m_SteamID + "...";
                return true;
            }

            status = _steamUnavailableStatus;
            return false;
        }

        public bool TryCopyLobbyId(out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }
            if (_lobbyId == CSteamID.Nil)
            {
                status = "Host or join a lobby first.";
                return false;
            }

            GUIUtility.systemCopyBuffer = "Lobby ID: " + _lobbyId.m_SteamID;
            status = "Lobby ID copied to clipboard.";
            return true;
        }

        public bool TryRequestRecovery(out string status)
        {
            status = string.Empty;
            if (!EnsureSteamReady())
            {
                status = _steamUnavailableStatus;
                return false;
            }
            if (_state != NetState.Connected)
            {
                status = "Connect first before requesting a resync.";
                return false;
            }

            status = SessionSync.RequestRecovery();
            return true;
        }

        public string BuildDiagnosticsReport()
        {
            var nl = Environment.NewLine;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Steam Ready: " + _steamReady);
            sb.AppendLine("State: " + _state);
            sb.AppendLine("Is Host: " + _isHost);
            sb.AppendLine("Overlay Enabled: " + IsOverlayEnabled());
            sb.AppendLine("Latency: " + Latency + "ms");
            sb.AppendLine("Lobby ID: " + (_lobbyId == CSteamID.Nil ? "(none)" : _lobbyId.m_SteamID.ToString()));
            sb.AppendLine("Peer: " + (_peerId == CSteamID.Nil ? "(none)" : FriendName(_peerId) + " [" + _peerId.m_SteamID + "]"));
            sb.AppendLine("Lobby Members: " + LobbyMemberCount);
            sb.AppendLine("Connected Peers: " + ConnectedPeerCount);
            sb.AppendLine("Pending Peers: " + PendingPeerCount);
            sb.AppendLine("Retry Action: " + GetRetryActionLabel());
            sb.AppendLine("Reconnect Available: " + _lastDisconnectWasConnected);
            sb.AppendLine("Last Status: " + (_lastStatusMessage ?? string.Empty));
            sb.AppendLine("Last Failure: " + (_lastFailureReason ?? string.Empty));

            if (!_steamReady)
                sb.AppendLine("Steam Status: " + _steamUnavailableStatus.Replace(nl, " | "));

            string presence = GetLobbyPresence();
            if (!string.IsNullOrEmpty(presence))
            {
                sb.AppendLine("Presence:");
                sb.AppendLine(presence);
            }

            string hostPeerSection = BuildHostPeerDiagnostics();
            if (!string.IsNullOrEmpty(hostPeerSection))
            {
                sb.AppendLine("Tracked Peers:");
                sb.AppendLine(hostPeerSection);
            }

            string sessionDiagnostics = SessionSync.BuildDiagnosticsSection();
            if (!string.IsNullOrEmpty(sessionDiagnostics))
            {
                sb.AppendLine("Session:");
                sb.AppendLine(sessionDiagnostics);
            }

            return sb.ToString().TrimEnd();
        }

        // ── State ─────────────────────────────────────────────────────────────
        enum NetState
        {
            Idle,
            CreatingLobby,
            WaitingInLobby,   // host: invite overlay shown
            JoiningLobby,     // client: JoinLobby call in flight
            WaitingHello,     // host: session accepted, waiting Hello
            WaitingWelcome,   // client: Hello sent, waiting Welcome
            WaitingReady,     // host: Welcome sent, waiting Ready
            Connected,
            Error,
        }

        enum RetryIntent
        {
            None,
            Host,
            JoinLobby,
        }

        enum HostPeerStage
        {
            Lobby,
            WaitingHello,
            WaitingReady,
            Connected,
        }

        sealed class HostPeerInfo
        {
            public CSteamID SteamId;
            public HostPeerStage Stage;
            public DateTime LastReceiveUtc;
            public float StageEnteredTime;
            public string CachedName;
        }

        NetState _state  = NetState.Idle;
        bool     _isHost;
        CSteamID _peerId  = CSteamID.Nil;
        CSteamID _lobbyId = CSteamID.Nil;
        RetryIntent _lastRetryIntent = RetryIntent.None;
        CSteamID    _lastRetryLobbyId = CSteamID.Nil;
        string      _lastRetryPeerName = string.Empty;
        bool        _lastDisconnectWasConnected;

        // ── Callbacks (hold references — prevent GC) ──────────────────────────
        Callback<P2PSessionRequest_t>      _cbP2PReq;
        Callback<P2PSessionConnectFail_t>  _cbP2PFail;
        Callback<GameLobbyJoinRequested_t> _cbLobbyJoinReq;
        Callback<LobbyChatUpdate_t>        _cbLobbyChatUpd;
        Callback<GameOverlayActivated_t>   _cbOverlay;
        CallResult<LobbyCreated_t>         _crLobbyCreated;
        CallResult<LobbyEnter_t>           _crLobbyEntered;

        // ── Buffers ───────────────────────────────────────────────────────────
        readonly byte[]       _recvBuf    = new byte[65536];
        readonly MemoryStream _sendBuf    = new MemoryStream(256);
        readonly System.IO.BinaryWriter _sendWriter;

        // ── Timing ────────────────────────────────────────────────────────────
        const float HANDSHAKE_TIMEOUT = 12f;   // seconds
        const float LOBBY_TIMEOUT     = 20f;
        const int   PEER_TIMEOUT_MS   = 30_000;
        const float PING_INTERVAL     = 3f;
        const int   MAX_LOBBY_MEMBERS = 8;
        const byte  INVALID_PARTICIPANT_ID = byte.MaxValue;
        const string LOBBY_KEY_ACTIVE_PEER = "active_peer";
        const string LOBBY_KEY_ACTIVE_MODE = "active_mode";
        const string LOBBY_KEY_PARTICIPANT_PREFIX = "participant_";
        const string LOBBY_MEMBER_KEY_COLOR = "preferred_color";

        // ── Auto-reconnect (client side) ──────────────────────────────────────
        const int   AUTO_RECONNECT_ATTEMPTS = 3;
        const float AUTO_RECONNECT_DELAY    = 3f;
        int   _autoReconnectAttemptsLeft;
        float _nextAutoReconnectAt;
        bool  _autoReconnectSuppressed;   // set when the peer refused us (version mismatch)

        float    _stateEnteredTime;            // Time.realtimeSinceStartup at state change
        DateTime _lastReceive  = DateTime.UtcNow;
        float    _nextPingTime;
        float    _nextQueuePollTime;
        DateTime _pingSentAt;
        bool     _pingSentPending;
        bool     _steamInitAttempted;
        bool     _steamReady;
        string   _steamUnavailableStatus = "Steam is unavailable.\nLaunch Cuphead through Steam.";
        string   _lastStatusMessage;
        string   _lastFailureReason;
        readonly Dictionary<ulong, HostPeerInfo> _hostPeers = new Dictionary<ulong, HostPeerInfo>();
        readonly Dictionary<ulong, byte> _peerSessionParticipantIds = new Dictionary<ulong, byte>();
        byte _nextSessionParticipantId = 2;

        // ── Constructor ───────────────────────────────────────────────────────

        public SteamNetManager()
        {
            _sendWriter = new System.IO.BinaryWriter(_sendBuf);
            Plugin.Log.LogInfo("[SteamNet] Created.");
        }

        public bool TryInitializeSteam()
        {
            if (_steamReady) return true;
            if (_steamInitAttempted) return false;

            _steamInitAttempted     = true;
            _steamUnavailableStatus = BuildSteamUnavailableStatus();

            try
            {
                if (!SteamAPI.Init())
                {
                    Plugin.Log.LogWarning("[SteamNet] SteamAPI.Init() returned false.");
                    Plugin.Log.LogWarning("[SteamNet] " + _steamUnavailableStatus.Replace('\n', ' '));
                    return false;
                }

                _steamReady      = true;
                _cbP2PReq        = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
                _cbP2PFail       = Callback<P2PSessionConnectFail_t>.Create(OnP2PConnectFailRaw);
                _cbLobbyJoinReq  = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequestedRaw);
                _cbLobbyChatUpd  = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdateRaw);
                _cbOverlay       = Callback<GameOverlayActivated_t>.Create(OnGameOverlayActivated);
                Plugin.Log.LogInfo("[SteamNet] Steam initialized.");
                return true;
            }
            catch (DllNotFoundException ex)
            {
                Plugin.Log.LogError("[SteamNet] Steam DLL missing: " + ex.Message);
                Plugin.Log.LogWarning("[SteamNet] " + _steamUnavailableStatus.Replace('\n', ' '));
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[SteamNet] Steam initialization failed: " + ex);
                Plugin.Log.LogWarning("[SteamNet] " + _steamUnavailableStatus.Replace('\n', ' '));
                return false;
            }
        }

        // ── Host flow ─────────────────────────────────────────────────────────

        public bool StartHost()
        {
            if (!EnsureSteamReady()) return false;

            _lastRetryIntent = RetryIntent.Host;
            _lastRetryLobbyId = CSteamID.Nil;
            _lastRetryPeerName = string.Empty;
            _lastDisconnectWasConnected = false;
            Shutdown();
            _isHost = true;
            _hostPeers.Clear();
            _nextQueuePollTime = 0f;
            SteamNetworking.AllowP2PPacketRelay(true);
            MultiplayerSession.StartAsHost();

            SetState(NetState.CreatingLobby, "Creating lobby...");
            _crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _crLobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MAX_LOBBY_MEMBERS));
            return true;
        }

        void OnLobbyCreated(LobbyCreated_t r, bool ioFail)
        {
            if (!_steamReady) return;

            if (ioFail || r.m_eResult != EResult.k_EResultOK)
            {
                MultiplayerSession.End();
                SetState(NetState.Error, DescribeLobbyCreateFailure(r.m_eResult, ioFail));
                Plugin.Log.LogWarning("[SteamNet] Lobby creation failed: " + r.m_eResult);
                return;
            }
            _lobbyId = new CSteamID(r.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(_lobbyId, "game", "CupheadOnline");
            SteamMatchmaking.SetLobbyData(_lobbyId, LOBBY_KEY_ACTIVE_MODE, "single-primary");
            PublishLocalLobbyMemberData();
            UpdateLobbyActivePeerData();
            RefreshHostLobbyRoster();

            string waitStatus =
                "Waiting for a gameplay peer...\n"
                + "Extra lobby members will queue automatically.\n"
                + "Use Invite Friend to send another Steam invite.";
            string copyStatus;
            if (TryCopyLobbyId(out copyStatus))
                waitStatus += "\n" + copyStatus;

            SetState(NetState.WaitingInLobby, waitStatus);
            Plugin.Log.LogInfo("[SteamNet] Lobby: " + _lobbyId);
            string inviteStatus;
            if (!OpenInviteDialog(out inviteStatus))
                FireStatus(inviteStatus);
        }

        void OnP2PSessionRequest(P2PSessionRequest_t req)
        {
            if (!_steamReady) return;

            if (_lobbyId != CSteamID.Nil && !IsLobbyMember(req.m_steamIDRemote))
            {
                Plugin.Log.LogWarning("[SteamNet] Rejected P2P from non-lobby peer.");
                return;
            }
            SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
            if (_isHost)
            {
                var info = GetOrCreateHostPeer(req.m_steamIDRemote);
                info.LastReceiveUtc = DateTime.UtcNow;

                if (_peerId == CSteamID.Nil || (_peerId == req.m_steamIDRemote && _state != NetState.Connected))
                {
                    _peerId = req.m_steamIDRemote;
                    UpdateHostPeerStage(req.m_steamIDRemote, HostPeerStage.WaitingHello);
                    UpdateLobbyActivePeerData();
                    SetState(NetState.WaitingHello, "Player connecting...");
                }
                else if (req.m_steamIDRemote != _peerId)
                {
                    UpdateHostPeerStage(req.m_steamIDRemote, HostPeerStage.WaitingHello);
                    Plugin.Log.LogInfo("[SteamNet] Accepted extra gameplay peer " + FriendName(req.m_steamIDRemote) + ".");
                }
            }
            else
            {
                _peerId = req.m_steamIDRemote;
            }

            Plugin.Log.LogInfo("[SteamNet] P2P accepted from " + FriendName(req.m_steamIDRemote));
        }

        // ── Client flow ───────────────────────────────────────────────────────

        public bool JoinLobby(CSteamID lobbyId)
        {
            if (!EnsureSteamReady()) return false;

            _lastRetryIntent = RetryIntent.JoinLobby;
            _lastRetryLobbyId = lobbyId;
            _lastDisconnectWasConnected = false;
            Shutdown();
            _isHost = false;
            _nextQueuePollTime = 0f;
            SteamNetworking.AllowP2PPacketRelay(true);
            SetState(NetState.JoiningLobby, "Joining lobby #" + lobbyId.m_SteamID + "...");
            _crLobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            _crLobbyEntered.Set(SteamMatchmaking.JoinLobby(lobbyId));
            return true;
        }

        // Thread-safe wrapper — Steam callback may arrive on any thread
        void OnLobbyJoinRequestedRaw(GameLobbyJoinRequested_t req)
        {
            var id = req.m_steamIDLobby;
            MainThreadQueue.Enqueue(() =>
            {
                Plugin.Log.LogInfo("[SteamNet] Invite accepted — joining " + id);
                JoinLobby(id);
            });
        }

        void OnLobbyEntered(LobbyEnter_t r, bool ioFail)
        {
            if (!_steamReady) return;

            if (ioFail || r.m_EChatRoomEnterResponse
                       != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                SetState(NetState.Error,
                    DescribeLobbyJoinFailure((EChatRoomEnterResponse)r.m_EChatRoomEnterResponse, ioFail));
                return;
            }
            _lobbyId = new CSteamID(r.m_ulSteamIDLobby);
            _peerId  = SteamMatchmaking.GetLobbyOwner(_lobbyId);
            _lastRetryPeerName = FriendName(_peerId);
            _nextQueuePollTime = 0f;
            PublishLocalLobbyMemberData();
            TryBeginClientHandshakeFromLobby(forceStatus: true);
        }

        // ── Handshake message handlers ────────────────────────────────────────

        void OnHelloReceived(CSteamID sender)
        {
            if (!_isHost || sender == CSteamID.Nil)
                return;

            if (_peerId == CSteamID.Nil)
                _peerId = sender;

            UpdateHostPeerStage(sender, HostPeerStage.WaitingReady);
            RawSendTo(sender, BuildHandshakePayload(PacketType.Welcome), reliable: true);
            if (sender == _peerId && _state != NetState.Connected)
                SetState(NetState.WaitingReady, "Almost there\u2026");
            else if (_state == NetState.Connected)
                FireStatus("Additional participant authorizing...");
            Plugin.Log.LogInfo("[SteamNet] Hello received from " + FriendName(sender) + ", Welcome sent.");
        }

        void OnWelcomeReceived()
        {
            // client ← Welcome
            RawSend(new[] { (byte)PacketType.Ready }, reliable: true);
            FinishConnect(_peerId);
            Plugin.Log.LogInfo("[SteamNet] Welcome received, Ready sent.");
        }

        void OnReadyReceived(CSteamID sender)
        {
            if (!_isHost || sender == CSteamID.Nil)
                return;

            if (_peerId == CSteamID.Nil)
                _peerId = sender;

            FinishConnect(sender);
            Plugin.Log.LogInfo("[SteamNet] Ready received from " + FriendName(sender) + " - fully connected.");
        }

        void FinishConnect(CSteamID connectedPeer)
        {
            if (_isHost)
            {
                if (!MultiplayerSession.IsActive || !MultiplayerSession.IsHost)
                    MultiplayerSession.StartAsHost();
            }
            else if (!MultiplayerSession.IsActive || MultiplayerSession.IsHost)
            {
                MultiplayerSession.StartAsClient();
            }

            _lastReceive = DateTime.UtcNow;
            _lastDisconnectWasConnected = false;
            string name  = FriendName(_isHost ? connectedPeer : _peerId);
            if (_isHost && connectedPeer != CSteamID.Nil)
            {
                UpdateHostPeerStage(connectedPeer, HostPeerStage.Connected);
                if (connectedPeer == _peerId)
                {
                    ReleaseSessionParticipantIdForPeer(connectedPeer);
                    UpdateLobbyActivePeerData();
                }
                else
                {
                    GetOrAssignSessionParticipantId(connectedPeer);
                }
                RefreshHostLobbyRoster();
            }
            SetState(
                NetState.Connected,
                _isHost
                    ? "Participant connected.\nSelect OPEN SAVE SLOT to choose a file."
                    : "Connected.\nWaiting for the host to choose a save slot.");

            ConnectionHUD.Show("Connected - " + (_isHost ? BuildCurrentPeerSummary() : name));
            SessionSync.OnConnected(_isHost);

            if (_isHost)
                SessionSync.BroadcastRecoveryBundle("Peer connected or reconnected.");
        }

        // ── Disconnect / failure ──────────────────────────────────────────────

        // Thread-safe wrapper
        void OnP2PConnectFailRaw(P2PSessionConnectFail_t cb)
        {
            var err = cb.m_eP2PSessionError;
            MainThreadQueue.Enqueue(() =>
            {
                Plugin.Log.LogWarning("[SteamNet] P2P fail, error=" + err);
                HandleDisconnect(DescribeP2PFailure(err));
            });
        }

        // Thread-safe wrapper
        void OnLobbyChatUpdateRaw(LobbyChatUpdate_t cb)
        {
            bool left = (cb.m_rgfChatMemberStateChange &
                         (uint)EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0
                     || (cb.m_rgfChatMemberStateChange &
                         (uint)EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0;
            bool entered = (cb.m_rgfChatMemberStateChange &
                            (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0;
            var changed = new CSteamID(cb.m_ulSteamIDUserChanged);
            MainThreadQueue.Enqueue(() =>
            {
                if (_isHost)
                    RefreshHostLobbyRoster();

                if (entered && changed != SteamUser.GetSteamID())
                {
                    string joinedName = FriendName(changed);
                    Plugin.Log.LogInfo("[SteamNet] " + joinedName + " joined the lobby.");
                    ConnectionHUD.Show(joinedName + " joined the lobby!");
                }

                if (!left)
                    return;

                string name = FriendName(changed);
                Plugin.Log.LogInfo("[SteamNet] " + name + " left the lobby.");
                if (changed == _peerId)
                    HandleDisconnect(name + " left the lobby.");
                else if (_isHost)
                    RemoveHostPeer(changed);
            });
        }

        // Thread-safe wrapper — fired when the Steam overlay opens or closes
        void OnGameOverlayActivated(GameOverlayActivated_t cb)
        {
            if (cb.m_bActive != 0) return;   // 0 = overlay closed
            MainThreadQueue.Enqueue(() => OnOverlayClosed?.Invoke());
        }

        /// <summary>
        /// Host-side: drop a peer whose build cannot play with ours, without
        /// tearing down the lobby — the slot reopens for a compatible player.
        /// </summary>
        void RejectIncompatiblePeer(CSteamID peer, string reason)
        {
            if (peer == CSteamID.Nil)
                return;

            try { SteamNetworking.CloseP2PSessionWithUser(peer); } catch { }
            RemoveHostPeer(peer);

            if (peer == _peerId)
            {
                _peerId = CSteamID.Nil;
                UpdateLobbyActivePeerData();
                if (_lobbyId != CSteamID.Nil)
                {
                    SetState(NetState.WaitingInLobby,
                        reason + "\n\nWaiting for a compatible player...");
                }
            }

            ConnectionHUD.Show(reason);
        }

        void HandleDisconnect(string reason)
        {
            bool wasConnected = _state == NetState.Connected;
            string friendlyReason = string.IsNullOrEmpty(reason) ? "Connection closed." : reason;
            _lastFailureReason = friendlyReason;
            _lastDisconnectWasConnected = wasConnected;
            CSteamID oldPeer = _peerId;
            if (_steamReady && oldPeer != CSteamID.Nil)
                SteamNetworking.CloseP2PSessionWithUser(oldPeer);
            if (_isHost && oldPeer != CSteamID.Nil)
            {
                if (IsLobbyMember(oldPeer))
                    UpdateHostPeerStage(oldPeer, HostPeerStage.Lobby);
                else
                    RemoveHostPeer(oldPeer);

                var otherPeers = new List<CSteamID>();
                foreach (var entry in _hostPeers.Values)
                {
                    if (entry == null || entry.SteamId == CSteamID.Nil || entry.SteamId == oldPeer)
                        continue;
                    otherPeers.Add(entry.SteamId);
                }

                for (int i = 0; i < otherPeers.Count; i++)
                {
                    var peer = otherPeers[i];
                    try { SteamNetworking.CloseP2PSessionWithUser(peer); } catch { }
                    if (IsLobbyMember(peer))
                        UpdateHostPeerStage(peer, HostPeerStage.Lobby);
                    else
                        RemoveHostPeer(peer);
                    ReleaseSessionParticipantIdForPeer(peer);
                }
            }
            ReleaseSessionParticipantIdForPeer(oldPeer);
            _peerId          = CSteamID.Nil;
            _pingSentPending = false;
            UpdateLobbyActivePeerData();
            if (_isHost)
                RefreshHostLobbyRoster();
            MultiplayerSession.End();

            if (_isHost && _lobbyId != CSteamID.Nil)
            {
                // Host stays in lobby — reset to WaitingInLobby so another player can join
                SetState(NetState.WaitingInLobby,
                    friendlyReason + "\n\nWaiting for the next gameplay peer...\n"
                    + "Extra lobby members will queue automatically.");
                if (wasConnected) ConnectionHUD.ShowDisconnected(friendlyReason);
            }
            else
            {
                // Client: arm auto-reconnect for unexpected drops of a live run.
                bool canAutoReconnect = Plugin.EnableAutoReconnect
                    && wasConnected
                    && !_autoReconnectSuppressed
                    && _lastRetryIntent == RetryIntent.JoinLobby
                    && _lastRetryLobbyId != CSteamID.Nil;

                if (canAutoReconnect)
                {
                    _autoReconnectAttemptsLeft = AUTO_RECONNECT_ATTEMPTS;
                    _nextAutoReconnectAt = Time.realtimeSinceStartup + AUTO_RECONNECT_DELAY;
                    SetState(NetState.Error,
                        friendlyReason + "\n\nReconnecting automatically...");
                }
                else
                {
                    SetState(NetState.Error,
                        friendlyReason + "\n\nUse Retry Last or Join Game to try again.");
                }

                if (wasConnected) ConnectionHUD.ShowDisconnected(friendlyReason);
            }
        }

        // ── Poll (every frame from Plugin.Update) ─────────────────────────────

        public void Poll()
        {
            if (!_steamReady) return;

            try
            {
                SteamAPI.RunCallbacks();
                DrainP2PPackets();
            }
            catch (InvalidOperationException ex)
            {
                HandleSteamRuntimeFailure("polling", ex);
                return;
            }

            // Auto-reconnect runs from the Error state, so check before bailing.
            if (_state == NetState.Error
             && _autoReconnectAttemptsLeft > 0
             && !_autoReconnectSuppressed
             && Time.realtimeSinceStartup >= _nextAutoReconnectAt)
            {
                int attempt = AUTO_RECONNECT_ATTEMPTS - _autoReconnectAttemptsLeft + 1;
                _autoReconnectAttemptsLeft--;
                _nextAutoReconnectAt = Time.realtimeSinceStartup + AUTO_RECONNECT_DELAY + LOBBY_TIMEOUT;
                var lobby = _lastRetryLobbyId;
                Plugin.Log.LogInfo("[SteamNet] Auto-reconnect attempt " + attempt + "/" + AUTO_RECONNECT_ATTEMPTS + "...");
                ConnectionHUD.Show("Reconnecting... attempt " + attempt + "/" + AUTO_RECONNECT_ATTEMPTS);
                int remainingAttempts = _autoReconnectAttemptsLeft;
                if (JoinLobby(lobby))
                {
                    // JoinLobby resets state; restore the remaining attempt budget
                    // so a failed rejoin can try again.
                    _autoReconnectAttemptsLeft = remainingAttempts;
                    _nextAutoReconnectAt = Time.realtimeSinceStartup + AUTO_RECONNECT_DELAY + LOBBY_TIMEOUT;
                }
            }

            if (_state == NetState.Idle || _state == NetState.Error) return;

            float now     = Time.realtimeSinceStartup;
            float elapsed = now - _stateEnteredTime;

            if (_state == NetState.WaitingInLobby && !_isHost && now >= _nextQueuePollTime)
            {
                _nextQueuePollTime = now + 1f;
                TryBeginClientHandshakeFromLobby(forceStatus: false);
            }

            // Handshake / lobby timeouts
            switch (_state)
            {
                case NetState.CreatingLobby:
                    if (elapsed > LOBBY_TIMEOUT)
                    {
                        MultiplayerSession.End();
                        SetState(NetState.Error,
                            "Steam took too long to create the lobby.\nUse Retry Last or Host Game to try again.");
                    }
                    return;

                case NetState.JoiningLobby:
                    if (elapsed > LOBBY_TIMEOUT)
                    {
                        MultiplayerSession.End();
                        SetState(NetState.Error,
                            "Steam took too long to join the lobby.\nUse Retry Last or Join Game to try again.");
                    }
                    return;

                case NetState.WaitingHello:
                case NetState.WaitingWelcome:
                case NetState.WaitingReady:
                    if (elapsed > HANDSHAKE_TIMEOUT)
                        HandleDisconnect("The handshake with " + FriendName(_peerId) + " timed out.");
                    return;

                case NetState.WaitingInLobby:
                    return;   // no timeout — host waits indefinitely
            }

            // Connected: keepalive + peer timeout check
            if ((DateTime.UtcNow - _lastReceive).TotalMilliseconds > PEER_TIMEOUT_MS)
            {
                HandleDisconnect(FriendName(_peerId) + " stopped responding.");
                return;
            }

            if (now > _nextPingTime)
            {
                _nextPingTime    = now + PING_INTERVAL;
                _pingSentAt      = DateTime.UtcNow;
                _pingSentPending = true;
                RawSend(new[] { (byte)PacketType.Ping }, reliable: false);
            }
        }

        void DrainP2PPackets()
        {
            if (!_steamReady) return;

            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size))
            {
                if (size > (uint)_recvBuf.Length)
                {
                    uint d1; CSteamID d2;
                    SteamNetworking.ReadP2PPacket(_recvBuf, (uint)_recvBuf.Length, out d1, out d2);
                    continue;
                }
                uint     read;
                CSteamID sender;
                if (SteamNetworking.ReadP2PPacket(_recvBuf, size, out read, out sender) && read > 0)
                    ProcessPacket(_recvBuf, (int)read, sender);
            }
        }

        bool IsConnectedHostPeer(CSteamID sender)
        {
            HostPeerInfo info;
            return _isHost
                && _hostPeers.TryGetValue(sender.m_SteamID, out info)
                && info != null
                && info.Stage == HostPeerStage.Connected;
        }

        byte GetDispatchParticipantIdForHostPeer(CSteamID sender)
        {
            if (sender == CSteamID.Nil)
                return INVALID_PARTICIPANT_ID;

            if (sender == _peerId)
                return (byte)PlayerId.PlayerTwo;

            return GetOrAssignSessionParticipantId(sender);
        }

        byte GetOrAssignSessionParticipantId(CSteamID sender)
        {
            if (sender == CSteamID.Nil)
                return INVALID_PARTICIPANT_ID;

            byte participantId;
            if (_peerSessionParticipantIds.TryGetValue(sender.m_SteamID, out participantId))
                return participantId;

            while (_nextSessionParticipantId <= (byte)PlayerId.PlayerTwo
                || _peerSessionParticipantIds.ContainsValue(_nextSessionParticipantId))
            {
                _nextSessionParticipantId++;
                if (_nextSessionParticipantId == INVALID_PARTICIPANT_ID)
                    return INVALID_PARTICIPANT_ID;
            }

            participantId = _nextSessionParticipantId;
            _peerSessionParticipantIds[sender.m_SteamID] = participantId;
            _nextSessionParticipantId++;
            return participantId;
        }

        void ReleaseSessionParticipantIdForPeer(CSteamID sender)
        {
            if (sender == CSteamID.Nil)
                return;

            byte participantId;
            if (_peerSessionParticipantIds.TryGetValue(sender.m_SteamID, out participantId))
            {
                _peerSessionParticipantIds.Remove(sender.m_SteamID);
                ExtraParticipantTracker.RemoveParticipant(participantId);
            }
        }

        bool TryGetPeerForParticipant(byte participantId, out CSteamID peer)
        {
            peer = CSteamID.Nil;

            if (!_isHost || participantId == INVALID_PARTICIPANT_ID)
                return false;

            if (participantId == (byte)PlayerId.PlayerTwo)
            {
                peer = _peerId;
                return peer != CSteamID.Nil;
            }

            foreach (var entry in _peerSessionParticipantIds)
            {
                if (entry.Value != participantId)
                    continue;

                peer = new CSteamID(entry.Key);
                return peer != CSteamID.Nil;
            }

            return false;
        }

        void BroadcastToConnectedPeers(byte[] data, bool reliable, CSteamID excludePeer)
        {
            if (!_steamReady || !_isHost)
                return;

            foreach (var entry in _hostPeers.Values)
            {
                if (entry == null
                 || entry.SteamId == CSteamID.Nil
                 || entry.Stage != HostPeerStage.Connected
                 || entry.SteamId == excludePeer)
                    continue;

                RawSendTo(entry.SteamId, data, reliable);
            }
        }

        void RelayParticipantPlayerState(PlayerStatePacket pkt, CSteamID sender)
        {
            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)PacketType.PlayerState);
            pkt.Write(_sendWriter);
            _sendWriter.Flush();

            int len = (int)_sendBuf.Length;
            var data = new byte[len];
            Buffer.BlockCopy(_sendBuf.GetBuffer(), 0, data, 0, len);
            BroadcastToConnectedPeers(data, reliable: false, excludePeer: sender);
        }

        void RelayParticipantWeaponEvent(WeaponEventPacket pkt, CSteamID sender)
        {
            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)PacketType.WeaponEvent);
            pkt.Write(_sendWriter);
            _sendWriter.Flush();

            int len = (int)_sendBuf.Length;
            var data = new byte[len];
            Buffer.BlockCopy(_sendBuf.GetBuffer(), 0, data, 0, len);
            BroadcastToConnectedPeers(data, reliable: true, excludePeer: sender);
        }

        void RelayParticipantPlayerStatus(PlayerStatusPacket pkt, CSteamID sender)
        {
            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)PacketType.PlayerStatus);
            pkt.Write(_sendWriter);
            _sendWriter.Flush();

            int len = (int)_sendBuf.Length;
            var data = new byte[len];
            Buffer.BlockCopy(_sendBuf.GetBuffer(), 0, data, 0, len);
            BroadcastToConnectedPeers(data, reliable: true, excludePeer: sender);
        }

        void HandleHostPlayerState(CSteamID sender, BinaryReader reader)
        {
            var incoming = new PlayerStatePacket();
            incoming.Read(reader);

            byte sessionParticipantId = sender == _peerId
                ? (byte)PlayerId.PlayerTwo
                : GetOrAssignSessionParticipantId(sender);
            if (sessionParticipantId == INVALID_PARTICIPANT_ID)
                return;

            if (sender != _peerId)
            {
                var extraState = incoming;
                extraState.PlayerId = sessionParticipantId;
                ExtraParticipantTracker.Apply(extraState);
            }

            if (ConnectedPeerCount <= 1)
                return;

            incoming.PlayerId = sessionParticipantId;
            RelayParticipantPlayerState(incoming, sender);
        }

        void HandleHostWeaponEvent(CSteamID sender, BinaryReader reader)
        {
            var incoming = new WeaponEventPacket();
            incoming.Read(reader);

            byte sessionParticipantId = sender == _peerId
                ? (byte)PlayerId.PlayerTwo
                : GetOrAssignSessionParticipantId(sender);

            if (sessionParticipantId == INVALID_PARTICIPANT_ID)
                return;

            incoming.PlayerId = sessionParticipantId;
            RemoteWeaponReplicator.Apply(incoming);

            if (ConnectedPeerCount <= 1)
                return;

            RelayParticipantWeaponEvent(incoming, sender);
        }

        void HandleHostPlayerStatus(CSteamID sender, BinaryReader reader)
        {
            var incoming = new PlayerStatusPacket();
            incoming.Read(reader);

            byte sessionParticipantId = sender == _peerId
                ? (byte)PlayerId.PlayerTwo
                : GetOrAssignSessionParticipantId(sender);

            if (sessionParticipantId == INVALID_PARTICIPANT_ID)
                return;

            incoming.ParticipantId = sessionParticipantId;
            ParticipantStatusTracker.Apply(incoming);

            if (ConnectedPeerCount <= 1)
                return;

            RelayParticipantPlayerStatus(incoming, sender);
        }

        void HandleHostReviveRequest(CSteamID sender, BinaryReader reader)
        {
            var incoming = new ReviveRequestPacket();
            incoming.Read(reader);

            byte sessionParticipantId = sender == _peerId
                ? (byte)PlayerId.PlayerTwo
                : GetOrAssignSessionParticipantId(sender);

            if (sessionParticipantId == INVALID_PARTICIPANT_ID)
                return;

            ParticipantReviveController.ResolveHostReviveRequest(
                sessionParticipantId,
                new Vector2(incoming.PosX, incoming.PosY),
                incoming.Tick,
                this);
        }

        void ProcessPacket(byte[] buf, int length, CSteamID sender)
        {
            if (length == 0) return;
            DateTime receivedAt = DateTime.UtcNow;
            if (_peerId == CSteamID.Nil || sender == _peerId || _state != NetState.Connected)
                _lastReceive = receivedAt;
            if (_isHost)
            {
                var info = GetOrCreateHostPeer(sender);
                info.LastReceiveUtc = receivedAt;
            }

            byte type = buf[0];

            // ── Handshake ─────────────────────────────────────────────────────
            if (type == (byte)PacketType.Hello)
            {
                if (!_isHost)
                    return;

                string peerVersion;
                string failReason;
                if (!TryValidateHandshake(buf, length, out peerVersion, out failReason))
                {
                    Plugin.Log.LogWarning("[SteamNet] Rejected " + FriendName(sender)
                        + " (v" + peerVersion + "): " + failReason);
                    SendVersionRejectTo(sender, failReason);
                    RejectIncompatiblePeer(sender, failReason);
                    return;
                }

                OnHelloReceived(sender);
                return;
            }
            if (type == (byte)PacketType.Welcome)
            {
                if (_isHost || _state != NetState.WaitingWelcome)
                    return;

                string peerVersion;
                string failReason;
                if (!TryValidateHandshake(buf, length, out peerVersion, out failReason))
                {
                    Plugin.Log.LogWarning("[SteamNet] Host build incompatible (v" + peerVersion + "): " + failReason);
                    _autoReconnectSuppressed = true;
                    HandleDisconnect(failReason);
                    return;
                }

                OnWelcomeReceived();
                return;
            }
            if (type == (byte)PacketType.Ready)
            {
                if (_isHost) OnReadyReceived(sender);
                return;
            }
            if (type == (byte)PacketType.VersionReject)
            {
                var reject = new VersionRejectPacket();
                try
                {
                    using (var ms = new MemoryStream(buf, 1, length - 1, false))
                    using (var r = new BinaryReader(ms))
                        reject.Read(r);
                }
                catch
                {
                    reject.Reason = "The other player's build refused the connection.";
                }

                string reason = string.IsNullOrEmpty(reject.Reason)
                    ? "Version mismatch with the other player."
                    : reject.Reason;
                Plugin.Log.LogWarning("[SteamNet] Connection refused by peer (v" + reject.Version + "): " + reason);
                _autoReconnectSuppressed = true;
                HandleDisconnect(reason);
                return;
            }

            // ── Ping / Pong ───────────────────────────────────────────────────
            if (type == (byte)PacketType.Ping)
            {
                if (_isHost)
                    RawSendTo(sender, new[] { (byte)PacketType.Pong }, reliable: false);
                else
                    RawSend(new[] { (byte)PacketType.Pong }, reliable: false);
                return;
            }
            if (type == (byte)PacketType.Pong && _pingSentPending && (!_isHost || sender == _peerId))
            {
                _pingSentPending = false;
                Latency = (int)(DateTime.UtcNow - _pingSentAt).TotalMilliseconds;
                ConnectionHUD.UpdatePing(Latency);
                return;
            }

            // ── Graceful disconnect ───────────────────────────────────────────
            if (type == (byte)PacketType.Disconnect)
            {
                if (sender == _peerId)
                    HandleDisconnect(FriendName(sender) + " disconnected.");
                else if (_isHost)
                    RemoveHostPeer(sender);
                return;
            }

            // ── Game packets — only accepted when fully connected ─────────────
            if (_state != NetState.Connected) return;
            if (_isHost)
            {
                if (!IsConnectedHostPeer(sender))
                    return;

                if (type == (byte)PacketType.PlayerState)
                {
                    using (var ms = new MemoryStream(buf, 1, length - 1, false))
                    using (var r = new BinaryReader(ms))
                        HandleHostPlayerState(sender, r);
                    return;
                }

                if (type == (byte)PacketType.WeaponEvent)
                {
                    using (var ms = new MemoryStream(buf, 1, length - 1, false))
                    using (var r = new BinaryReader(ms))
                        HandleHostWeaponEvent(sender, r);
                    return;
                }

                if (type == (byte)PacketType.PlayerStatus)
                {
                    using (var ms = new MemoryStream(buf, 1, length - 1, false))
                    using (var r = new BinaryReader(ms))
                        HandleHostPlayerStatus(sender, r);
                    return;
                }

                if (type == (byte)PacketType.ReviveRequest)
                {
                    using (var ms = new MemoryStream(buf, 1, length - 1, false))
                    using (var r = new BinaryReader(ms))
                        HandleHostReviveRequest(sender, r);
                    return;
                }
            }
            else if (_peerId != CSteamID.Nil && sender != _peerId)
            {
                return;
            }

            byte sourceParticipantId = INVALID_PARTICIPANT_ID;
            if (_isHost)
                sourceParticipantId = GetDispatchParticipantIdForHostPeer(sender);

            using (var ms = new MemoryStream(buf, 1, length - 1, false))
            using (var r  = new BinaryReader(ms))
                PacketDispatcher.Dispatch((PacketType)type, r, sourceParticipantId);
        }

        // ── Send helpers ──────────────────────────────────────────────────────

        public void SendPlayerState (ref PlayerStatePacket  p) => Send(PacketType.PlayerState,  ref p, false);
        public void SendInputFrame  (ref InputFramePacket   p) => Send(PacketType.InputFrame,   ref p, false);
        public void SendEnemyState  (ref EnemyStatePacket   p, bool reliable = false) => Send(PacketType.EnemyState,   ref p, reliable);
        public void SendWeaponEvent (ref WeaponEventPacket  p) => Send(PacketType.WeaponEvent,  ref p, true);
        public void SendDamageEvent (ref DamageEventPacket  p) => Send(PacketType.DamageEvent,  ref p, true);
        public void SendPlayerStatus(ref PlayerStatusPacket p) => Send(PacketType.PlayerStatus, ref p, true);
        public void SendReviveRequest(ref ReviveRequestPacket p) => Send(PacketType.ReviveRequest, ref p, true);
        public void SendReviveGrant(ref ReviveGrantPacket p) => Send(PacketType.ReviveGrant, ref p, true);
        public bool SendDamageEventForParticipant(byte participantId, float damage, byte source, uint tick)
        {
            return SendDamageEventForParticipant(participantId, damage, 0f, source, tick);
        }

        public bool SendDamageEventForParticipant(byte participantId, float damage, float stoneTime, byte source, uint tick)
        {
            if (!_steamReady || !_isHost || _state != NetState.Connected)
                return false;
            if (damage <= 0f && stoneTime <= 0f)
                return false;

            var pkt = new DamageEventPacket
            {
                Damage = damage,
                StoneTime = stoneTime,
                Source = source,
                Tick = tick,
            };

            if (participantId == (byte)PlayerId.PlayerOne)
            {
                pkt.TargetPlayerId = (byte)PlayerId.PlayerOne;
                Send(PacketType.DamageEvent, ref pkt, true);
                return true;
            }

            CSteamID targetPeer;
            if (!TryGetPeerForParticipant(participantId, out targetPeer))
                return false;

            pkt.TargetPlayerId = (byte)PlayerId.PlayerTwo;
            SendToPeer(PacketType.DamageEvent, ref pkt, true, targetPeer);
            return true;
        }
        public bool SendReviveGrantToParticipant(byte participantId, ref ReviveGrantPacket pkt)
        {
            if (!_steamReady || !_isHost || _state != NetState.Connected)
                return false;

            if (participantId == (byte)PlayerId.PlayerOne)
            {
                Send(PacketType.ReviveGrant, ref pkt, true);
                return true;
            }

            CSteamID targetPeer;
            if (!TryGetPeerForParticipant(participantId, out targetPeer))
                return false;

            SendToPeer(PacketType.ReviveGrant, ref pkt, true, targetPeer);
            return true;
        }
        public void SendSceneReady  (ref SceneReadyPacket   p) => Send(PacketType.SceneReady,   ref p, true);
        public void SendCommMessage (ref CommMessagePacket  p) => Send(PacketType.CommMessage,  ref p, true);
        public void SendStatsReport (ref StatsReportPacket  p) => Send(PacketType.StatsReport,  ref p, true);
        public void SendStateHash   (ref StateHashPacket    p) => Send(PacketType.StateHash,    ref p, false);
        public void SendSceneChange (ref SceneChangePacket  p) => Send(PacketType.SceneChange,  ref p, true);
        public void SendMenuSceneChange(ref MenuSceneChangePacket p) => Send(PacketType.MenuSceneChange, ref p, true);
        public void SendSaveSlotSync(ref SaveSlotSyncPacket p) => Send(PacketType.SaveSlotSync, ref p, true);
        public void SendSaveProfile(ref SaveProfilePacket p) => Send(PacketType.SaveProfile, ref p, true);
        public void SendLobbySync   (ref LobbySyncPacket    p) => Send(PacketType.LobbySync,    ref p, true);
        public void SendSessionSnapshot(ref SessionSnapshotPacket p, bool reliable = true) => Send(PacketType.SessionSnapshot, ref p, reliable);
        public void SendSessionSignal(ref SessionSignalPacket p) => Send(PacketType.SessionSignal, ref p, true);
        public void SendSessionStart(ref SessionStartPacket p) => Send(PacketType.SessionStart, ref p, true);

        void Send<T>(PacketType type, ref T pkt, bool reliable) where T : struct, IPacket
        {
            if (!_steamReady) return;
            if (_state != NetState.Connected && type != PacketType.SessionStart) return;
            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)type);
            pkt.Write(_sendWriter);
            _sendWriter.Flush();
            int len  = (int)_sendBuf.Length;
            var data = new byte[len];
            Buffer.BlockCopy(_sendBuf.GetBuffer(), 0, data, 0, len);
            if (_isHost)
                BroadcastToConnectedPeers(data, reliable, CSteamID.Nil);
            else
                RawSend(data, reliable);
        }

        void SendToPeer<T>(PacketType type, ref T pkt, bool reliable, CSteamID target) where T : struct, IPacket
        {
            if (!_steamReady || target == CSteamID.Nil)
                return;
            if (_state != NetState.Connected && type != PacketType.SessionStart)
                return;

            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)type);
            pkt.Write(_sendWriter);
            _sendWriter.Flush();
            int len = (int)_sendBuf.Length;
            var data = new byte[len];
            Buffer.BlockCopy(_sendBuf.GetBuffer(), 0, data, 0, len);
            RawSendTo(target, data, reliable);
        }

        void RawSend(byte[] data, bool reliable)
        {
            RawSendTo(_peerId, data, reliable);
        }

        void RawSendTo(CSteamID target, byte[] data, bool reliable)
        {
            if (!_steamReady) return;
            if (target == CSteamID.Nil) return;
            var mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable;
            SteamNetworking.SendP2PPacket(target, data, (uint)data.Length, mode);
        }

        // ── Shutdown ──────────────────────────────────────────────────────────

        public void Shutdown()
        {
            bool hadState = _state != NetState.Idle
                         || _peerId != CSteamID.Nil
                         || _lobbyId != CSteamID.Nil;

            if (_steamReady && _peerId != CSteamID.Nil)
            {
                try { SteamNetworking.SendP2PPacket(_peerId, new[] { (byte)PacketType.Disconnect }, 1, EP2PSend.k_EP2PSendReliable); } catch { }
                SteamNetworking.CloseP2PSessionWithUser(_peerId);
            }
            if (_steamReady && _isHost)
            {
                foreach (var entry in _hostPeers.Values)
                {
                    if (entry == null || entry.SteamId == CSteamID.Nil || entry.SteamId == _peerId)
                        continue;
                    try { SteamNetworking.CloseP2PSessionWithUser(entry.SteamId); } catch { }
                }
                UpdateLobbyActivePeerData(CSteamID.Nil);
            }
            if (_steamReady && _lobbyId != CSteamID.Nil)
            {
                SteamMatchmaking.LeaveLobby(_lobbyId);
            }
            _peerId          = CSteamID.Nil;
            _lobbyId         = CSteamID.Nil;
            _pingSentPending = false;
            _isHost          = false;
            Latency          = 0;
            _state           = NetState.Idle;
            _autoReconnectAttemptsLeft = 0;
            _autoReconnectSuppressed = false;
            _hostPeers.Clear();
            _peerSessionParticipantIds.Clear();
            _nextSessionParticipantId = 2;
            _nextQueuePollTime = 0f;
            ConnectionHUD.Hide();
            MultiplayerSession.End();
            if (hadState)
                Plugin.Log.LogInfo("[SteamNet] Shutdown.");
        }

        public void Dispose()
        {
            Shutdown();
            if (!_steamReady) return;

            try
            {
                SteamAPI.Shutdown();
                Plugin.Log.LogInfo("[SteamNet] Steam shutdown.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[SteamNet] Steam shutdown failed: " + ex.Message);
            }
            finally
            {
                _steamReady      = false;
                _cbP2PReq        = null;
                _cbP2PFail       = null;
                _cbLobbyJoinReq  = null;
                _cbLobbyChatUpd  = null;
                _cbOverlay       = null;
                _crLobbyCreated  = null;
                _crLobbyEntered  = null;
            }
        }

        /// <summary>Kept for source compat — Steam flow uses JoinLobby/invite.</summary>
        public void Connect(string ip, int port = 0) =>
            Plugin.Log.LogWarning("[SteamNet] Connect(ip) ignored — use Steam invite.");

        // ── Helpers ──────────────────────────────────────────────────────────

        bool TryBeginClientHandshakeFromLobby(bool forceStatus)
        {
            if (_isHost || !_steamReady || _lobbyId == CSteamID.Nil || _peerId == CSteamID.Nil)
                return false;

            if (_state != NetState.JoiningLobby && _state != NetState.WaitingInLobby)
                return false;

            ulong activePeerValue;
            if (TryGetActiveLobbyPeer(out activePeerValue) && activePeerValue != 0UL && activePeerValue != SteamUser.GetSteamID().m_SteamID)
            {
                if (forceStatus || _state != NetState.WaitingInLobby)
                    SetState(NetState.WaitingInLobby, BuildWaitingForOpenSlotStatus(activePeerValue));
                return false;
            }

            SetState(NetState.WaitingWelcome, "Connecting to " + FriendName(_peerId) + "...");
            RawSendTo(_peerId, BuildHandshakePayload(PacketType.Hello), reliable: true);
            Plugin.Log.LogInfo("[SteamNet] Hello sent to " + FriendName(_peerId)
                + " (v" + PluginInfo.VERSION + " proto " + NetProtocol.PROTOCOL + ")");
            return true;
        }

        /// <summary>[type, protocol, version-string] — both handshake messages carry build identity.</summary>
        byte[] BuildHandshakePayload(PacketType type)
        {
            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)type);
            _sendWriter.Write(NetProtocol.PROTOCOL);
            _sendWriter.Write(PluginInfo.VERSION);
            _sendWriter.Flush();
            int len = (int)_sendBuf.Length;
            var data = new byte[len];
            Buffer.BlockCopy(_sendBuf.GetBuffer(), 0, data, 0, len);
            return data;
        }

        /// <summary>
        /// Validates the peer's handshake identity. Returns false (and reports a
        /// human-readable reason) when the builds cannot play together.
        /// </summary>
        static bool TryValidateHandshake(byte[] buf, int length, out string peerVersion, out string failReason)
        {
            peerVersion = "unknown (pre-1.4.0)";
            failReason = string.Empty;

            if (length < 2)
            {
                failReason = "They are running an older CupHeads build. Both players need v" + PluginInfo.VERSION + ".";
                return false;
            }

            byte peerProtocol;
            try
            {
                using (var ms = new MemoryStream(buf, 1, length - 1, false))
                using (var r = new BinaryReader(ms))
                {
                    peerProtocol = r.ReadByte();
                    peerVersion = r.ReadString();
                }
            }
            catch
            {
                failReason = "Their CupHeads build sent a malformed handshake. Update both players to v" + PluginInfo.VERSION + ".";
                return false;
            }

            if (peerProtocol != NetProtocol.PROTOCOL)
            {
                failReason = "Version mismatch: you have v" + PluginInfo.VERSION
                    + ", they have v" + peerVersion + ". Both players need the same build.";
                return false;
            }

            return true;
        }

        void SendVersionRejectTo(CSteamID target, string reason)
        {
            if (target == CSteamID.Nil)
                return;

            var pkt = new VersionRejectPacket
            {
                Protocol = NetProtocol.PROTOCOL,
                Version = PluginInfo.VERSION,
                Reason = reason,
            };

            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)PacketType.VersionReject);
            pkt.Write(_sendWriter);
            _sendWriter.Flush();
            int len = (int)_sendBuf.Length;
            var data = new byte[len];
            Buffer.BlockCopy(_sendBuf.GetBuffer(), 0, data, 0, len);
            RawSendTo(target, data, reliable: true);
        }

        string BuildWaitingForOpenSlotStatus(ulong activePeerValue)
        {
            string activeName = "another player";
            if (activePeerValue != 0UL)
            {
                string resolved = FriendName(new CSteamID(activePeerValue));
                if (!string.IsNullOrEmpty(resolved) && resolved != "Unknown Player")
                    activeName = resolved;
            }

            return "Lobby joined.\n"
                + activeName
                + " is using the active gameplay slot.\nWaiting for the host to open the next slot...";
        }

        bool TryGetActiveLobbyPeer(out ulong activePeerValue)
        {
            activePeerValue = 0UL;
            if (!_steamReady || _lobbyId == CSteamID.Nil)
                return false;

            string raw = SteamMatchmaking.GetLobbyData(_lobbyId, LOBBY_KEY_ACTIVE_PEER);
            return ulong.TryParse(raw, out activePeerValue) && activePeerValue != 0UL;
        }

        void UpdateLobbyActivePeerData()
        {
            UpdateLobbyActivePeerData(_peerId);
        }

        void UpdateLobbyActivePeerData(CSteamID activePeer)
        {
            if (!_steamReady || !_isHost || _lobbyId == CSteamID.Nil)
                return;

            SteamMatchmaking.SetLobbyData(_lobbyId, LOBBY_KEY_ACTIVE_MODE, "single-primary");
            SteamMatchmaking.SetLobbyData(
                _lobbyId,
                LOBBY_KEY_ACTIVE_PEER,
                activePeer == CSteamID.Nil ? string.Empty : activePeer.m_SteamID.ToString());
        }

        string BuildLobbyPresenceLine(CSteamID member, CSteamID localId)
        {
            string name = member == localId ? "You" : FriendName(member);
            if (string.IsNullOrEmpty(name))
                name = "Unknown Player";

            byte participantId;
            bool hasParticipantId = TryGetLobbyMemberParticipantId(member, out participantId);
            int selection = hasParticipantId
                ? GetParticipantColorSelection(participantId)
                : GetLobbyMemberPreferredColorSelection(member);

            string line = PlayerColorSync.GetSwatchRichText(selection, hasParticipantId ? (byte?)participantId : null);
            if (hasParticipantId)
                line += " " + PlayerColorSync.GetParticipantLabel(participantId);

            line += " " + name + GetLobbyMemberSuffix(member, localId);
            return line;
        }

        string GetLobbyMemberSuffix(CSteamID member, CSteamID localId)
        {
            if (member == localId)
            {
                if (_isHost)
                    return " (Host)";
                if (!_isHost && _state == NetState.WaitingInLobby)
                    return " (Queued)";
                if (!_isHost && _state == NetState.WaitingWelcome)
                    return " (Connecting\u2026)";
                if (!_isHost && _state == NetState.Connected)
                    return " (You)";
                return string.Empty;
            }

            if (_isHost)
            {
                HostPeerInfo info;
                if (_hostPeers.TryGetValue(member.m_SteamID, out info))
                    return " (" + DescribeHostPeerStage(info) + ")";

                if (member == _peerId && _state == NetState.Connected)
                    return " (Active)";

                return string.Empty;
            }

            ulong activePeerValue;
            if (TryGetActiveLobbyPeer(out activePeerValue) && member.m_SteamID == activePeerValue)
                return " (Active)";

            if (member == _peerId)
            {
                if (_state == NetState.WaitingWelcome)
                    return " (Connecting\u2026)";
                if (_state == NetState.Connected)
                    return " (Host)";
            }

            return " (Queued)";
        }

        string DescribeHostPeerStage(HostPeerInfo info)
        {
            if (info == null)
                return "Unknown";

            switch (info.Stage)
            {
                case HostPeerStage.WaitingHello:
                    return "Connecting";
                case HostPeerStage.WaitingReady:
                    return "Authorizing";
                case HostPeerStage.Connected:
                    return info.SteamId == _peerId ? "Active" : "Connected";
                default:
                    return info.SteamId == _peerId ? "Connecting" : "Queued";
            }
        }

        int GetLobbyMemberCount()
        {
            if (!_steamReady || _lobbyId == CSteamID.Nil)
                return 0;

            try { return SteamMatchmaking.GetNumLobbyMembers(_lobbyId); }
            catch { return 0; }
        }

        int CountHostPeers(HostPeerStage stage)
        {
            int count = 0;
            foreach (var info in _hostPeers.Values)
            {
                if (info != null && info.Stage == stage)
                    count++;
            }
            return count;
        }

        int CountPendingHostPeers()
        {
            int count = 0;
            foreach (var info in _hostPeers.Values)
            {
                if (info != null && info.Stage != HostPeerStage.Connected)
                    count++;
            }
            return count;
        }

        string BuildCurrentPeerSummary()
        {
            if (_isHost)
            {
                if (_peerId != CSteamID.Nil && (_state == NetState.WaitingHello || _state == NetState.WaitingReady))
                {
                    if (PendingPeerCount > 1)
                        return FriendName(_peerId) + " connecting, " + (PendingPeerCount - 1) + " queued";
                    return FriendName(_peerId) + " connecting";
                }

                if (_peerId != CSteamID.Nil && _state == NetState.Connected)
                {
                    int extraConnected = Mathf.Max(0, ConnectedPeerCount - 1);
                    if (extraConnected > 0 && PendingPeerCount > 0)
                        return FriendName(_peerId) + " active, " + extraConnected + " extra connected, " + PendingPeerCount + " queued";
                    if (extraConnected > 0)
                        return FriendName(_peerId) + " active, " + extraConnected + " extra connected";
                    if (PendingPeerCount > 0)
                        return FriendName(_peerId) + " active, " + PendingPeerCount + " queued";
                    return FriendName(_peerId) + " active";
                }

                if (PendingPeerCount > 0)
                    return PendingPeerCount + " queued in lobby";

                return "No gameplay peer connected";
            }

            if (_state == NetState.WaitingInLobby)
                return "Queued for the next gameplay slot";
            if (_state == NetState.WaitingWelcome)
                return "Connecting to " + FriendName(_peerId);
            if (_state == NetState.Connected)
                return "Connected to " + FriendName(_peerId);
            return string.Empty;
        }

        string BuildHostPeerDiagnostics()
        {
            if (!_isHost || _hostPeers.Count == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (var entry in _hostPeers.Values)
            {
                if (entry == null)
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine();

                sb.Append(entry.CachedName);
                sb.Append(" [");
                sb.Append(entry.SteamId.m_SteamID);
                sb.Append("] - ");
                sb.Append(DescribeHostPeerStage(entry));
            }
            return sb.ToString();
        }

        HostPeerInfo GetOrCreateHostPeer(CSteamID id)
        {
            HostPeerInfo info;
            if (!_hostPeers.TryGetValue(id.m_SteamID, out info))
            {
                info = new HostPeerInfo
                {
                    SteamId = id,
                    Stage = HostPeerStage.Lobby,
                    LastReceiveUtc = DateTime.UtcNow,
                    StageEnteredTime = Time.realtimeSinceStartup,
                    CachedName = FriendName(id),
                };
                _hostPeers[id.m_SteamID] = info;
            }
            else if (string.IsNullOrEmpty(info.CachedName) || info.CachedName == "Unknown Player")
            {
                info.CachedName = FriendName(id);
            }

            return info;
        }

        void UpdateHostPeerStage(CSteamID id, HostPeerStage stage)
        {
            var info = GetOrCreateHostPeer(id);
            if (info.Stage != stage)
            {
                info.Stage = stage;
                info.StageEnteredTime = Time.realtimeSinceStartup;
            }
            info.LastReceiveUtc = DateTime.UtcNow;
            info.CachedName = FriendName(id);
        }

        void RemoveHostPeer(CSteamID id)
        {
            if (id == CSteamID.Nil)
                return;

            ReleaseSessionParticipantIdForPeer(id);
            _hostPeers.Remove(id.m_SteamID);
        }

        void RefreshHostLobbyRoster()
        {
            if (!_isHost || !_steamReady || _lobbyId == CSteamID.Nil)
                return;

            var localId = SteamUser.GetSteamID();
            var liveMembers = new HashSet<ulong>();
            var publishedParticipants = new HashSet<byte>();
            int lobbyMembers = GetLobbyMemberCount();
            for (int i = 0; i < lobbyMembers; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(_lobbyId, i);
                if (member == localId)
                    continue;

                liveMembers.Add(member.m_SteamID);
                var info = GetOrCreateHostPeer(member);
                info.CachedName = FriendName(member);
                if (member == _peerId)
                {
                    switch (_state)
                    {
                        case NetState.WaitingHello:
                            info.Stage = HostPeerStage.WaitingHello;
                            break;
                        case NetState.WaitingReady:
                            info.Stage = HostPeerStage.WaitingReady;
                            break;
                        case NetState.Connected:
                            info.Stage = HostPeerStage.Connected;
                            break;
                        default:
                            if (info.Stage != HostPeerStage.Connected)
                                info.Stage = HostPeerStage.Lobby;
                            break;
                    }
                }
                else if (info.Stage != HostPeerStage.Connected)
                {
                    info.Stage = HostPeerStage.Lobby;
                }
            }

            PublishLobbyParticipantSlot((byte)PlayerId.PlayerOne, localId, publishedParticipants);
            if (_peerId != CSteamID.Nil && IsLobbyMember(_peerId))
                PublishLobbyParticipantSlot((byte)PlayerId.PlayerTwo, _peerId, publishedParticipants);

            foreach (var entry in _peerSessionParticipantIds)
            {
                if (entry.Key == _peerId.m_SteamID || entry.Value == INVALID_PARTICIPANT_ID)
                    continue;

                var member = new CSteamID(entry.Key);
                if (!IsLobbyMember(member))
                    continue;

                PublishLobbyParticipantSlot(entry.Value, member, publishedParticipants);
            }

            ClearLobbyParticipantSlots(publishedParticipants);

            var removed = new List<ulong>();
            foreach (var key in _hostPeers.Keys)
            {
                if (!liveMembers.Contains(key))
                    removed.Add(key);
            }

            for (int i = 0; i < removed.Count; i++)
                RemoveHostPeer(new CSteamID(removed[i]));
        }

        void PublishLocalLobbyMemberData()
        {
            if (!_steamReady || _lobbyId == CSteamID.Nil)
                return;

            try
            {
                SteamMatchmaking.SetLobbyMemberData(
                    _lobbyId,
                    LOBBY_MEMBER_KEY_COLOR,
                    PlayerColorSync.NormalizeSelection(Plugin.PreferredPlayerColorSelection).ToString());
            }
            catch (Exception ex)
            {
                Plugin.LogVerbose("[SteamNet] Failed to publish lobby member appearance: " + ex.Message);
            }
        }

        void PublishLobbyParticipantSlot(byte participantId, CSteamID member, HashSet<byte> publishedParticipants)
        {
            if (!_steamReady || !_isHost || _lobbyId == CSteamID.Nil || member == CSteamID.Nil)
                return;

            SteamMatchmaking.SetLobbyData(
                _lobbyId,
                LOBBY_KEY_PARTICIPANT_PREFIX + participantId,
                member.m_SteamID.ToString());

            if (publishedParticipants != null)
                publishedParticipants.Add(participantId);
        }

        void ClearLobbyParticipantSlots(HashSet<byte> publishedParticipants)
        {
            if (!_steamReady || !_isHost || _lobbyId == CSteamID.Nil)
                return;

            for (byte participantId = 0; participantId < MAX_LOBBY_MEMBERS; participantId++)
            {
                if (publishedParticipants != null && publishedParticipants.Contains(participantId))
                    continue;

                SteamMatchmaking.DeleteLobbyData(_lobbyId, LOBBY_KEY_PARTICIPANT_PREFIX + participantId);
            }
        }

        int GetLobbyMemberPreferredColorSelection(CSteamID member)
        {
            if (!_steamReady || _lobbyId == CSteamID.Nil || member == CSteamID.Nil)
                return PlayerColorSync.AutoSelection;

            string raw = SteamMatchmaking.GetLobbyMemberData(_lobbyId, member, LOBBY_MEMBER_KEY_COLOR);
            int parsed;
            if (!int.TryParse(raw, out parsed))
                return PlayerColorSync.AutoSelection;

            return PlayerColorSync.NormalizeSelection(parsed);
        }

        bool TryGetLobbyMemberParticipantId(CSteamID member, out byte participantId)
        {
            participantId = INVALID_PARTICIPANT_ID;
            if (!_steamReady || _lobbyId == CSteamID.Nil || member == CSteamID.Nil)
                return false;

            CSteamID localId = SteamUser.GetSteamID();
            if (_isHost && member == localId)
            {
                participantId = (byte)PlayerId.PlayerOne;
                return true;
            }

            if (_isHost && member == _peerId && _peerId != CSteamID.Nil)
            {
                participantId = (byte)PlayerId.PlayerTwo;
                return true;
            }

            if (!_isHost && member == _peerId && _peerId != CSteamID.Nil)
            {
                participantId = (byte)PlayerId.PlayerOne;
                return true;
            }

            if (!_isHost && _state == NetState.Connected && member == localId)
            {
                participantId = (byte)PlayerId.PlayerTwo;
                return true;
            }

            for (byte candidate = 0; candidate < MAX_LOBBY_MEMBERS; candidate++)
            {
                ulong mappedSteamId;
                if (!TryGetLobbyParticipantSteamId(candidate, out mappedSteamId))
                    continue;

                if (mappedSteamId != member.m_SteamID)
                    continue;

                participantId = candidate;
                return true;
            }

            return false;
        }

        bool TryGetLobbyMemberForParticipant(byte participantId, out CSteamID member)
        {
            member = CSteamID.Nil;
            if (!_steamReady || _lobbyId == CSteamID.Nil || participantId == INVALID_PARTICIPANT_ID)
                return false;

            CSteamID localId = SteamUser.GetSteamID();
            if (_isHost && participantId == (byte)PlayerId.PlayerOne)
            {
                member = localId;
                return true;
            }

            if (!_isHost && participantId == (byte)PlayerId.PlayerTwo && _state == NetState.Connected)
            {
                member = localId;
                return true;
            }

            if (participantId == (byte)PlayerId.PlayerOne && !_isHost && _peerId != CSteamID.Nil)
            {
                member = _peerId;
                return true;
            }

            if (participantId == (byte)PlayerId.PlayerTwo && _isHost && _peerId != CSteamID.Nil)
            {
                member = _peerId;
                return true;
            }

            ulong mappedSteamId;
            if (!TryGetLobbyParticipantSteamId(participantId, out mappedSteamId) || mappedSteamId == 0UL)
                return false;

            member = new CSteamID(mappedSteamId);
            return member != CSteamID.Nil;
        }

        bool TryGetLobbyParticipantSteamId(byte participantId, out ulong steamIdValue)
        {
            steamIdValue = 0UL;
            if (!_steamReady || _lobbyId == CSteamID.Nil)
                return false;

            string raw = SteamMatchmaking.GetLobbyData(_lobbyId, LOBBY_KEY_PARTICIPANT_PREFIX + participantId);
            return ulong.TryParse(raw, out steamIdValue) && steamIdValue != 0UL;
        }

        void SetState(NetState s, string status)
        {
            _state            = s;
            _stateEnteredTime = Time.realtimeSinceStartup;
            _lastStatusMessage = status;
            if (s == NetState.Error)
                _lastFailureReason = status;
            FireStatus(status);
            Plugin.Log.LogInfo("[SteamNet] → " + s + ": " + status);
        }

        void FireStatus(string msg) => OnStatusChanged?.Invoke(msg);

        string FriendName(CSteamID id)
        {
            if (!_steamReady || id == CSteamID.Nil) return "Unknown Player";
            return SteamFriends.GetFriendPersonaName(id);
        }

        bool IsLobbyMember(CSteamID id)
        {
            if (!_steamReady) return false;
            if (_lobbyId == CSteamID.Nil) return true;
            int n = SteamMatchmaking.GetNumLobbyMembers(_lobbyId);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(_lobbyId, i) == id) return true;
            return false;
        }

        bool EnsureSteamReady()
        {
            if (_steamReady) return true;

            if (!_steamInitAttempted)
                TryInitializeSteam();

            if (_steamReady) return true;

            SetState(NetState.Error, _steamUnavailableStatus);
            return false;
        }

        bool IsOverlayEnabled()
        {
            if (!_steamReady) return false;
            try { return SteamUtils.IsOverlayEnabled(); }
            catch { return false; }
        }

        string BuildSteamUnavailableStatus()
        {
            string status = "Steam is unavailable.\nLaunch Cuphead through Steam.";

            try
            {
                string gameRoot = Path.GetDirectoryName(Application.dataPath);
                if (!string.IsNullOrEmpty(gameRoot))
                {
                    string appIdPath = Path.Combine(gameRoot, "steam_appid.txt");
                    if (!File.Exists(appIdPath))
                        status += "\nIf testing outside Steam, add steam_appid.txt next to Cuphead.exe.";
                }
            }
            catch
            {
                // Best-effort hint only.
            }

            return status;
        }

        bool TryParseLobbyId(string rawLobbyId, out CSteamID lobbyId)
        {
            lobbyId = CSteamID.Nil;
            if (string.IsNullOrEmpty(rawLobbyId) || rawLobbyId.Trim().Length == 0)
                return false;

            string raw = rawLobbyId.Trim();
            ulong value;
            if (ulong.TryParse(raw, out value) && value != 0UL)
            {
                lobbyId = new CSteamID(value);
                return true;
            }

            var match = System.Text.RegularExpressions.Regex.Match(raw, @"Lobby ID:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && ulong.TryParse(match.Groups[1].Value, out value) && value != 0UL)
            {
                lobbyId = new CSteamID(value);
                return true;
            }

            match = System.Text.RegularExpressions.Regex.Match(raw, @"\b(\d{8,})\b");
            if (match.Success && ulong.TryParse(match.Groups[1].Value, out value) && value != 0UL)
            {
                lobbyId = new CSteamID(value);
                return true;
            }

            return false;
        }

        string DescribeLobbyCreateFailure(EResult result, bool ioFail)
        {
            if (ioFail)
                return "Steam could not create the lobby.\nCheck Steam and try again.";

            switch (result)
            {
                case EResult.k_EResultNoConnection:
                    return "Steam is offline.\nReconnect Steam and try again.";
                case EResult.k_EResultTimeout:
                    return "Steam timed out while creating the lobby.\nUse Retry Last.";
                case EResult.k_EResultAccessDenied:
                    return "Steam blocked lobby creation.\nCheck the overlay and privacy settings.";
                default:
                    return "Steam could not create the lobby (" + result + ").\nUse Retry Last or Host Game to try again.";
            }
        }

        string DescribeLobbyJoinFailure(EChatRoomEnterResponse response, bool ioFail)
        {
            if (ioFail)
                return "Steam could not join the lobby.\nCheck Steam and try again.";

            switch (response)
            {
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist:
                    return "That Steam lobby no longer exists.\nAsk the host for a fresh invite.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseFull:
                    return "That Steam lobby is already full.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseBanned:
                    return "Steam reported that this account is blocked from the lobby.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseLimited:
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseCommunityBan:
                    return "Steam account restrictions prevented the lobby join.";
                case EChatRoomEnterResponse.k_EChatRoomEnterResponseNotAllowed:
                    return "Steam blocked the lobby join.\nCheck your invite and privacy settings.";
                default:
                    return "Steam could not join the lobby (" + response + ").\nUse Retry Last or Join Game to try again.";
            }
        }

        string DescribeP2PFailure(byte err)
        {
            switch ((EP2PSessionError)err)
            {
                case EP2PSessionError.k_EP2PSessionErrorTimeout:
                    return "Steam P2P timed out while contacting the other player.";
                case EP2PSessionError.k_EP2PSessionErrorNotRunningApp:
                    return "The other player is not running Cuphead with the mod yet.";
                case EP2PSessionError.k_EP2PSessionErrorNoRightsToApp:
                    return "Steam denied the P2P session for this app.";
                case EP2PSessionError.k_EP2PSessionErrorDestinationNotLoggedIn:
                    return "The other player's Steam session went offline.";
                case EP2PSessionError.k_EP2PSessionErrorMax:
                    return "Steam P2P reported an unknown error.";
                default:
                    return "Steam P2P failed (" + err + ").";
            }
        }

        void HandleSteamRuntimeFailure(string context, Exception ex)
        {
            if (!_steamReady) return;

            _steamReady = false;
            _peerId = CSteamID.Nil;
            _lobbyId = CSteamID.Nil;
            _isHost = false;
            _pingSentPending = false;
            _hostPeers.Clear();
            _peerSessionParticipantIds.Clear();
            _nextSessionParticipantId = 2;
            _nextQueuePollTime = 0f;
            _steamUnavailableStatus = BuildSteamUnavailableStatus();
            Plugin.Log.LogError("[SteamNet] Steam runtime failure while " + context + ": " + ex.Message);
            ConnectionHUD.Hide();
            MultiplayerSession.End();

            if (_state != NetState.Idle && _state != NetState.Error)
                SetState(NetState.Error, _steamUnavailableStatus);
        }
    }
}
