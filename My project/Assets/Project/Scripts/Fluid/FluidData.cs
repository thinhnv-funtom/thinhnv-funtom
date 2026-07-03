using System;
using Unity.Mathematics;
using UnityEngine;

namespace Funtom.Fluid
{
    /// <summary>Maximum number of distinct fluid colors (packed into RGBA of one RT).</summary>
    public static class FluidConst
    {
        public const int MaxColors = 4;
    }

    /// <summary>
    /// All tunable parameters of the PBF solver. Exposed on the FluidSimulation
    /// component so the "feel" of the fluid can be dialed in from the Inspector.
    /// </summary>
    [Serializable]
    public struct FluidSettings
    {
        [Header("Solver")]
        [Tooltip("Smoothing radius. Neighborhood = this. Also the spatial-hash cell size.")]
        public float smoothingRadius;      // h
        [Tooltip("Target rest density. Higher = less compressible = 'thicker' jelly.")]
        public float restDensity;          // rho0
        [Tooltip("Constraint solver iterations per step. 2-4 is usually enough.")]
        public int solverIterations;
        [Tooltip("Relaxation epsilon in the lambda denominator (CFM). Stabilizes.")]
        public float relaxationEpsilon;

        [Header("Forces")]
        public float2 gravity;
        [Tooltip("Velocity damping applied every step (0..1 kept per step).")]
        public float velocityDamping;
        [Tooltip("Max speed clamp (units/sec) to keep the sim stable.")]
        public float maxSpeed;

        [Header("Tensile instability (anti-clumping)")]
        [Tooltip("s_corr strength k. ~0.0001 - 0.001.")]
        public float sCorrK;
        [Tooltip("s_corr reference distance as a fraction of h (dq). ~0.1 - 0.3.")]
        public float sCorrDeltaQ;
        [Tooltip("s_corr exponent n. Usually 4.")]
        public float sCorrN;

        [Header("Viscosity (XSPH) — jelly cohesion")]
        [Tooltip("XSPH viscosity coefficient. Higher = more 'gel' cohesive motion.")]
        public float viscosity;

        [Header("Jelly springs (locked particles)")]
        [Tooltip("Spring stiffness pulling a locked particle to its anchor.")]
        public float jellyStiffness;
        [Tooltip("Spring damping for locked particles (0..1 kept per step).")]
        public float jellyDamping;

        [Header("World bounds (axis-aligned play area)")]
        public float2 boundsMin;
        public float2 boundsMax;
        [Tooltip("Restitution when hitting a wall (0 = no bounce, 1 = full).")]
        public float boundsRestitution;
        [Tooltip("Tangential friction at walls (0..1 kept).")]
        public float boundsFriction;

        public static FluidSettings Default()
        {
            return new FluidSettings
            {
                smoothingRadius = 0.6f,
                // Rest density ~= 1 / spacing^2 for the packed jelly (spacing ~0.26).
                // This is the #1 "feel" knob: higher = thicker/less compressible.
                restDensity = 15f,
                solverIterations = 3,
                relaxationEpsilon = 100f,

                gravity = new float2(0f, -9.81f),
                velocityDamping = 0.995f,
                maxSpeed = 20f,

                sCorrK = 0.0004f,
                sCorrDeltaQ = 0.2f,
                sCorrN = 4f,

                viscosity = 0.08f,

                jellyStiffness = 260f,
                jellyDamping = 0.85f,

                boundsMin = new float2(-5f, -6f),
                boundsMax = new float2(5f, 6f),
                boundsRestitution = 0.05f,
                boundsFriction = 0.6f,
            };
        }
    }

    /// <summary>A straight wall segment particles collide against (tubes, funnels, containers).</summary>
    public struct WallSegment
    {
        public float2 a;
        public float2 b;
        public float radius; // collision thickness

        public WallSegment(float2 a, float2 b, float radius = 0.05f)
        {
            this.a = a;
            this.b = b;
            this.radius = radius;
        }
    }
}
