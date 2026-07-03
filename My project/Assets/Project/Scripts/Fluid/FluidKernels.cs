using Unity.Burst;
using Unity.Mathematics;

namespace Funtom.Fluid
{
    /// <summary>
    /// 2D SPH smoothing kernels used by the Position Based Fluids solver.
    /// All functions are Burst-friendly (static, no managed state) and take the
    /// smoothing radius h together with precomputed normalization factors so the
    /// hot loops never recompute pow().
    ///
    /// References: Macklin &amp; Müller, "Position Based Fluids" (SIGGRAPH 2013).
    /// The 2D normalization constants differ from the classic 3D ones.
    /// </summary>
    [BurstCompile]
    public static class FluidKernels
    {
        /// <summary>
        /// Precomputed constants for a given smoothing radius. Build once per frame
        /// on the main thread and pass by value into the jobs.
        /// </summary>
        public struct Precomputed
        {
            public float H;        // smoothing radius
            public float H2;       // h^2
            public float Poly6;    // 4 / (pi * h^8)
            public float SpikyGrad;// -30 / (pi * h^5)

            public static Precomputed Create(float h)
            {
                float pi = math.PI;
                return new Precomputed
                {
                    H = h,
                    H2 = h * h,
                    Poly6 = 4f / (pi * math.pow(h, 8f)),
                    SpikyGrad = -30f / (pi * math.pow(h, 5f)),
                };
            }
        }

        /// <summary>Poly6 density kernel (2D). r2 is the squared distance.</summary>
        [BurstCompile]
        public static float Poly6(float r2, in Precomputed k)
        {
            if (r2 >= k.H2) return 0f;
            float d = k.H2 - r2;
            return k.Poly6 * d * d * d;
        }

        /// <summary>
        /// Gradient of the Spiky kernel (2D). Returns the full vector ∇W.
        /// dir = (p_i - p_j); r = |dir|. Points away from j.
        /// </summary>
        [BurstCompile]
        public static float2 SpikyGradient(float2 dir, float r, in Precomputed k)
        {
            if (r <= 1e-5f || r >= k.H) return float2.zero;
            float coeff = k.SpikyGrad * (k.H - r) * (k.H - r);
            return coeff * (dir / r);
        }
    }
}
