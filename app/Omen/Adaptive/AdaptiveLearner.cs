using System;
using System.Collections.Generic;
using System.Linq;

namespace OmenCore.Hardware.Adaptive
{
    /// <summary>Outcome of a single learning attempt.</summary>
    public enum LearningOutcome
    {
        Learned,                // bucket updated
        SkippedTransient,       // workload class not stable
        SkippedNotInDeadband,   // controller still regulating, sample is biased
        SkippedUnstablePower,   // power jitter too high
        SkippedUnstableTemp,    // temperature drifting
        SkippedOutlier,         // observation deviated too far from current estimate
        SkippedEcoMode,         // eco mode active — readings unreliable
        SkippedNoModel          // no model loaded
    }

    /// <summary>
    /// Continuously updates a FeedforwardMapV2 from observed
    /// (workloadClass, clock, power, temp) tuples when the system is
    /// in a stable state. Uses Bayesian-weighted EMA with outlier
    /// rejection and time-based decay.
    /// </summary>
    /// <remarks>
    /// Thread-safe: all mutations are guarded by an internal lock.
    /// </remarks>
    public sealed class AdaptiveLearner
    {
        // ---- Learning rate schedule ----
        // Fresh buckets learn fast (alpha=0.10); as they accumulate samples,
        // alpha decays to MinLearningRate so a single bad sample can't
        // derail an established estimate.
        private const double BaseLearningRate   = 0.10;
        private const double MinLearningRate    = 0.02;
        private const double SampleDecayFactor  = 0.01;   // alpha *= 1/(1+N*0.01)

        // ---- Outlier rejection ----
        // After a bucket has >OutlierMinSamples observations, samples that
        // deviate more than max(absW, relFrac * est) are rejected.
        private const long   OutlierMinSamples  = 5;
        private const double OutlierAbsWatts    = 5.0;
        private const double OutlierRelFrac     = 0.20;

        // ---- Stability requirements ----
        private const double PowerStabilityStdW = 1.5;     // W stddev over window
        private const double TempStabilityDeltaC = 3.0;    // max-min over window
        private const int    StabilityMinSamples = 5;

        // ---- Deadband ----
        // Only learn when the controller has settled: |observed - target| <= this
        private const double DeadbandForLearnW = 2.0;

        // ---- Grid snapping ----
        private const int GridMhz = 25;

        // ---- Decay ----
        private const double DecayFactor         = 0.95;
        private static readonly TimeSpan DecayAgeThreshold = TimeSpan.FromDays(7);
        private const double WeightPruneThreshold = 0.10;

        private readonly object _lock = new();
        private FeedforwardMapV2? _map;

        public bool HasModel => _map != null;

        public void SetModel(FeedforwardMapV2 map)
        {
            lock (_lock) _map = map;
        }

        public void ClearModel()
        {
            lock (_lock) _map = null;
        }

