using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Funtom.Fluid
{
    /// <summary>
    /// Owns the particle state (Structure-of-Arrays in NativeArrays) and runs the
    /// Position Based Fluids solver as a chain of Burst jobs on a fixed timestep.
    ///
    /// Particles are never removed (indices stay stable for the lifetime of the
    /// sim); a "jelly" particle is just a normal fluid particle that is additionally
    /// pulled toward an anchor while <see cref="locked"/> is set. Releasing a jelly
    /// block simply clears that flag.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class FluidSimulation : MonoBehaviour
    {
        [Header("Capacity")]
        [Tooltip("Hard cap on active particles. Arrays are allocated once at this size.")]
        public int maxParticles = 1200;

        [Header("Timestep")]
        [Tooltip("Fixed simulation frequency (Hz). 50 is a good stability/cost balance.")]
        public float simHz = 50f;
        [Tooltip("Max substeps per frame to avoid a spiral of death on hitches.")]
        public int maxSubStepsPerFrame = 3;

        public FluidSettings settings = FluidSettings.Default();

        // --- SoA particle state (Persistent for the object's lifetime) ---
        NativeArray<float2> _positions;
        NativeArray<float2> _velocities;
        NativeArray<float2> _predicted;
        NativeArray<float2> _deltaP;
        NativeArray<float2> _deltaV;
        NativeArray<float>  _lambdas;
        NativeArray<byte>   _locked;
        NativeArray<float2> _anchors;
        NativeArray<byte>   _colorIndex;

        NativeParallelMultiHashMap<int, int> _grid;

        NativeArray<WallSegment> _walls;
        readonly List<WallSegment> _wallList = new List<WallSegment>();
        bool _wallsDirty = true;

        int _count;
        float _accumulator;
        bool _allocated;

        public int Count => _count;
        public int Capacity => maxParticles;

        // Read-only views for the renderer. Only valid after Awake and between steps.
        public NativeArray<float2> Positions => _positions;
        public NativeArray<byte> Colors => _colorIndex;

        void OnDestroy()
        {
            Dispose();
        }

        // Allocated lazily on first use so callers (e.g. FluidBootstrap) can set
        // maxParticles after AddComponent but before the first AddParticle.
        void EnsureAllocated()
        {
            if (_allocated) return;
            var p = Allocator.Persistent;
            _positions  = new NativeArray<float2>(maxParticles, p);
            _velocities = new NativeArray<float2>(maxParticles, p);
            _predicted  = new NativeArray<float2>(maxParticles, p);
            _deltaP     = new NativeArray<float2>(maxParticles, p);
            _deltaV     = new NativeArray<float2>(maxParticles, p);
            _lambdas    = new NativeArray<float>(maxParticles, p);
            _locked     = new NativeArray<byte>(maxParticles, p);
            _anchors    = new NativeArray<float2>(maxParticles, p);
            _colorIndex = new NativeArray<byte>(maxParticles, p);
            _grid       = new NativeParallelMultiHashMap<int, int>(maxParticles, p);
            _walls      = new NativeArray<WallSegment>(0, p);
            _allocated  = true;
        }

        void Dispose()
        {
            if (!_allocated) return;
            if (_positions.IsCreated)  _positions.Dispose();
            if (_velocities.IsCreated) _velocities.Dispose();
            if (_predicted.IsCreated)  _predicted.Dispose();
            if (_deltaP.IsCreated)     _deltaP.Dispose();
            if (_deltaV.IsCreated)     _deltaV.Dispose();
            if (_lambdas.IsCreated)    _lambdas.Dispose();
            if (_locked.IsCreated)     _locked.Dispose();
            if (_anchors.IsCreated)    _anchors.Dispose();
            if (_colorIndex.IsCreated) _colorIndex.Dispose();
            if (_grid.IsCreated)       _grid.Dispose();
            if (_walls.IsCreated)      _walls.Dispose();
            _allocated = false;
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>Spawn a particle. Returns its stable index, or -1 if at capacity.</summary>
        public int AddParticle(float2 position, int colorIndex, bool locked = false, float2 anchor = default)
        {
            EnsureAllocated();
            if (_count >= maxParticles) return -1;
            int i = _count++;
            _positions[i]  = position;
            _velocities[i] = float2.zero;
            _predicted[i]  = position;
            _locked[i]     = (byte)(locked ? 1 : 0);
            _anchors[i]    = locked ? anchor : position;
            _colorIndex[i] = (byte)math.clamp(colorIndex, 0, FluidConst.MaxColors - 1);
            return i;
        }

        public void SetAnchor(int index, float2 anchor)
        {
            if ((uint)index < (uint)_count) _anchors[index] = anchor;
        }

        public void SetLocked(int index, bool locked)
        {
            if ((uint)index < (uint)_count) _locked[index] = (byte)(locked ? 1 : 0);
        }

        public float2 GetPosition(int index)
            => (uint)index < (uint)_count ? _positions[index] : float2.zero;

        /// <summary>Give a locked particle a velocity impulse (e.g. a "pop" when released).</summary>
        public void AddImpulse(int index, float2 impulse)
        {
            if ((uint)index < (uint)_count) _velocities[index] += impulse;
        }

        // --- Wall authoring ---
        public void ClearWalls() { _wallList.Clear(); _wallsDirty = true; }
        public void AddWall(WallSegment s) { _wallList.Add(s); _wallsDirty = true; }
        public void AddWallStrip(IReadOnlyList<Vector2> points, float radius = 0.05f, bool closed = false)
        {
            int n = points.Count;
            for (int i = 0; i < n - 1; i++)
                _wallList.Add(new WallSegment(points[i], points[i + 1], radius));
            if (closed && n > 2)
                _wallList.Add(new WallSegment(points[n - 1], points[0], radius));
            _wallsDirty = true;
        }

        void RebuildWalls()
        {
            if (!_wallsDirty) return;
            if (_walls.IsCreated) _walls.Dispose();
            _walls = new NativeArray<WallSegment>(_wallList.Count, Allocator.Persistent);
            for (int i = 0; i < _wallList.Count; i++) _walls[i] = _wallList[i];
            _wallsDirty = false;
        }

        // ------------------------------------------------------------------
        // Simulation loop (LateUpdate so jelly blocks can push anchors in Update)
        // ------------------------------------------------------------------
        void LateUpdate()
        {
            if (_count == 0) return;
            RebuildWalls();

            float fixedDt = 1f / math.max(1f, simHz);
            _accumulator += Time.deltaTime;

            int steps = 0;
            while (_accumulator >= fixedDt && steps < maxSubStepsPerFrame)
            {
                Step(fixedDt);
                _accumulator -= fixedDt;
                steps++;
            }
            // Drop leftover time if we hit the substep cap (avoid death spiral).
            if (steps == maxSubStepsPerFrame) _accumulator = 0f;
        }

        void Step(float dt)
        {
            var k = FluidKernels.Precomputed.Create(settings.smoothingRadius);
            float invH = 1f / settings.smoothingRadius;
            float dq = settings.sCorrDeltaQ * settings.smoothingRadius;
            float wDeltaQ = FluidKernels.Poly6(dq * dq, k);
            wDeltaQ = math.max(wDeltaQ, 1e-9f);

            // 1) Predict.
            var handle = new PredictJob
            {
                velocities = _velocities,
                predicted = _predicted,
                positions = _positions,
                locked = _locked,
                anchors = _anchors,
                dt = dt,
                gravity = settings.gravity,
                jellyStiffness = settings.jellyStiffness,
                jellyDamping = settings.jellyDamping,
            }.Schedule(_count, 64);
            handle.Complete();

            // 2) Rebuild spatial hash (main-thread Clear, then parallel fill).
            _grid.Clear();
            handle = new HashJob
            {
                predicted = _predicted,
                invH = invH,
                map = _grid.AsParallelWriter(),
            }.Schedule(_count, 64);

            // 3) Constraint iterations.
            for (int iter = 0; iter < settings.solverIterations; iter++)
            {
                handle = new DensityLambdaJob
                {
                    predicted = _predicted,
                    map = _grid,
                    lambdas = _lambdas,
                    k = k,
                    invH = invH,
                    restDensity = settings.restDensity,
                    epsilon = settings.relaxationEpsilon,
                }.Schedule(_count, 64, handle);

                handle = new DeltaPositionJob
                {
                    predicted = _predicted,
                    lambdas = _lambdas,
                    map = _grid,
                    deltaP = _deltaP,
                    k = k,
                    invH = invH,
                    restDensity = settings.restDensity,
                    sCorrK = settings.sCorrK,
                    sCorrN = settings.sCorrN,
                    wDeltaQ = wDeltaQ,
                }.Schedule(_count, 64, handle);

                handle = new ApplyDeltaAndCollideJob
                {
                    predicted = _predicted,
                    deltaP = _deltaP,
                    walls = _walls,
                    boundsMin = settings.boundsMin,
                    boundsMax = settings.boundsMax,
                    wallEpsilon = settings.smoothingRadius * 0.25f,
                }.Schedule(_count, 64, handle);
            }

            // 4) Velocity from position change.
            handle = new VelocityJob
            {
                velocities = _velocities,
                predicted = _predicted,
                positions = _positions,
                invDt = 1f / dt,
            }.Schedule(_count, 64, handle);

            // 5) XSPH viscosity.
            handle = new ViscosityJob
            {
                predicted = _predicted,
                velocities = _velocities,
                map = _grid,
                deltaV = _deltaV,
                k = k,
                invH = invH,
                viscosity = settings.viscosity,
            }.Schedule(_count, 64, handle);

            // 6) Commit.
            handle = new IntegrateJob
            {
                velocities = _velocities,
                positions = _positions,
                predicted = _predicted,
                deltaV = _deltaV,
                damping = settings.velocityDamping,
                maxSpeed = settings.maxSpeed,
            }.Schedule(_count, 64, handle);

            handle.Complete();
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            var min = (Vector2)settings.boundsMin;
            var max = (Vector2)settings.boundsMax;
            Gizmos.DrawLine(new Vector3(min.x, min.y), new Vector3(max.x, min.y));
            Gizmos.DrawLine(new Vector3(max.x, min.y), new Vector3(max.x, max.y));
            Gizmos.DrawLine(new Vector3(max.x, max.y), new Vector3(min.x, max.y));
            Gizmos.DrawLine(new Vector3(min.x, max.y), new Vector3(min.x, min.y));
        }
    }
}
