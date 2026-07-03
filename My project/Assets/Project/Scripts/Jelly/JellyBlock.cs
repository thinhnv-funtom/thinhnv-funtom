using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Funtom.Fluid
{
    public enum JellyShapeType { Rectangle, Circle }

    /// <summary>
    /// A frozen cluster of fluid particles that holds a shape via anchor springs
    /// (so it jiggles when disturbed but does not fall apart), then dissolves into
    /// free-flowing fluid when tapped.
    ///
    /// The block itself has no renderer — its particles are drawn by the metaball
    /// <see cref="FluidRenderer"/>. On release we simply unlock the particles and
    /// destroy this authoring object.
    /// </summary>
    [DefaultExecutionOrder(-60)] // update anchors before FluidSimulation.LateUpdate
    public class JellyBlock : MonoBehaviour
    {
        public static readonly List<JellyBlock> Active = new List<JellyBlock>();

        [Header("Identity")]
        [Range(0, 3)] public int colorIndex = 0;

        [Header("Shape")]
        public JellyShapeType shape = JellyShapeType.Rectangle;
        public Vector2 size = new Vector2(1.6f, 1.6f);
        [Tooltip("Distance between particles when packing the shape. ~0.4-0.5 * smoothingRadius.")]
        public float spacing = 0.28f;
        [Tooltip("Rounded-corner radius for Rectangle shape.")]
        public float cornerRadius = 0.35f;

        [Header("Idle jiggle (before tap)")]
        public float wobbleAmplitude = 0.05f;
        public float wobbleFrequency = 2.2f;

        [Header("Release")]
        [Tooltip("Outward pop impulse applied to each particle when tapped.")]
        public float releaseImpulse = 1.5f;

        FluidSimulation _sim;
        readonly List<int> _indices = new List<int>();
        readonly List<float2> _localAnchors = new List<float2>();
        bool _released;
        float _phase;

        /// <summary>Spawn the block's particles into the sim. Call once after placement.</summary>
        public void Initialize(FluidSimulation sim)
        {
            _sim = sim;
            _indices.Clear();
            _localAnchors.Clear();

            var pts = JellyShapeSampler.Sample(shape, size, spacing, cornerRadius);
            foreach (var local in pts)
            {
                float2 world = (float2)(Vector2)transform.TransformPoint((Vector2)local);
                int idx = sim.AddParticle(world, colorIndex, locked: true, anchor: world);
                if (idx < 0) break; // hit capacity
                _indices.Add(idx);
                _localAnchors.Add(local);
            }

            // Deterministic-ish phase offset from position (no Random needed at edit time).
            _phase = (transform.position.x * 1.3f + transform.position.y * 0.7f);

            if (!Active.Contains(this)) Active.Add(this);
        }

        void Update()
        {
            if (_released || _sim == null) return;

            // Whole-body idle wobble keeps the jelly feeling alive before the tap.
            float t = Time.time * wobbleFrequency + _phase;
            Vector2 wob = new Vector2(math.sin(t) , math.cos(t * 1.31f)) * wobbleAmplitude;

            for (int i = 0; i < _indices.Count; i++)
            {
                Vector3 world = transform.TransformPoint((Vector2)_localAnchors[i]) + (Vector3)wob;
                _sim.SetAnchor(_indices[i], (float2)(Vector2)world);
            }
        }

        /// <summary>Melt into free fluid: unlock every particle and pop it outward.</summary>
        public void Release()
        {
            if (_released || _sim == null) return;
            _released = true;
            Active.Remove(this);

            float2 center = (float2)(Vector2)transform.position;
            for (int i = 0; i < _indices.Count; i++)
            {
                int idx = _indices[i];
                _sim.SetLocked(idx, false);
                float2 dir = _sim.GetPosition(idx) - center;
                float len = math.length(dir);
                float2 n = len > 1e-4f ? dir / len : new float2(0f, 1f);
                _sim.AddImpulse(idx, n * releaseImpulse);
            }
            Destroy(gameObject);
        }

        /// <summary>World-space hit test for tap input (rough AABB in local space).</summary>
        public bool ContainsPoint(Vector2 world)
        {
            Vector2 local = transform.InverseTransformPoint(world);
            Vector2 half = size * 0.5f + Vector2.one * 0.15f;
            return math.abs(local.x) <= half.x && math.abs(local.y) <= half.y;
        }

        void OnDisable()
        {
            Active.Remove(this);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = JellyPalette.Gizmo(colorIndex);
            Gizmos.matrix = transform.localToWorldMatrix;
            if (shape == JellyShapeType.Circle)
                Gizmos.DrawWireSphere(Vector3.zero, size.x * 0.5f);
            else
                Gizmos.DrawWireCube(Vector3.zero, new Vector3(size.x, size.y, 0.01f));
        }
    }

    /// <summary>Generates a filled set of local particle positions for a jelly shape.</summary>
    public static class JellyShapeSampler
    {
        public static List<float2> Sample(JellyShapeType shape, Vector2 size, float spacing, float cornerRadius)
        {
            var result = new List<float2>();
            spacing = Mathf.Max(0.05f, spacing);
            float halfX = size.x * 0.5f;
            float halfY = size.y * 0.5f;

            int nx = Mathf.Max(1, Mathf.RoundToInt(size.x / spacing));
            int ny = Mathf.Max(1, Mathf.RoundToInt(size.y / spacing));

            for (int iy = 0; iy <= ny; iy++)
            for (int ix = 0; ix <= nx; ix++)
            {
                // Hex-ish offset rows pack tighter and look more organic.
                float ox = (iy & 1) == 1 ? spacing * 0.5f : 0f;
                float x = -halfX + ix * spacing + ox;
                float y = -halfY + iy * spacing;
                if (x > halfX) continue;

                var p = new float2(x, y);
                if (Inside(shape, p, halfX, halfY, cornerRadius))
                    result.Add(p);
            }
            return result;
        }

        static bool Inside(JellyShapeType shape, float2 p, float hx, float hy, float corner)
        {
            switch (shape)
            {
                case JellyShapeType.Circle:
                {
                    float2 n = new float2(p.x / math.max(hx, 1e-4f), p.y / math.max(hy, 1e-4f));
                    return math.lengthsq(n) <= 1f;
                }
                default: // Rounded rectangle (signed-distance style).
                {
                    corner = math.min(corner, math.min(hx, hy));
                    float2 d = math.abs(p) - new float2(hx - corner, hy - corner);
                    float outside = math.length(math.max(d, 0f));
                    float inside = math.min(math.max(d.x, d.y), 0f);
                    return outside + inside <= corner;
                }
            }
        }
    }

    /// <summary>Shared color palette for jelly / fluid (index 0..3 = RGBA channels).</summary>
    public static class JellyPalette
    {
        public static readonly Color[] Colors =
        {
            new Color(0.20f, 0.55f, 1.00f), // 0 blue
            new Color(1.00f, 0.35f, 0.45f), // 1 red/pink
            new Color(0.35f, 0.85f, 0.45f), // 2 green
            new Color(1.00f, 0.80f, 0.25f), // 3 yellow
        };

        public static Color Gizmo(int i) => Colors[Mathf.Clamp(i, 0, 3)];
    }
}
