using System;
using System.Collections.Generic;
using OmenCore.Hardware.Adaptive;

namespace OmenCore.Hardware.Calibration
{
    /// <summary>Outcome of a calibration run.</summary>
    public enum CalibrationOutcome
    {
        /// <summary>All scenes completed, map saved.</summary>
        Success,
        /// <summary>User cancelled via the cancel button.</summary>
        Cancelled,
        /// <summary>Aborted because GPU exceeded thermal threshold.</summary>
        ThermalAbort,
        /// <summary>Aborted because workload utilization dropped (benchmark stopped running).</summary>
        UtilAbort,
        /// <summary>NVML not initialized or no admin privileges.</summary>
        NotAvailable,
        /// <summary>Could not create D3D11 device on the NVIDIA dGPU.</summary>
        NoGpu,
        /// <summary>Unexpected exception.</summary>
        Error
    }

    /// <summary>Result returned by RunCalibrationAsync.</summary>
    public sealed class CalibrationResult
    {
        public CalibrationOutcome Outcome { get; init; }
        public string Message { get; init; } = "";
        public int ScenesCompleted { get; init; }
        public int TotalScenes { get; init; }
        public int PointsCollected { get; init; }
        public TimeSpan Duration { get; init; }
        public FeedforwardMapV2? Map { get; init; }

        public bool IsSuccess => Outcome == CalibrationOutcome.Success;

        public static CalibrationResult Success(FeedforwardMapV2 map, int points, TimeSpan duration) => new()
        {
            Outcome = CalibrationOutcome.Success,
            Map = map,
            PointsCollected = points,
            ScenesCompleted = map.Workloads.Count,
            TotalScenes = map.Workloads.Count,
            Duration = duration,
            Message = $"Calibration complete: {points} points across {map.Workloads.Count} workload classes."
        };

        public static CalibrationResult Cancelled(int pointsCollected, TimeSpan duration) => new()
        {
            Outcome = CalibrationOutcome.Cancelled,
            PointsCollected = pointsCollected,
            Duration = duration,
            Message = "Calibration cancelled by user."
        };

        public static CalibrationResult ThermalAbort(int step, string scene, TimeSpan duration) => new()
        {
            Outcome = CalibrationOutcome.ThermalAbort,
            Duration = duration,
            Message = $"Aborted at step {step} ({scene}): GPU exceeded 85°C. Improve cooling and try again."
        };

        public static CalibrationResult UtilAbort(int step, string scene, TimeSpan duration) => new()
        {
            Outcome = CalibrationOutcome.UtilAbort,
            Duration = duration,
            Message = $"Aborted at step {step} ({scene}): GPU utilization dropped below 50%. Is something interfering with the benchmark?"
        };

        public static CalibrationResult NotAvailable(string reason) => new()
        {
            Outcome = CalibrationOutcome.NotAvailable,
            Message = reason
        };

        public static CalibrationResult NoGpu(string reason) => new()
        {
            Outcome = CalibrationOutcome.NoGpu,
            Message = reason
        };

        public static CalibrationResult Error(Exception ex) => new()
        {
            Outcome = CalibrationOutcome.Error,
            Message = ex.Message
        };
    }

    /// <summary>Progress update pushed by the calibrator to the UI.</summary>
    public sealed class CalibrationProgress
    {
        public int SceneIndex { get; init; }       // 0-based
        public int TotalScenes { get; init; }
        public string SceneName { get; init; } = "";
        public WorkloadClass WorkloadClass { get; init; }
        public int StepIndex { get; init; }        // 1-based within scene
        public int TotalSteps { get; init; }
        public int ClockMHz { get; init; }
        public double PowerWatts { get; init; }
        public int TempC { get; init; }
        public int UtilPct { get; init; }
        public string Phase { get; init; } = "";   // "ramp" / "sampling" / "scene_init"

        public int OverallStep =>
            SceneIndex * TotalSteps + StepIndex;

        public int OverallTotal =>
            TotalScenes * TotalSteps;
    }
}
