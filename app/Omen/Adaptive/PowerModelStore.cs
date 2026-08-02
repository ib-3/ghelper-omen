using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GHelper;
using GHelper.Helpers;

namespace OmenCore.Hardware.Adaptive
{
    /// <summary>
    /// Handles persistence of the v2 power model, including one-way
    /// migration from the v1 single-workload feedforward map.
    /// </summary>
    public static class PowerModelStore
    {
        public const string StorageKey = "gpu_power_model_v2";
        public const string LegacyKey  = "gpu_feedforward_map";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>
        /// Load the model. Tries v2 first, then falls back to v1 with migration.
        /// Returns null if nothing stored or if GPU name mismatches.
        /// </summary>
        public static FeedforwardMapV2? Load(string currentGpuName, string? currentDriverVersion = null)
        {
            // 1. Try v2
            try
            {
                string? v2Json = AppConfig.GetString(StorageKey);
                if (!string.IsNullOrEmpty(v2Json))
                {
                    var map = JsonSerializer.Deserialize<FeedforwardMapV2>(v2Json, JsonOpts);
                    if (map != null)
                    {
                        if (!ValidateGpuMatch(map.GpuName, currentGpuName))
                        {
                            Logger.WriteLine($"[PowerModel] Discarding v2 map: GPU mismatch (stored='{map.GpuName}', current='{currentGpuName}')");
                            return null;
                        }
                        Logger.WriteLine($"[PowerModel] Loaded v2 map: {map.Workloads.Values.Sum(l => l.Count)} buckets, {map.TotalSamples} samples");
                        RunDecayIfStale(map);
                        return map;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[PowerModel] Failed to load v2 map: " + ex.Message);
            }

            // 2. Try v1 (legacy)
            try
            {
                string? v1Json = AppConfig.GetString(LegacyKey);
                if (!string.IsNullOrEmpty(v1Json))
                {
                    var v1 = JsonSerializer.Deserialize<LegacyV1Map>(v1Json, JsonOpts);
                    if (v1 != null && v1.Points != null && v1.Points.Count > 0)
                    {
                        if (!ValidateGpuMatch(v1.GpuName, currentGpuName))
                        {
                            Logger.WriteLine($"[PowerModel] Discarding v1 map: GPU mismatch");
                            return null;
                        }
                        var migrated = MigrateFromV1(v1, currentGpuName, currentDriverVersion);
                        Logger.WriteLine($"[PowerModel] Migrated v1 map ({v1.Points.Count} pts) → v2 under 'Gaming' class");
                        Save(migrated);
                        return migrated;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[PowerModel] Failed to migrate v1 map: " + ex.Message);
            }

            return null;
        }

        /// <summary>Save the model. Updates LastLearnedAtUtc.</summary>
        public static void Save(FeedforwardMapV2 map)
        {
            try
            {
                map.LastLearnedAtUtc = DateTime.UtcNow;
                string json = JsonSerializer.Serialize(map, JsonOpts);
                AppConfig.Set(StorageKey, json);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[PowerModel] Failed to save: " + ex.Message);
            }
        }

        /// <summary>Delete the v2 model (and optionally the legacy v1 entry).</summary>
        public static void Clear(bool clearLegacyToo = true)
        {
            try { AppConfig.Set(StorageKey, ""); } catch { }
            if (clearLegacyToo)
            {
                try { AppConfig.Set(LegacyKey, ""); } catch { }
            }
        }

        // ----------------------------------------------------------------

        private static bool ValidateGpuMatch(string? mapGpu, string? currentGpu)
        {
            if (string.IsNullOrEmpty(mapGpu) || string.IsNullOrEmpty(currentGpu))
                return true;  // can't validate — allow
            return string.Equals(mapGpu, currentGpu, StringComparison.OrdinalIgnoreCase);
        }

        private static void RunDecayIfStale(FeedforwardMapV2 map)
        {
            if (DateTime.UtcNow - map.LastDecayAtUtc > TimeSpan.FromHours(24))
            {
                var learner = new AdaptiveLearner();
                learner.SetModel(map);
                learner.DecayOldBuckets();
                Logger.WriteLine("[PowerModel] Ran stale decay on load");
            }
        }

        private static FeedforwardMapV2 MigrateFromV1(LegacyV1Map v1, string gpuName, string? driverVersion)
        {
            var v2 = new FeedforwardMapV2
            {
                Version         = 2,
                GpuName         = gpuName,
                DriverVersion   = driverVersion ?? "",
                CalibratedAtUtc = v1.CalibratedAt == default
                    ? DateTime.UtcNow
                    : v1.CalibratedAt.ToUniversalTime(),
                LastLearnedAtUtc = DateTime.UtcNow,
                LastDecayAtUtc   = DateTime.UtcNow,
                TotalSamples     = v1.Points.Count
            };

            // v1 was single-workload; assume Gaming as the closest analog
            // (most users calibrated with FurMark or a game).
            var buckets = v2.GetWorkload(WorkloadClass.Gaming);
            foreach (var p in v1.Points.OrderBy(p => p.Clock))
            {
                int gridClock = ((p.Clock + 12) / 25) * 25;
                // De-duplicate: if grid clock already exists, merge
                var existing = buckets.FirstOrDefault(b => b.ClockMHz == gridClock);
                if (existing != null)
                {
                    existing.PowerEstimate = (existing.PowerEstimate + p.Power) / 2.0;
                    existing.N += 1;
                }
                else
                {
                    buckets.Add(new CalibrationBucket
                    {
                        ClockMHz      = gridClock,
                        PowerEstimate = p.Power,
                        Weight        = 0.7,    // somewhat trusted but not as much as fresh
                        N             = 1,
                        LastSeenUtc   = DateTime.UtcNow,
                        LastTempC     = p.Temp
                    });
                }
            }
            return v2;
        }

        // ----------------------------------------------------------------
        //  Legacy v1 schema (used only for deserialization during migration)
        // ----------------------------------------------------------------

        private sealed class LegacyV1Map
        {
            public int Version { get; set; } = 1;
            public string GpuName { get; set; } = "";
            public DateTime CalibratedAt { get; set; }
            public List<LegacyV1Point> Points { get; set; } = new();
        }

        private sealed class LegacyV1Point
        {
            public int Clock { get; set; }
            public double Power { get; set; }
            public int Temp { get; set; }
            public int Util { get; set; }
        }
    }
}
