using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// Principal dimensions of a world-space output-pixel footprint.
    /// </summary>
    public readonly struct PixelFootprint
    {
        public PixelFootprint(float majorWorld, float minorWorld, float areaWorld2, float anisotropy)
        {
            MajorWorld = majorWorld;
            MinorWorld = minorWorld;
            AreaWorld2 = areaWorld2;
            Anisotropy = anisotropy;
        }

        public float MajorWorld { get; }

        public float MinorWorld { get; }

        public float AreaWorld2 { get; }

        public float Anisotropy { get; }
    }
}
