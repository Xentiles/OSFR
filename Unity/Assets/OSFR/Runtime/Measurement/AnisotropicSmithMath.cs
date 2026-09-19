using System;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// CPU reference for the height-correlated anisotropic Smith-GGX visibility
    /// term used by the LEAN-like validation material.
    /// </summary>
    public static class AnisotropicSmithMath
    {
        public static float Evaluate(
            Vector3 normal,
            Vector3 tangent,
            Vector3 viewDirection,
            Vector3 lightDirection,
            float alphaTangent,
            float alphaBitangent)
        {
            if (!IsFinite(normal) || !IsFinite(tangent)
                || !IsFinite(viewDirection) || !IsFinite(lightDirection))
            {
                throw new ArgumentException("Smith visibility directions must be finite.");
            }

            if (!float.IsFinite(alphaTangent) || !float.IsFinite(alphaBitangent)
                || alphaTangent <= 0.0f || alphaBitangent <= 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(alphaTangent), "Smith roughness axes must be finite and positive.");
            }

            Vector3 n = normal.normalized;
            Vector3 t = (tangent - n * Vector3.Dot(n, tangent)).normalized;
            Vector3 b = Vector3.Cross(n, t).normalized;
            Vector3 v = viewDirection.normalized;
            Vector3 l = lightDirection.normalized;
            float nDotV = Mathf.Max(0.0f, Vector3.Dot(n, v));
            float nDotL = Mathf.Max(0.0f, Vector3.Dot(n, l));
            float lambdaV = nDotL * new Vector3(
                alphaTangent * Vector3.Dot(t, v),
                alphaBitangent * Vector3.Dot(b, v),
                nDotV).magnitude;
            float lambdaL = nDotV * new Vector3(
                alphaTangent * Vector3.Dot(t, l),
                alphaBitangent * Vector3.Dot(b, l),
                nDotL).magnitude;
            return 0.5f / Mathf.Max(lambdaV + lambdaL, 0.000001f);
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x)
                && float.IsFinite(value.y)
                && float.IsFinite(value.z);
        }
    }
}
