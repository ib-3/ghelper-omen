using System;
using System.Collections.Generic;
using System.Linq;

namespace OmenCore.Hardware.Adaptive
{
    /// <summary>
    /// One learned (clock → power) data point for a specific workload class.
    /// Bayesian-weighted: powerEstimate is the running EMA, weight decays
    /// over time, N counts total observations.
    /// </summary>
    public sealed class CalibrationBucket
    {
        public int ClockMHz { get; set; }
        public double PowerEstimate { get; set; }
        public double Weight { get; set; } = 1.0;
        public long N { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int LastTempC { get; set; }

        public CalibrationBucket Clone() => new()
        {
            ClockMHz      = ClockMHz,
            PowerEstimate = PowerEstimate,
            Weight        = Weight,
            N             = N,
            LastSeenUtc   = LastSeenUtc,
            LastTempC     = LastTempC
        };
    }

    /// <summary>
    /// Learned thermal compensation: power drops as temp rises.
    /// trackedPower = basePower - coefficient * (currentTemp - baselineTempC)
    /// </summary>
    public sealed class ThermalCompensation
    {
        public double Coefficient { get; set; }    // W per °C above baseline
        public double BaselineTempC { get; set; } = 50.0;
        public long Samples { get; set; }

        public double Adjust(double basePowerW, int currentTempC)
        {
            if (Samples < 20) return basePowerW;  // not enough data to trust
            double delta = currentTempC - BaselineTempC;
            if (delta <= 0) return basePowerW;
            return basePowerW - Coefficient * delta;
        }
    }

    /// <summary>
    /// v2 adaptive power model. Contains per-workload-class maps of
    /// (clock → power) buckets, plus thermal compensation.
    /// </summary>
    public sealed class FeedforwardMapV2
    {
        public int Version { get; set; } = 2;
        public string GpuName { get; set; } = "";
        public string GpuPciId { get; set; } = "";
        public string DriverVersion { get; set; } = "";
        public DateTime CalibratedAtUtc { get; set; }
        public DateTime LastLearnedAtUtc { get; set; }
        public DateTime LastDecayAtUtc { get; set; }
        public long TotalSamples { get; set; }

        /// <summary>
        /// Workload-keyed bucket lists. Key is WorkloadClass.ToString()
        /// for JSON portability (string enum keys survive renaming).
        /// </summary>
        public Dictionary<string, List<CalibrationBucket>> Workloads { get; set; } = new();

        public ThermalCompensation Thermal { get; set; } = new();

        // ----------------------------------------------------------------
        //  Access helpers
        // ----------------------------------------------------------------

        public List<CalibrationBucket> GetWorkload(WorkloadClass cls)
        {
            string key = cls.ToString();
            if (!Workloads.TryGetValue(key, out var list))
            {
                list = new List<CalibrationBucket>();
                Workloads[key] = list;
            }
            return list;
        }

        public IEnumerable<CalibrationBucket> AllBuckets()
            => Workloads.Values.SelectMany(b => b);

        public void RemoveBucketsWhere(Func<CalibrationBucket, bool> predicate)
        {
            foreach (var key in Workloads.Keys.ToList())
            {
                Workloads[key].RemoveAll(b => predicate(b));
                if (Workloads[key].Count == 0 && key != WorkloadClass.Gaming.ToString())
                    Workloads.Remove(key);
            }
        }

        /// <summary>
        /// Look up the clock that should produce targetWatts under the given
        /// workload class, with linear interpolation. Returns -1 if no data.
        /// </summary>
        public int LookupClock(WorkloadClass cls, double targetWatts, int currentTempC)
        {
            var buckets = GetWorkload(cls);
            if (buckets.Count == 0)
            {
                // Fallback: try Gaming (most populated from v1 migration)
                buckets = GetWorkload(WorkloadClass.Gaming);
                if (buckets.Count == 0) return -1;
            }

            // Sort ascending by power (defensive — should already be sorted)
            var sorted = buckets.OrderBy(b => b.PowerEstimate).ToList();

            // Degenerate: only one point
            if (sorted.Count == 1)
            {
                double adjusted = Thermal.Adjust(sorted[0].PowerEstimate, currentTempC);
                return targetWatts <= adjusted ? sorted[0].ClockMHz : sorted[0].ClockMHz;
            }

            // Find bracketing pair
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var a = sorted[i];
                var b = sorted[i + 1];

                double aPwr = Thermal.Adjust(a.PowerEstimate, currentTempC);
                double bPwr = Thermal.Adjust(b.PowerEstimate, currentTempC);

                if (targetWatts >= aPwr && targetWatts <= bPwr)
                {
                    double denom = bPwr - aPwr;
                    if (Math.Abs(denom) < 0.01) return a.ClockMHz;
                    double t = (targetWatts - aPwr) / denom;
                    return (int)Math.Round(a.ClockMHz + t * (b.ClockMHz - a.ClockMHz));
                }
            }

            // Out of range — clamp
            return targetWatts < Thermal.Adjust(sorted[0].PowerEstimate, currentTempC)
                ? sorted[0].ClockMHz
                : sorted[^1].ClockMHz;
        }

        /// <summary>
        /// Quick health summary for UI display.
        /// </summary>
        public (int totalBuckets, long totalSamples, string oldestClass) HealthSummary()
        {
            int totalBuckets = Workloads.Values.Sum(l => l.Count);
            long totalSamples = Workloads.Values.SelectMany(l => l).Sum(b => b.N);

            string oldest = "";
            DateTime oldestDate = DateTime.MaxValue;
            foreach (var kv in Workloads)
            {
                foreach (var b in kv.Value)
                {
                    if (b.LastSeenUtc < oldestDate)
                    {
                        oldestDate = b.LastSeenUtc;
                        oldest     = kv.Key;
                    }
                }
            }
            return (totalBuckets, totalSamples, oldest);
        }
    }
}
