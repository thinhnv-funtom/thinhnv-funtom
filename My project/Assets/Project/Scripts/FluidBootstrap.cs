using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Funtom.Fluid
{
    /// <summary>
    /// One-click playable demo. Drop this on an empty GameObject in the scene and
    /// press Play: it wires up the camera, the FluidSimulation + FluidRenderer,
    /// builds a couple of containers out of wall segments, spawns colored jelly
    /// blocks, and lets you tap a block to melt it into flowing liquid.
    ///
    /// Everything is created at runtime, so no scene setup is required.
    /// </summary>
    public class FluidBootstrap : MonoBehaviour
    {
        [Header("World")]
        public Vector2 boundsMin = new Vector2(-5f, -6f);
        public Vector2 boundsMax = new Vector2(5f, 6f);
        public Color background = new Color(0.06f, 0.07f, 0.10f);

        [Header("Sim")]
        public int maxParticles = 1200;

        [Header("Jelly blocks")]
        public Vector2 blockSize = new Vector2(2.2f, 1.8f);
        public float blockSpacing = 0.26f;

        FluidSimulation _sim;
        FluidRenderer _renderer;
        Camera _cam;

        void Start()
        {
            SetupCamera();
            SetupSim();
            SetupRenderer();
            BuildContainers();
            SpawnBlocks();
        }

        void SetupCamera()
        {
            _cam = Camera.main;
            if (_cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                _cam = go.AddComponent<Camera>();
            }
            _cam.orthographic = true;
            _cam.transform.position = new Vector3(
                (boundsMin.x + boundsMax.x) * 0.5f,
                (boundsMin.y + boundsMax.y) * 0.5f,
                -10f);
            // Fit the play area vertically with a little margin.
            _cam.orthographicSize = (boundsMax.y - boundsMin.y) * 0.5f + 0.5f;
            _cam.backgroundColor = background;
            _cam.clearFlags = CameraClearFlags.SolidColor;
        }

        void SetupSim()
        {
            _sim = gameObject.AddComponent<FluidSimulation>();
            _sim.maxParticles = maxParticles;
            var s = FluidSettings.Default();
            s.boundsMin = (float2)boundsMin;
            s.boundsMax = (float2)boundsMax;
            _sim.settings = s;
        }

        void SetupRenderer()
        {
            _renderer = gameObject.AddComponent<FluidRenderer>();
            _renderer.sim = _sim;
            _renderer.targetCamera = _cam;
        }

        void BuildContainers()
        {
            // Two open-top cups along the bottom to catch the poured jelly.
            float floor = boundsMin.y + 0.3f;
            AddCup(new Vector2(-2.4f, floor), 1.7f, 2.4f);
            AddCup(new Vector2(2.4f, floor), 1.7f, 2.4f);

            // A slanted chute in the middle to make the flow interesting.
            var chute = new List<Vector2> { new Vector2(-0.2f, 1.5f), new Vector2(1.8f, 0.2f) };
            _sim.AddWallStrip(chute, 0.08f, closed: false);
            DrawWalls(chute);
        }

        // Adds a U-shaped cup: left wall, bottom, right wall.
        void AddCup(Vector2 bottomCenter, float width, float height)
        {
            float hw = width * 0.5f;
            var pts = new List<Vector2>
            {
                new Vector2(bottomCenter.x - hw, bottomCenter.y + height),
                new Vector2(bottomCenter.x - hw, bottomCenter.y),
                new Vector2(bottomCenter.x + hw, bottomCenter.y),
                new Vector2(bottomCenter.x + hw, bottomCenter.y + height),
            };
            _sim.AddWallStrip(pts, 0.08f, closed: false);
            DrawWalls(pts);
        }

        // Cheap visual for walls (collision is handled by the sim, not these lines).
        void DrawWalls(List<Vector2> pts)
        {
            var go = new GameObject("WallLine");
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = pts.Count;
            lr.widthMultiplier = 0.12f;
            lr.numCapVertices = 2;
            var shader = Shader.Find("Sprites/Default");
            if (shader != null) lr.material = new Material(shader);
            lr.startColor = lr.endColor = new Color(0.5f, 0.55f, 0.65f, 1f);
            for (int i = 0; i < pts.Count; i++)
                lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0.5f));
        }

        void SpawnBlocks()
        {
            SpawnBlock(new Vector3(-2.4f, 3.8f), 0, JellyShapeType.Rectangle);
            SpawnBlock(new Vector3(2.4f, 3.8f), 1, JellyShapeType.Circle);
            SpawnBlock(new Vector3(0f, 5.0f), 2, JellyShapeType.Rectangle);
        }

        void SpawnBlock(Vector3 pos, int color, JellyShapeType shape)
        {
            var go = new GameObject($"JellyBlock_{color}");
            go.transform.position = pos;
            var block = go.AddComponent<JellyBlock>();
            block.colorIndex = color;
            block.shape = shape;
            block.size = blockSize;
            block.spacing = blockSpacing;
            block.Initialize(_sim);
        }

        void Update()
        {
            if (!TryGetTap(out Vector2 screenPos)) return;

            Vector3 world = _cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, -_cam.transform.position.z));
            Vector2 world2 = world;

            // Topmost block under the tap wins.
            for (int i = JellyBlock.Active.Count - 1; i >= 0; i--)
            {
                var b = JellyBlock.Active[i];
                if (b != null && b.ContainsPoint(world2))
                {
                    b.Release();
                    break;
                }
            }
        }

        static bool TryGetTap(out Vector2 screenPos)
        {
            screenPos = default;
            if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
            {
                screenPos = Touchscreen.current.primaryTouch.position.ReadValue();
                return true;
            }
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                screenPos = Mouse.current.position.ReadValue();
                return true;
            }
            return false;
        }
    }
}
