using System.Collections.Generic;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Maps stable enemy IDs → DamageReceiver components for the active level's
    /// enemies. Used by EnemySyncManager to look up enemies by the numeric ID sent
    /// in EnemyStatePacket.
    ///
    /// Unity instance IDs are process-local and can never match between two
    /// machines, so the wire ID is a deterministic FNV-1a hash of the enemy's
    /// transform path (names + sibling indices). Both peers load the same scene
    /// and spawn enemies in the same order, so the hashes line up.
    ///
    /// Populated lazily with a short cooldown so a burst of packets for unknown
    /// IDs cannot trigger a FindObjectsOfType scan every frame.
    /// </summary>
    public static class EnemyRegistry
    {
        private static readonly Dictionary<int, DamageReceiver> _map
            = new Dictionary<int, DamageReceiver>(32);
        private static readonly Dictionary<int, int> _stableIdCache
            = new Dictionary<int, int>(64);

        private const float RebuildCooldown = 0.5f;

        private static bool _dirty = true;
        private static float _lastRebuildAt = -10f;

        public static void MarkDirty() => _dirty = true;

        public static void Clear()
        {
            _map.Clear();
            _stableIdCache.Clear();
            _dirty = true;
            _lastRebuildAt = -10f;
        }

        public static bool TryGet(int stableId, out DamageReceiver dr)
        {
            if (_dirty && Time.unscaledTime - _lastRebuildAt >= RebuildCooldown)
                Rebuild();

            if (_map.TryGetValue(stableId, out dr))
            {
                if (dr != null)
                    return true;

                _map.Remove(stableId);
            }

            return false;
        }

        /// <summary>
        /// Deterministic ID for an enemy object, identical on host and client for
        /// objects with the same hierarchy path and sibling order. Cached per
        /// Unity instance because transform walks are not free at 60 Hz.
        /// </summary>
        public static int GetStableId(GameObject go)
        {
            if (go == null)
                return 0;

            int instanceId = go.GetInstanceID();
            int stableId;
            if (_stableIdCache.TryGetValue(instanceId, out stableId))
                return stableId;

            stableId = ComputeStableId(go);
            _stableIdCache[instanceId] = stableId;
            return stableId;
        }

        static int ComputeStableId(GameObject go)
        {
            unchecked
            {
                // FNV-1a over the transform path — explicit so both peers agree
                // regardless of runtime string.GetHashCode behaviour.
                uint hash = 2166136261u;
                var t = go.transform;
                while (t != null)
                {
                    string name = t.name;
                    for (int i = 0; i < name.Length; i++)
                    {
                        hash ^= name[i];
                        hash *= 16777619u;
                    }

                    hash ^= (uint)t.GetSiblingIndex();
                    hash *= 16777619u;
                    t = t.parent;
                }

                int result = (int)hash;
                return result == 0 ? 1 : result;
            }
        }

        static void Rebuild()
        {
            _map.Clear();
            foreach (var dr in Object.FindObjectsOfType<DamageReceiver>())
            {
                if (dr.type != DamageReceiver.Type.Enemy)
                    continue;
                if (EnemySyncManager.IsExcludedName(dr.gameObject.name))
                    continue;

                int stableId = GetStableId(dr.gameObject);
                if (!_map.ContainsKey(stableId))
                    _map[stableId] = dr;
            }
            _dirty = false;
            _lastRebuildAt = Time.unscaledTime;
        }
    }
}
