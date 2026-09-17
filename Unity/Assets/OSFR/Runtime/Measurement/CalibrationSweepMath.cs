using System;
using System.Collections.Generic;
using System.Linq;

namespace OSFR.Measurement
{
    /// <summary>
    /// Deterministic two-objective ranking used by V2 calibration sweeps.
    /// Lower primary and secondary errors are better; a separate regression
    /// guard can exclude candidates before the Pareto frontier is formed.
    /// </summary>
    public static class CalibrationSweepMath
    {
        public readonly struct Candidate
        {
            public Candidate(float parameter, float primaryError, float secondaryError, float worstRegression)
            {
                Parameter = parameter;
                PrimaryError = primaryError;
                SecondaryError = secondaryError;
                WorstRegression = worstRegression;
            }

            public float Parameter { get; }
            public float PrimaryError { get; }
            public float SecondaryError { get; }
            public float WorstRegression { get; }
        }

        public readonly struct Ranking
        {
            public Ranking(Candidate candidate, bool eligible, bool pareto, float idealDistance, bool recommended)
            {
                Candidate = candidate;
                Eligible = eligible;
                Pareto = pareto;
                IdealDistance = idealDistance;
                Recommended = recommended;
            }

            public Candidate Candidate { get; }
            public bool Eligible { get; }
            public bool Pareto { get; }
            public float IdealDistance { get; }
            public bool Recommended { get; }
        }

        public static Ranking[] Rank(
            IReadOnlyList<Candidate> candidates,
            float maximumAllowedRegression = 0.02f)
        {
            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            if (candidates.Count == 0)
            {
                throw new ArgumentException("At least one calibration candidate is required.", nameof(candidates));
            }

            if (maximumAllowedRegression < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumAllowedRegression));
            }

            for (int index = 0; index < candidates.Count; index++)
            {
                Candidate candidate = candidates[index];
                if (!IsFinite(candidate.Parameter) || !IsFinite(candidate.PrimaryError)
                    || !IsFinite(candidate.SecondaryError) || !IsFinite(candidate.WorstRegression)
                    || candidate.PrimaryError < 0.0f || candidate.SecondaryError < 0.0f)
                {
                    throw new ArgumentException("Calibration candidates must contain finite non-negative errors.", nameof(candidates));
                }
            }

            bool[] eligible = candidates
                .Select(candidate => candidate.WorstRegression <= maximumAllowedRegression)
                .ToArray();
            if (!eligible.Any(value => value))
            {
                throw new InvalidOperationException("No calibration candidate satisfies the regression guard.");
            }

            bool[] pareto = new bool[candidates.Count];
            for (int index = 0; index < candidates.Count; index++)
            {
                if (!eligible[index])
                {
                    continue;
                }

                pareto[index] = true;
                for (int other = 0; other < candidates.Count; other++)
                {
                    if (other == index || !eligible[other])
                    {
                        continue;
                    }

                    bool noWorse = candidates[other].PrimaryError <= candidates[index].PrimaryError
                        && candidates[other].SecondaryError <= candidates[index].SecondaryError;
                    bool strictlyBetter = candidates[other].PrimaryError < candidates[index].PrimaryError
                        || candidates[other].SecondaryError < candidates[index].SecondaryError;
                    if (noWorse && strictlyBetter)
                    {
                        pareto[index] = false;
                        break;
                    }
                }
            }

            float minimumPrimary = float.PositiveInfinity;
            float maximumPrimary = float.NegativeInfinity;
            float minimumSecondary = float.PositiveInfinity;
            float maximumSecondary = float.NegativeInfinity;
            for (int index = 0; index < candidates.Count; index++)
            {
                if (!pareto[index])
                {
                    continue;
                }

                minimumPrimary = Math.Min(minimumPrimary, candidates[index].PrimaryError);
                maximumPrimary = Math.Max(maximumPrimary, candidates[index].PrimaryError);
                minimumSecondary = Math.Min(minimumSecondary, candidates[index].SecondaryError);
                maximumSecondary = Math.Max(maximumSecondary, candidates[index].SecondaryError);
            }

