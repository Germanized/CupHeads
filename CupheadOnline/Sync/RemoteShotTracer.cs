using System.Collections.Generic;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Visual-only tracer streaks for the OTHER player's shots. Real remote
    /// projectiles are intentionally not spawned (they would double-count damage
    /// against the host-synced boss HP), but an unarmed streak from their muzzle
    /// makes remote fire readable instead of invisible.
    /// </summary>
    public static class RemoteShotTracer
    {
        const int POOL_SIZE = 24;
        const float SPEED = 1500f;
        const float LIFETIME = 0.30f;
        const float LENGTH = 42f;
        const float THICKNESS = 4f;

        sealed class Tracer
        {
            public GameObject Go;
            public SpriteRenderer Renderer;
            public Vector3 Velocity;
            public float DieAt;
        }

        static readonly List<Tracer> _pool = new List<Tracer>(POOL_SIZE);
        static Sprite _sprite;
        static int _nextIndex;

        static RemoteShotTracer()
        {
            MultiplayerSession.OnSessionEnded += HideAll;
        }

        public static void Spawn(Vector2 origin, Vector2 direction)
        {
            if (!Plugin.EnableRemoteShotTracers)
                return;

            if (direction.sqrMagnitude < 0.01f)
                direction = Vector2.right;
            direction.Normalize();

            var tracer = GetTracer();
            if (tracer == null || tracer.Go == null)
                return;

            tracer.Go.transform.position = new Vector3(origin.x, origin.y, 0f);
            tracer.Go.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
            tracer.Velocity = direction * SPEED;
            tracer.DieAt = Time.time + LIFETIME;
            tracer.Go.SetActive(true);
        }

        public static void Update()
        {
            if (_pool.Count == 0)
                return;

            float now = Time.time;
            float dt = Time.deltaTime;
            for (int i = 0; i < _pool.Count; i++)
            {
                var tracer = _pool[i];
                if (tracer.Go == null || !tracer.Go.activeSelf)
                    continue;

                if (now >= tracer.DieAt || Level.Current == null)
                {
                    tracer.Go.SetActive(false);
                    continue;
                }

                tracer.Go.transform.position += tracer.Velocity * dt;
                float lifeLeft = Mathf.Clamp01((tracer.DieAt - now) / LIFETIME);
                var colour = tracer.Renderer.color;
                colour.a = 0.55f * lifeLeft;
                tracer.Renderer.color = colour;
            }
        }

        public static void HideAll()
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i].Go != null)
                    _pool[i].Go.SetActive(false);
            }
        }

        static Tracer GetTracer()
        {
            EnsureSprite();

            if (_pool.Count < POOL_SIZE)
            {
                var created = CreateTracer();
                if (created != null)
                    _pool.Add(created);
                return created;
            }

            var tracer = _pool[_nextIndex];
            _nextIndex = (_nextIndex + 1) % _pool.Count;
            if (tracer.Go == null)
            {
                tracer = CreateTracer();
                if (tracer != null)
                    _pool[_nextIndex] = tracer;
            }
            return tracer;
        }

        static Tracer CreateTracer()
        {
            if (_sprite == null)
                return null;

            var go = new GameObject("CupHeads_ShotTracer");
            Object.DontDestroyOnLoad(go);
            go.SetActive(false);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = _sprite;
            renderer.color = new Color(1f, 0.95f, 0.72f, 0.55f);
            renderer.sortingOrder = 900;
            go.transform.localScale = new Vector3(LENGTH, THICKNESS, 1f);

            return new Tracer { Go = go, Renderer = renderer };
        }

        static void EnsureSprite()
        {
            if (_sprite != null)
                return;

            var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply(false, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            _sprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.15f, 0.5f), 1f);
            _sprite.hideFlags = HideFlags.HideAndDontSave;
        }
    }
}
