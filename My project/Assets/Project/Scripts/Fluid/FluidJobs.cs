using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Funtom.Fluid
{
    /// <summary>
    /// Shared helpers for spatial hashing. Cell size == smoothing radius h, so a
    /// particle only needs to look at its own cell + the 8 neighbours (3x3).
    /// </summary>
    [BurstCompile]
    public static class Grid
    {
        public static int2 Cell(float2 p, float invH) => (int2)math.floor(p * invH);

        // Unbounded spatial hash. math.hash gives a well-mixed uint; cast to int for map key.
        public static int Hash(int2 c) => (int)math.hash(c);
    }

    /// <summary>
    /// Step 1: integrate external forces, apply jelly springs for locked particles,
    /// then predict the next position. Runs once per simulation step.
    /// </summary>
    [BurstCompile]
    public struct PredictJob : IJobParallelFor
    {
        public NativeArray<float2> velocities;
        public NativeArray<float2> predicted;
        [ReadOnly] public NativeArray<float2> positions;
        [ReadOnly] public NativeArray<byte> locked;
        [ReadOnly] public NativeArray<float2> anchors;

        public float dt;
        public float2 gravity;
        public float jellyStiffness;
        public float jellyDamping;

        public void Execute(int i)
        {
            float2 v = velocities[i];
            float2 x = positions[i];

            v += gravity * dt;

            if (locked[i] != 0)
            {
                // Critically-ish damped spring toward the anchor -> wobble, not rigid.
                float2 toAnchor = anchors[i] - x;
                v += jellyStiffness * toAnchor * dt;
                v *= jellyDamping;
            }

            velocities[i] = v;
            predicted[i] = x + v * dt;
        }
    }

    /// <summary>Step 2: write (cellHash -> particleIndex) into the spatial hash map.</summary>
    [BurstCompile]
    public struct HashJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> predicted;
        public float invH;
        public NativeParallelMultiHashMap<int, int>.ParallelWriter map;

        public void Execute(int i)
        {
            map.Add(Grid.Hash(Grid.Cell(predicted[i], invH)), i);
        }
    }

    /// <summary>
    /// Step 3a (per iteration): compute density and the per-particle lambda
    /// (constraint scaling factor) from the neighbourhood.
    /// </summary>
    [BurstCompile]
    public struct DensityLambdaJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> predicted;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> map;
        public NativeArray<float> lambdas;

        public FluidKernels.Precomputed k;
        public float invH;
        public float restDensity;
        public float epsilon;

        public void Execute(int i)
        {
            float2 pi = predicted[i];
            int2 baseCell = Grid.Cell(pi, invH);

            float density = 0f;
            float2 gradI = float2.zero;   // gradient wrt particle i (k = i term)
            float sumGrad2 = 0f;          // sum of |grad_k C|^2 for k = j

            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                int hash = Grid.Hash(baseCell + new int2(ox, oy));
                if (!map.TryGetFirstValue(hash, out int j, out var it)) continue;
                do
                {
                    float2 dir = pi - predicted[j];
                    float r2 = math.lengthsq(dir);
                    if (r2 >= k.H2) continue;

                    density += FluidKernels.Poly6(r2, k);

                    float r = math.sqrt(r2);
                    float2 grad = FluidKernels.SpikyGradient(dir, r, k) / restDensity;
                    gradI += grad;
                    sumGrad2 += math.dot(grad, grad);
                }
                while (map.TryGetNextValue(out j, ref it));
            }

            float c = density / restDensity - 1f;
            sumGrad2 += math.dot(gradI, gradI);
            lambdas[i] = -c / (sumGrad2 + epsilon);
        }
    }

    /// <summary>
    /// Step 3b (per iteration): compute the position correction deltaP from the
    /// lambdas, including the tensile-instability term s_corr (anti-clumping).
    /// Read-only over predicted/lambdas; writes to a separate deltaP buffer.
    /// </summary>
    [BurstCompile]
    public struct DeltaPositionJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> predicted;
        [ReadOnly] public NativeArray<float> lambdas;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> map;
        public NativeArray<float2> deltaP;

        public FluidKernels.Precomputed k;
        public float invH;
        public float restDensity;
        public float sCorrK;
        public float sCorrN;
        public float wDeltaQ; // Poly6 evaluated at the s_corr reference distance

        public void Execute(int i)
        {
            float2 pi = predicted[i];
            float li = lambdas[i];
            int2 baseCell = Grid.Cell(pi, invH);

            float2 dp = float2.zero;

            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                int hash = Grid.Hash(baseCell + new int2(ox, oy));
                if (!map.TryGetFirstValue(hash, out int j, out var it)) continue;
                do
                {
                    if (j == i) continue;
                    float2 dir = pi - predicted[j];
                    float r2 = math.lengthsq(dir);
                    if (r2 >= k.H2) continue;

                    float r = math.sqrt(r2);
                    float w = FluidKernels.Poly6(r2, k);
                    float scorr = -sCorrK * math.pow(w / wDeltaQ, sCorrN);
                    float2 grad = FluidKernels.SpikyGradient(dir, r, k);
                    dp += (li + lambdas[j] + scorr) * grad;
                }
                while (map.TryGetNextValue(out j, ref it));
            }

            deltaP[i] = dp / restDensity;
        }
    }

    /// <summary>
    /// Step 3c (per iteration): apply deltaP and resolve collisions against the
    /// world bounds and wall segments.
    /// </summary>
    [BurstCompile]
    public struct ApplyDeltaAndCollideJob : IJobParallelFor
    {
        public NativeArray<float2> predicted;
        [ReadOnly] public NativeArray<float2> deltaP;
        [ReadOnly] public NativeArray<WallSegment> walls;

        public float2 boundsMin;
        public float2 boundsMax;
        public float wallEpsilon;

        public void Execute(int i)
        {
            float2 p = predicted[i] + deltaP[i];

            // Wall segments (tubes / funnels / containers).
            for (int w = 0; w < walls.Length; w++)
            {
                WallSegment s = walls[w];
                float2 ab = s.b - s.a;
                float len2 = math.max(math.lengthsq(ab), 1e-6f);
                float t = math.saturate(math.dot(p - s.a, ab) / len2);
                float2 closest = s.a + t * ab;
                float2 d = p - closest;
                float dist = math.length(d);
                float minDist = s.radius + wallEpsilon;
                if (dist < minDist)
                {
                    float2 n = dist > 1e-5f ? d / dist : new float2(0f, 1f);
                    p = closest + n * minDist;
                }
            }

            // Axis-aligned play area.
            p = math.clamp(p, boundsMin, boundsMax);

            predicted[i] = p;
        }
    }

    /// <summary>Step 4: derive velocity from the position change (PBF).</summary>
    [BurstCompile]
    public struct VelocityJob : IJobParallelFor
    {
        public NativeArray<float2> velocities;
        [ReadOnly] public NativeArray<float2> predicted;
        [ReadOnly] public NativeArray<float2> positions;
        public float invDt;

        public void Execute(int i)
        {
            velocities[i] = (predicted[i] - positions[i]) * invDt;
        }
    }

    /// <summary>
    /// Step 5: XSPH viscosity — nudges each particle toward the average velocity of
    /// its neighbours. This is what gives the "gel / cohesive" jelly motion.
    /// Reads velocities read-only, writes into a separate deltaV buffer.
    /// </summary>
    [BurstCompile]
    public struct ViscosityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> predicted;
        [ReadOnly] public NativeArray<float2> velocities;
        [ReadOnly] public NativeParallelMultiHashMap<int, int> map;
        public NativeArray<float2> deltaV;

        public FluidKernels.Precomputed k;
        public float invH;
        public float viscosity;

        public void Execute(int i)
        {
            float2 pi = predicted[i];
            float2 vi = velocities[i];
            int2 baseCell = Grid.Cell(pi, invH);

            float2 accum = float2.zero;
            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                int hash = Grid.Hash(baseCell + new int2(ox, oy));
                if (!map.TryGetFirstValue(hash, out int j, out var it)) continue;
                do
                {
                    if (j == i) continue;
                    float r2 = math.lengthsq(pi - predicted[j]);
                    if (r2 >= k.H2) continue;
                    accum += (velocities[j] - vi) * FluidKernels.Poly6(r2, k);
                }
                while (map.TryGetNextValue(out j, ref it));
            }

            deltaV[i] = viscosity * accum;
        }
    }

    /// <summary>
    /// Step 6: commit. Apply viscosity, damping and speed clamp, then advance the
    /// authoritative positions.
    /// </summary>
    [BurstCompile]
    public struct IntegrateJob : IJobParallelFor
    {
        public NativeArray<float2> velocities;
        public NativeArray<float2> positions;
        [ReadOnly] public NativeArray<float2> predicted;
        [ReadOnly] public NativeArray<float2> deltaV;

        public float damping;
        public float maxSpeed;

        public void Execute(int i)
        {
            float2 v = (velocities[i] + deltaV[i]) * damping;

            float speed = math.length(v);
            if (speed > maxSpeed) v *= maxSpeed / speed;

            velocities[i] = v;
            positions[i] = predicted[i];
        }
    }
}
