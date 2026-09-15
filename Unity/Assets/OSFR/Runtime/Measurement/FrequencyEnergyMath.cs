using System.Collections.Generic;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// CPU reference for the compressed-luminance detector used by the V1 pyramid shader.
    /// Image filtering itself remains linear HDR; compression is detector-only.
    /// </summary>
    public static class FrequencyEnergyMath
    {
        private static readonly Vector3 Rec709Luminance = new Vector3(0.2126f, 0.7152f, 0.0722f);

        public static float CompressedLuminance(Color linearHdrColor, float compressionStrength)
        {
            float k = Mathf.Max(0.0001f, compressionStrength);
            float luminance = Mathf.Max(0.0f, Vector3.Dot(
                new Vector3(linearHdrColor.r, linearHdrColor.g, linearHdrColor.b),
                Rec709Luminance));
            return Mathf.Log(1.0f + k * luminance);
        }

        public static float SquaredBandEnergy(Color fine, Color coarse, float compressionStrength)
        {
            float band = CompressedLuminance(fine, compressionStrength)
                - CompressedLuminance(coarse, compressionStrength);
            return band * band;
        }

        public static float FineFrequencyRatio(
            IReadOnlyList<float> bandEnergies,
            int fineBandCount,
            float epsilon = 0.000001f)
        {
            if (bandEnergies == null || bandEnergies.Count == 0)
            {
                return 0.0f;
            }

            int clampedFineBandCount = Mathf.Clamp(fineBandCount, 1, bandEnergies.Count);
            float fineEnergy = 0.0f;
            float totalEnergy = 0.0f;

            for (int index = 0; index < bandEnergies.Count; index++)
            {
                float energy = Mathf.Max(0.0f, bandEnergies[index]);
                totalEnergy += energy;
                if (index < clampedFineBandCount)
                {
                    fineEnergy += energy;
                }
            }

            return Mathf.Clamp01(fineEnergy / (Mathf.Max(0.00000001f, epsilon) + totalEnergy));
        }

        public static float FootprintRiskWeight(
            float majorWorld,
            float minorWorld,
            float activationStartWorld,
            float activationEndWorld)
        {
            float start = Mathf.Max(0.0f, activationStartWorld);
            float end = Mathf.Max(start + 0.00000001f, activationEndWorld);
            float majorWeight = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(start, end, majorWorld));
            float minorWeight = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(start, end, minorWorld));

            // Treat either principal axis becoming undersampled as risk while allowing the
            // second axis to increase the response for isotropically coarse footprints.
            return Mathf.Clamp01(1.0f - (1.0f - majorWeight) * (1.0f - minorWeight));
        }

        public static float AliasRisk(float fineFrequencyRatio, float footprintWeight, float edgeConfidence)
        {
            return Mathf.Clamp01(fineFrequencyRatio)
                * Mathf.Clamp01(footprintWeight)
                * Mathf.Clamp01(edgeConfidence);
        }
    }
}
