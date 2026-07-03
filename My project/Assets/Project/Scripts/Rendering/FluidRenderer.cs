using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Funtom.Fluid
{
    /// <summary>
    /// Metaball renderer. Each particle is drawn as a soft radial blob into a
    /// half-resolution HDR field RenderTexture (additive, per-color channel), then
    /// a full-screen threshold pass turns that field into a thick liquid surface
    /// with a bright rim. See FluidParticle.shader / FluidMetaball.shader.
    ///
    /// No second camera and no dedicated layer: the particle mesh is rasterized
    /// straight into the RT with a CommandBuffer using the gameplay camera's
    /// view/projection, so it lines up 1:1 with the world.
    /// </summary>
    [DefaultExecutionOrder(100)] // after FluidSimulation.LateUpdate
    public class FluidRenderer : MonoBehaviour
    {
        [Header("References")]
        public FluidSimulation sim;
        public Camera targetCamera;

        [Header("Field RT")]
        [Tooltip("1 = full res, 2 = half res (recommended on mobile), 4 = quarter.")]
        [Range(1, 4)] public int downsample = 2;

        [Header("Blob")]
        [Tooltip("Visual blob radius as a multiple of the smoothing radius.")]
        public float blobScale = 1.15f;
        [Range(0.25f, 6f)] public float falloffSoftness = 2.0f;
        [Range(0.1f, 4f)] public float fieldStrength = 1.0f;

        [Header("Surface thresholds")]
        public float bodyThreshold = 0.85f;   // _T1
        public float edgeThreshold = 0.35f;   // _T2
        [Range(0.001f, 0.6f)] public float edgeAntialias = 0.08f;
        [Range(0f, 1f)] public float rimBrightness = 0.55f;

        RenderTexture _rt;
        Material _particleMat;
        Material _compositeMat;
        Mesh _mesh;
        CommandBuffer _cb;
        RawImage _display;

        // Reusable mesh buffers (avoid per-frame GC).
        Vector3[] _verts;
        Vector2[] _uvs;
        Color32[] _cols;
        int[] _tris;
        int _capacity;

        static readonly Color32[] ChannelMask =
        {
            new Color32(255, 0, 0, 0),
            new Color32(0, 255, 0, 0),
            new Color32(0, 0, 255, 0),
            new Color32(0, 0, 0, 255),
        };

        void Start()
        {
            if (targetCamera == null) targetCamera = Camera.main;
            EnsureResources();
        }

        void EnsureResources()
        {
            if (sim == null) return;

            if (_particleMat == null)
            {
                var s = Shader.Find("Funtom/FluidParticle");
                if (s != null) _particleMat = new Material(s) { hideFlags = HideFlags.DontSave };
            }
            if (_compositeMat == null)
            {
                var s = Shader.Find("Funtom/FluidMetaball");
                if (s != null) _compositeMat = new Material(s) { hideFlags = HideFlags.DontSave };
            }

            EnsureMesh();
            EnsureRT();
            EnsureDisplay();
            if (_cb == null) _cb = new CommandBuffer { name = "Fluid Metaball Field" };
        }

        void EnsureMesh()
        {
            int cap = sim.Capacity;
            if (_mesh != null && _capacity == cap) return;
            _capacity = cap;

            _mesh = new Mesh { name = "FluidParticles", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _mesh.MarkDynamic();

            _verts = new Vector3[cap * 4];
            _uvs   = new Vector2[cap * 4];
            _cols  = new Color32[cap * 4];
            _tris  = new int[cap * 6];

            // Static per-quad UVs and triangles.
            for (int i = 0; i < cap; i++)
            {
                int v = i * 4;
                _uvs[v + 0] = new Vector2(0, 0);
                _uvs[v + 1] = new Vector2(1, 0);
                _uvs[v + 2] = new Vector2(1, 1);
                _uvs[v + 3] = new Vector2(0, 1);

                int t = i * 6;
                _tris[t + 0] = v + 0; _tris[t + 1] = v + 2; _tris[t + 2] = v + 1;
                _tris[t + 3] = v + 0; _tris[t + 4] = v + 3; _tris[t + 5] = v + 2;
            }
        }

        void EnsureRT()
        {
            int w = Mathf.Max(1, Screen.width / downsample);
            int h = Mathf.Max(1, Screen.height / downsample);
            if (_rt != null && _rt.width == w && _rt.height == h) return;

            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf)
            {
                name = "FluidField",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1,
            };
            _rt.Create();
            if (_display != null) _display.texture = _rt;
        }

        void EnsureDisplay()
        {
            if (_display != null) return;

            var canvasGo = new GameObject("FluidCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var imgGo = new GameObject("FluidDisplay");
            imgGo.transform.SetParent(canvasGo.transform, false);
            _display = imgGo.AddComponent<RawImage>();
            _display.raycastTarget = false;
            _display.texture = _rt;
            _display.material = _compositeMat;

            var rt = _display.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        void LateUpdate()
        {
            if (sim == null) return;
            EnsureResources();
            if (_rt == null || _particleMat == null || _compositeMat == null) return;
            EnsureRT();

            PushMaterialParams();
            BuildMesh();
            RenderField();
        }

        void PushMaterialParams()
        {
            _particleMat.SetFloat("_Softness", falloffSoftness);
            _particleMat.SetFloat("_Strength", fieldStrength);

            _compositeMat.SetFloat("_T1", bodyThreshold);
            _compositeMat.SetFloat("_T2", edgeThreshold);
            _compositeMat.SetFloat("_AA", edgeAntialias);
            _compositeMat.SetFloat("_RimBoost", rimBrightness);
            _compositeMat.SetColor("_Color0", JellyPalette.Colors[0]);
            _compositeMat.SetColor("_Color1", JellyPalette.Colors[1]);
            _compositeMat.SetColor("_Color2", JellyPalette.Colors[2]);
            _compositeMat.SetColor("_Color3", JellyPalette.Colors[3]);
        }

        void BuildMesh()
        {
            int count = sim.Count;
            NativeArray<float2> pos = sim.Positions;
            NativeArray<byte> colIdx = sim.Colors;
            float r = sim.settings.smoothingRadius * blobScale * 0.5f;

            for (int i = 0; i < count; i++)
            {
                float2 p = pos[i];
                int v = i * 4;
                _verts[v + 0] = new Vector3(p.x - r, p.y - r, 0f);
                _verts[v + 1] = new Vector3(p.x + r, p.y - r, 0f);
                _verts[v + 2] = new Vector3(p.x + r, p.y + r, 0f);
                _verts[v + 3] = new Vector3(p.x - r, p.y + r, 0f);

                Color32 m = ChannelMask[colIdx[i] & 3];
                _cols[v + 0] = m; _cols[v + 1] = m; _cols[v + 2] = m; _cols[v + 3] = m;
            }

            // Collapse unused quads to a degenerate point so leftover capacity draws nothing.
            for (int i = count; i < _capacity; i++)
            {
                int v = i * 4;
                _verts[v + 0] = _verts[v + 1] = _verts[v + 2] = _verts[v + 3] = Vector3.zero;
            }

            _mesh.SetVertices(_verts);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetColors(_cols);
            _mesh.SetTriangles(_tris, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
        }

        void RenderField()
        {
            _cb.Clear();
            _cb.SetRenderTarget(_rt);
            _cb.ClearRenderTarget(true, true, Color.clear);

            Matrix4x4 view = targetCamera.worldToCameraMatrix;
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(targetCamera.projectionMatrix, true);
            _cb.SetViewProjectionMatrices(view, proj);
            _cb.DrawMesh(_mesh, Matrix4x4.identity, _particleMat, 0, 0);

            Graphics.ExecuteCommandBuffer(_cb);
        }

        void OnDestroy()
        {
            if (_rt != null) _rt.Release();
            if (_cb != null) _cb.Release();
            if (_mesh != null) Destroy(_mesh);
            if (_particleMat != null) Destroy(_particleMat);
            if (_compositeMat != null) Destroy(_compositeMat);
        }
    }
}