            float primaryRange = Math.Max(maximumPrimary - minimumPrimary, 0.000000001f);
            float secondaryRange = Math.Max(maximumSecondary - minimumSecondary, 0.000000001f);
            float[] distances = Enumerable.Repeat(float.PositiveInfinity, candidates.Count).ToArray();
            int recommendedIndex = -1;
            float bestDistance = float.PositiveInfinity;
            for (int index = 0; index < candidates.Count; index++)
            {
                if (!pareto[index])
                {
                    continue;
                }

                float normalizedPrimary = (candidates[index].PrimaryError - minimumPrimary) / primaryRange;
                float normalizedSecondary = (candidates[index].SecondaryError - minimumSecondary) / secondaryRange;
                distances[index] = (float)Math.Sqrt(
                    normalizedPrimary * normalizedPrimary
                    + normalizedSecondary * normalizedSecondary);
                if (distances[index] < bestDistance
                    || (Math.Abs(distances[index] - bestDistance) <= 0.0000001f
                        && candidates[index].Parameter < candidates[recommendedIndex].Parameter))
                {
                    bestDistance = distances[index];
                    recommendedIndex = index;
                }
            }

            var result = new Ranking[candidates.Count];
            for (int index = 0; index < candidates.Count; index++)
            {
                result[index] = new Ranking(
                    candidates[index],
                    eligible[index],
                    pareto[index],
                    distances[index],
                    index == recommendedIndex);
            }

            return result;
        }

        /// <summary>
        /// Selects the lowest parameter whose two errors are both within a
        /// relative tolerance of the best eligible values. This makes a
        /// monotonic calibration prefer the start of its saturation plateau
        /// instead of whichever upper bound happened to be tested.
        /// </summary>
        public static int SelectSmallestNearIdeal(
            IReadOnlyList<Candidate> candidates,
            float maximumAllowedRegression = 0.02f,
            float relativeTolerance = 0.05f)
        {
            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            if (candidates.Count == 0)
            {
                throw new ArgumentException("At least one calibration candidate is required.", nameof(candidates));
            }

            if (maximumAllowedRegression < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumAllowedRegression));
            }

            if (!IsFinite(relativeTolerance) || relativeTolerance < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(relativeTolerance));
            }

            float minimumPrimary = float.PositiveInfinity;
            float minimumSecondary = float.PositiveInfinity;
            for (int index = 0; index < candidates.Count; index++)
            {
                Candidate candidate = candidates[index];
                if (!IsFinite(candidate.Parameter) || !IsFinite(candidate.PrimaryError)
                    || !IsFinite(candidate.SecondaryError) || !IsFinite(candidate.WorstRegression)
                    || candidate.PrimaryError < 0.0f || candidate.SecondaryError < 0.0f)
                {
                    throw new ArgumentException("Calibration candidates must contain finite non-negative errors.", nameof(candidates));
                }

                if (candidate.WorstRegression <= maximumAllowedRegression)
                {
                    minimumPrimary = Math.Min(minimumPrimary, candidate.PrimaryError);
                    minimumSecondary = Math.Min(minimumSecondary, candidate.SecondaryError);
                }
            }

            if (float.IsPositiveInfinity(minimumPrimary))
            {
                throw new InvalidOperationException("No calibration candidate satisfies the regression guard.");
            }

            float primaryLimit = minimumPrimary * (1.0f + relativeTolerance);
            float secondaryLimit = minimumSecondary * (1.0f + relativeTolerance);
            int selectedIndex = -1;
            float smallestParameter = float.PositiveInfinity;
            for (int index = 0; index < candidates.Count; index++)
            {
                Candidate candidate = candidates[index];
                if (candidate.WorstRegression <= maximumAllowedRegression
                    && candidate.PrimaryError <= primaryLimit
                    && candidate.SecondaryError <= secondaryLimit
                    && candidate.Parameter < smallestParameter)
                {
                    selectedIndex = index;
                    smallestParameter = candidate.Parameter;
                }
            }

            return selectedIndex;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