        /// <summary>
        /// Attempt to learn from a recent observation. Returns the outcome.
        /// </summary>
        public LearningOutcome TryLearn(
            WorkloadClass workloadClass,
            int observedClockMHz,
            double observedPowerWatts,
            int observedTempC,
            int targetWatts,
            IReadOnlyList<double>? recentPowerSamples,
            IReadOnlyList<int>? recentTempSamples,
            bool ecoModeActive)
        {
            FeedforwardMapV2? map;
            lock (_lock) map = _map;
            if (map == null) return LearningOutcome.SkippedNoModel;

            if (ecoModeActive) return LearningOutcome.SkippedEcoMode;
            if (workloadClass == WorkloadClass.Transient)
                return LearningOutcome.SkippedTransient;

            // Power stability check
            if (recentPowerSamples != null && recentPowerSamples.Count >= StabilityMinSamples)
            {
                double std = StdDev(recentPowerSamples);
                if (std > PowerStabilityStdW)
                    return LearningOutcome.SkippedUnstablePower;
            }

            // Temp stability check
            if (recentTempSamples != null && recentTempSamples.Count >= StabilityMinSamples)
            {
                double delta = recentTempSamples.Max() - recentTempSamples.Min();
                if (delta > TempStabilityDeltaC)
                    return LearningOutcome.SkippedUnstableTemp;
            }

            // Deadband check — sample is only unbiased when controller has settled
            if (Math.Abs(observedPowerWatts - targetWatts) > DeadbandForLearnW)
                return LearningOutcome.SkippedNotInDeadband;

            // Snap to grid
            int gridClock = SnapToGrid(observedClockMHz);

            lock (_lock)
            {
                if (_map == null) return LearningOutcome.SkippedNoModel;
                var buckets = _map.GetWorkload(workloadClass);

                // Find or create bucket
                var bucket = buckets.FirstOrDefault(b => b.ClockMHz == gridClock);
                bool isNewBucket = bucket == null;
                if (isNewBucket)
                {
                    bucket = new CalibrationBucket
                    {
                        ClockMHz    = gridClock,
                        LastSeenUtc = DateTime.UtcNow
                    };
                    buckets.Add(bucket);
                }

                // Outlier rejection (only for established buckets)
                if (bucket.N > OutlierMinSamples)
                {
                    double threshold = Math.Max(OutlierAbsWatts, OutlierRelFrac * bucket.PowerEstimate);
                    if (Math.Abs(observedPowerWatts - bucket.PowerEstimate) > threshold)
                        return LearningOutcome.SkippedOutlier;
                }

                // Bayesian-weighted EMA update
                double alpha = Math.Max(
                    MinLearningRate,
                    BaseLearningRate / (1.0 + bucket.N * SampleDecayFactor));

                if (bucket.N == 0)
                {
                    bucket.PowerEstimate = observedPowerWatts;
                    bucket.Weight        = 1.0;
                }
                else
                {
                    bucket.PowerEstimate = (1.0 - alpha) * bucket.PowerEstimate + alpha * observedPowerWatts;
                    bucket.Weight        = Math.Min(1.0, bucket.Weight + 0.05);
                }

                bucket.N           += 1;
                bucket.LastSeenUtc  = DateTime.UtcNow;
                bucket.LastTempC    = observedTempC;

                _map.TotalSamples  += 1;

                // Thermal compensation update (best-effort, very slow)
                UpdateThermalCompensation(_map, gridClock, observedPowerWatts, observedTempC, workloadClass);

                return LearningOutcome.Learned;
            }
        }

        /// <summary>
        /// Apply time-based decay to buckets not seen recently.
        /// Call this once per day (or on load if last decay was &gt;24h ago).
        /// </summary>
        public void DecayOldBuckets()
        {
            lock (_lock)
            {
                if (_map == null) return;
                var cutoff = DateTime.UtcNow - DecayAgeThreshold;

                _map.RemoveBucketsWhere(b =>
                {
                    if (b.LastSeenUtc < cutoff)
                    {
                        b.Weight *= DecayFactor;
                        if (b.Weight < WeightPruneThreshold) return true;
                    }
                    return false;
                });

                _map.LastDecayAtUtc = DateTime.UtcNow;
            }
        }

        public FeedforwardMapV2? GetSnapshot()
        {
            lock (_lock) return _map;  // callers should treat as read-only
        }

        // ----------------------------------------------------------------

        private static int SnapToGrid(int mhz)
        {
            return ((mhz + GridMhz / 2) / GridMhz) * GridMhz;
        }

        private static double StdDev(IReadOnlyList<double> values)
        {
            if (values.Count < 2) return 0;
            double mean = values.Average();
            double sum  = values.Sum(v => (v - mean) * (v - mean));
            return Math.Sqrt(sum / values.Count);
        }

        /// <summary>
        /// Very simple thermal-coefficient learner: for the current clock,
        /// compare observed power to the bucket's power estimate. If temp
        /// is higher than baseline and power is lower, nudge coefficient up.
        /// </summary>
        private static void UpdateThermalCompensation(
            FeedforwardMapV2 map,
            int gridClock,
            double observedPower,
            int observedTemp,
            WorkloadClass cls)
        {
            // Only learn thermal when we have a trusted baseline at this clock
            var bucket = map.GetWorkload(cls).FirstOrDefault(b => b.ClockMHz == gridClock);
            if (bucket == null || bucket.N < 10) return;

            double expected = bucket.PowerEstimate;
            double delta = expected - observedPower;  // positive = less power than expected (thermal throttle)

            if (observedTemp > map.Thermal.BaselineTempC && delta > 0.5)
            {
                double tempDelta = observedTemp - map.Thermal.BaselineTempC;
                double observedCoeff = delta / tempDelta;

                // Slow EMA: 5% per sample
                if (map.Thermal.Samples == 0)
                {
                    map.Thermal.Coefficient = observedCoeff;
                }
                else
                {
                    map.Thermal.Coefficient = 0.95 * map.Thermal.Coefficient + 0.05 * observedCoeff;
                }
                map.Thermal.Samples += 1;
            }
        }
    }
}
