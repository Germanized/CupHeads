using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// The guest plays on an in-memory mirror of the host's save.
    ///
    /// Cuphead co-op keeps both players' progress in one save file (the one
    /// chosen on the slot screen). Online, each machine used to run on its own
    /// save, so the two screens disagreed about coins, owned weapons and charms,
    /// unlocked map paths, completed levels and NPC dialogue state — the shop,
    /// equip cards, map and dialogue all diverged.
    ///
    /// Host: serialises the active slot (the same JSON the game writes to disk),
    /// gzips it and streams it in chunks whenever it saves or changes scene.
    /// Guest: snapshots all of its own save slots when the session starts,
    /// swaps the host's data in, blocks every write of its own save to disk for
    /// the session, and restores its original data in memory when the session
    /// ends. The guest's own save file is never touched.
    /// </summary>
    public static class SaveMirror
    {
        const float MinBroadcastInterval = 0.5f;
        const int MaxTransferBytes = 4 * 1024 * 1024;
        const int MaxChunks = 512;

        static readonly FieldInfo SaveFilesField = AccessTools.Field(typeof(PlayerData), "_saveFiles");

        // ── Host ─────────────────────────────────────────────────────────────
        static bool _broadcastQueued;
        static string _broadcastReason;
        static float _nextBroadcastAt;
        static ushort _nextTransferId = 1;

        // ── Guest ────────────────────────────────────────────────────────────
        static string[] _originalSlots;
        static string _originalDialoguer;
        static int _originalSaveIndex;
        static bool _hasOriginals;

        static ushort _incomingTransferId;
        static byte _incomingSlot;
        static int _incomingTotal;
        static byte[][] _incomingChunks;
        static int _incomingReceived;

        static PlayerData _pendingMirror;
        static int _pendingSlot = -1;
        static string _pendingDialoguer;

        public static int MirroredSlot { get; private set; } = -1;
        public static int MirrorsApplied { get; private set; }
        public static int MirrorsSent { get; private set; }

        public static bool IsGuestMirroring => MirroredSlot >= 0 && GuestSession;

        /// <summary>Guests never write their own save while connected.</summary>
        public static bool BlockLocalDiskSaves => GuestSession && _hasOriginals;

        static bool GuestSession => MultiplayerSession.IsClient && !LocalDevSession.IsActive;

        static SaveMirror()
        {
            MultiplayerSession.OnSessionStarted += OnSessionStarted;
            MultiplayerSession.OnSessionEnded += OnSessionEnded;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Host
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>Host: send a fresh copy of the save soon (coalesced).</summary>
        public static void QueueHostBroadcast(string reason)
        {
            if (!MultiplayerSession.IsHost || LocalDevSession.IsActive)
                return;

            _broadcastQueued = true;
            _broadcastReason = reason;
        }

        public static void Update()
        {
            if (!_selfTestDone && PlayerData.Initialized)
                RunSelfTest();

            if (MultiplayerSession.IsHost)
            {
                TickHostBroadcast();
                return;
            }

            if (_pendingMirror != null)
                TryApplyPending(force: false);

            if (_pendingDialoguer != null && Map.Current == null && !SceneLoader.CurrentlyLoading)
                ApplyPendingDialoguer();
        }

        static void TickHostBroadcast()
        {
            if (!_broadcastQueued || Plugin.Net == null || !Plugin.Net.IsConnected)
                return;
            if (Time.unscaledTime < _nextBroadcastAt)
                return;
            if (!SessionSync.HasTrackedSave)
                return;

            _broadcastQueued = false;
            _nextBroadcastAt = Time.unscaledTime + MinBroadcastInterval;

            int slot = Mathf.Clamp(SessionSync.TrackedSaveSlot, 0, 2);
            byte[] payload;
            try
            {
                var data = PlayerData.GetDataForSlot(slot);
                if (data == null)
                    return;

                // Same as PlayerData.Save: capture NPC dialogue progress first.
                if (slot == PlayerData.CurrentSaveFileIndex)
                {
                    try { data.dialoguerState = Dialoguer.GetGlobalVariablesState(); }
                    catch { }
                }

                payload = Compress(Encoding.UTF8.GetBytes(JsonUtility.ToJson(data)));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[SaveMirror] Could not serialise the host save: " + ex.Message);
                return;
            }

            int chunkSize = SaveMirrorChunkPacket.MaxChunkBytes;
            int chunkCount = Math.Max(1, (payload.Length + chunkSize - 1) / chunkSize);
            if (chunkCount > MaxChunks)
            {
                Plugin.Log.LogWarning("[SaveMirror] Save is too large to mirror (" + payload.Length + " bytes).");
                return;
            }

            ushort transferId = _nextTransferId++;
            if (_nextTransferId == 0)
                _nextTransferId = 1;

            for (int i = 0; i < chunkCount; i++)
            {
                int offset = i * chunkSize;
                int length = Math.Min(chunkSize, payload.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(payload, offset, chunk, 0, length);

                var pkt = new SaveMirrorChunkPacket
                {
                    TransferId = transferId,
                    Slot = (byte)slot,
                    ChunkIndex = (ushort)i,
                    ChunkCount = (ushort)chunkCount,
                    TotalBytes = payload.Length,
                    Data = chunk,
                };
                Plugin.Net.SendSaveMirrorChunk(ref pkt);
            }

            MirrorsSent++;
            Plugin.LogVerbose("[SaveMirror] Sent slot " + slot + " (" + payload.Length + " bytes, " + chunkCount
                + " chunk(s)). Reason: " + _broadcastReason);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Guest
        // ──────────────────────────────────────────────────────────────────────

        public static void OnChunkReceived(SaveMirrorChunkPacket pkt)
        {
            if (!GuestSession)
                return;
            if (pkt.Slot > 2 || pkt.ChunkCount == 0 || pkt.ChunkCount > MaxChunks
             || pkt.ChunkIndex >= pkt.ChunkCount || pkt.TotalBytes <= 0 || pkt.TotalBytes > MaxTransferBytes)
            {
                Plugin.Log.LogWarning("[SaveMirror] Ignored malformed save chunk.");
                return;
            }

            if (_incomingChunks == null || pkt.TransferId != _incomingTransferId)
            {
                _incomingTransferId = pkt.TransferId;
                _incomingSlot = pkt.Slot;
                _incomingTotal = pkt.TotalBytes;
                _incomingChunks = new byte[pkt.ChunkCount][];
                _incomingReceived = 0;
            }

            if (pkt.ChunkCount != _incomingChunks.Length || _incomingChunks[pkt.ChunkIndex] != null)
                return;

            _incomingChunks[pkt.ChunkIndex] = pkt.Data ?? new byte[0];
            _incomingReceived++;
            if (_incomingReceived < _incomingChunks.Length)
                return;

            var assembled = new byte[_incomingTotal];
            int offset = 0;
            for (int i = 0; i < _incomingChunks.Length; i++)
            {
                var part = _incomingChunks[i];
                if (offset + part.Length > assembled.Length)
                {
                    Plugin.Log.LogWarning("[SaveMirror] Save transfer size mismatch; waiting for the next copy.");
                    _incomingChunks = null;
                    return;
                }
                Buffer.BlockCopy(part, 0, assembled, offset, part.Length);
                offset += part.Length;
            }

            int slot = _incomingSlot;
            _incomingChunks = null;

            PlayerData mirror;
            string dialoguer;
            try
            {
                string json = Encoding.UTF8.GetString(Decompress(assembled));
                mirror = JsonUtility.FromJson<PlayerData>(json);
                if (mirror == null)
                    throw new IOException("empty save");
                if (mirror.coinManager != null && !mirror.coinManager.hasMigratedCoins)
                    mirror = PlayerData.Migrate(mirror);
                dialoguer = mirror.dialoguerState;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[SaveMirror] Could not read the host save: " + ex.Message);
                return;
            }

            _pendingMirror = mirror;
            _pendingSlot = slot;
            if (!string.IsNullOrEmpty(dialoguer))
                _pendingDialoguer = dialoguer;

            TryApplyPending(force: false);
        }

        /// <summary>Guest: a scene load is starting — the safest moment to swap.</summary>
        public static void OnSceneLoadStarted()
        {
            if (!GuestSession)
                return;

            if (_pendingMirror != null)
                TryApplyPending(force: true);
            if (_pendingDialoguer != null)
                ApplyPendingDialoguer();
        }

        static void TryApplyPending(bool force)
        {
            if (_pendingMirror == null || !GuestSession)
                return;

            // Never before this player's own saves are loaded: the originals
            // snapshotted below would be empty slots, and restoring those after
            // the session could later be saved over the real progress.
            if (!PlayerData.Initialized)
                return;

            // Swapping the data under an open shop or equip menu would pull the
            // inventory those screens are showing; wait until they close.
            if (!force && !IsSafeToSwap())
                return;

            var files = GetSaveFiles();
            if (files == null || _pendingSlot < 0 || _pendingSlot >= files.Length)
                return;

            if (!_hasOriginals)
                SnapshotOriginals();

            var previous = files[_pendingSlot];
            KeepLocalLoadout(previous, _pendingMirror);

            files[_pendingSlot] = _pendingMirror;
            PlayerData.CurrentSaveFileIndex = _pendingSlot;

            bool first = MirroredSlot != _pendingSlot;
            MirroredSlot = _pendingSlot;
            MirrorsApplied++;
            _pendingMirror = null;

            if (first)
                Plugin.Log.LogInfo("[SaveMirror] Playing on the host's save (slot " + (MirroredSlot + 1) + "). Your own save is untouched.");

            if (_pendingDialoguer != null && Map.Current == null)
                ApplyPendingDialoguer();
        }

        static bool IsSafeToSwap()
        {
            try
            {
                if (ShopScene.Current != null)
                    return false;
                var equip = AbstractEquipUI.Current;
                if (equip != null && equip.CurrentState == AbstractEquipUI.ActiveState.Active)
                    return false;
            }
            catch
            {
            }

            return true;
        }

        /// <summary>
        /// The guest is the authority on its own loadout (LoadoutReplicator).
        /// A mirror taken before the host heard about a fresh equip change
        /// must not revert it.
        /// </summary>
        static void KeepLocalLoadout(PlayerData from, PlayerData to)
        {
            if (from == null || to == null || from.Loadouts == null || to.Loadouts == null)
                return;

            try
            {
                var local = MultiplayerSession.LocalId;
                var source = from.Loadouts.GetPlayerLoadout(local);
                var target = to.Loadouts.GetPlayerLoadout(local);
                if (source == null || target == null)
                    return;

                target.primaryWeapon = source.primaryWeapon;
                target.secondaryWeapon = source.secondaryWeapon;
                target.super = source.super;
                target.charm = source.charm;
            }
            catch
            {
            }
        }

        static void ApplyPendingDialoguer()
        {
            string state = _pendingDialoguer;
            _pendingDialoguer = null;
            if (string.IsNullOrEmpty(state))
                return;

            try { Dialoguer.SetGlobalVariablesState(state); }
            catch (Exception ex) { Plugin.LogVerbose("[SaveMirror] Dialogue state not applied: " + ex.Message); }
        }

        static void SnapshotOriginals()
        {
            var files = GetSaveFiles();
            if (files == null)
                return;

            _originalSlots = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                try { _originalSlots[i] = files[i] != null ? JsonUtility.ToJson(files[i]) : null; }
                catch { _originalSlots[i] = null; }
            }

            try { _originalDialoguer = Dialoguer.GetGlobalVariablesState(); }
            catch { _originalDialoguer = null; }

            _originalSaveIndex = PlayerData.CurrentSaveFileIndex;
            _hasOriginals = true;
        }

        static void RestoreOriginals()
        {
            var files = GetSaveFiles();
            if (files != null && _originalSlots != null)
            {
                for (int i = 0; i < files.Length && i < _originalSlots.Length; i++)
                {
                    if (_originalSlots[i] == null)
                        continue;

                    try
                    {
                        var restored = JsonUtility.FromJson<PlayerData>(_originalSlots[i]);
                        if (restored != null)
                            files[i] = restored;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("[SaveMirror] Could not restore save slot " + (i + 1) + ": " + ex.Message);
                    }
                }
            }

            PlayerData.CurrentSaveFileIndex = _originalSaveIndex;
            if (!string.IsNullOrEmpty(_originalDialoguer))
            {
                try { Dialoguer.SetGlobalVariablesState(_originalDialoguer); }
                catch { }
            }

            Plugin.Log.LogInfo("[SaveMirror] Session over - your own save data is back.");
        }

        static void OnSessionStarted()
        {
            ResetTransfers();
            if (GuestSession && !_hasOriginals && PlayerData.Initialized)
                SnapshotOriginals();
        }

        static void OnSessionEnded()
        {
            if (_hasOriginals)
                RestoreOriginals();

            _hasOriginals = false;
            _originalSlots = null;
            _originalDialoguer = null;
            ResetTransfers();
        }

        static void ResetTransfers()
        {
            _broadcastQueued = false;
            _incomingChunks = null;
            _pendingMirror = null;
            _pendingSlot = -1;
            _pendingDialoguer = null;
            MirroredSlot = -1;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Self-test
        // ──────────────────────────────────────────────────────────────────────

        static bool _selfTestDone;

        /// <summary>
        /// Once per launch, push the current save through the whole mirror
        /// pipeline (JsonUtility, gzip, chunk packets, reassembly) without
        /// touching any save data, so a broken runtime shows up in the log
        /// before a session ever depends on it.
        /// </summary>
        static void RunSelfTest()
        {
            _selfTestDone = true;
            try
            {
                var data = PlayerData.GetDataForSlot(PlayerData.CurrentSaveFileIndex);
                if (data == null)
                    return;

                string json = JsonUtility.ToJson(data);
                byte[] payload = Compress(Encoding.UTF8.GetBytes(json));

                int chunkSize = SaveMirrorChunkPacket.MaxChunkBytes;
                int chunkCount = Math.Max(1, (payload.Length + chunkSize - 1) / chunkSize);
                var assembled = new byte[payload.Length];
                for (int i = 0; i < chunkCount; i++)
                {
                    int offset = i * chunkSize;
                    int length = Math.Min(chunkSize, payload.Length - offset);
                    var chunk = new byte[length];
                    Buffer.BlockCopy(payload, offset, chunk, 0, length);

                    var sent = new SaveMirrorChunkPacket
                    {
                        TransferId = 1,
                        Slot = 0,
                        ChunkIndex = (ushort)i,
                        ChunkCount = (ushort)chunkCount,
                        TotalBytes = payload.Length,
                        Data = chunk,
                    };
                    var received = new SaveMirrorChunkPacket();
                    using (var ms = new MemoryStream())
                    {
                        var w = new BinaryWriter(ms);
                        sent.Write(w);
                        w.Flush();
                        ms.Position = 0;
                        received.Read(new BinaryReader(ms));
                    }
                    Buffer.BlockCopy(received.Data, 0, assembled, offset, received.Data.Length);
                }

                string roundTrip = Encoding.UTF8.GetString(Decompress(assembled));
                var copy = JsonUtility.FromJson<PlayerData>(roundTrip);
                bool ok = copy != null && JsonUtility.ToJson(copy) == json;

                if (ok)
                    Plugin.Log.LogInfo("[SaveMirror] Self-test OK (" + json.Length + " chars, " + payload.Length + " bytes gzipped, " + chunkCount + " chunk(s)).");
                else
                    Plugin.Log.LogWarning("[SaveMirror] Self-test FAILED: the save did not survive the mirror pipeline unchanged.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[SaveMirror] Self-test FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static PlayerData[] GetSaveFiles()
        {
            try { return SaveFilesField != null ? SaveFilesField.GetValue(null) as PlayerData[] : null; }
            catch { return null; }
        }

        static byte[] Compress(byte[] raw)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress))
                    gzip.Write(raw, 0, raw.Length);
                return output.ToArray();
            }
        }

        static byte[] Decompress(byte[] compressed)
        {
            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > MaxTransferBytes * 8)
                        throw new IOException("Decompressed save is unreasonably large.");
                }
                return output.ToArray();
            }
        }
    }
}
