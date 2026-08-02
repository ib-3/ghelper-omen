using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GHelper;
using GHelper.Helpers;
using OmenCore.Hardware.Adaptive;

namespace OmenCore.Hardware.Calibration
{
    /// <summary>
    /// Orchestrates the calibration: stops the main power loop, runs each
    /// scene across a clock schedule, samples NVML power/temp/util, builds
    /// a v2 feedforward map, and saves it. Crash-safe: registers a
    /// ProcessExit handler that resets the GPU clocks.
    /// </summary>
    public sealed class GpuCalibrator : IDisposable
    {
        // ---- Timing (per clock step) ----
        private const int RampMs       = 1500;   // wait for clock + voltage to settle
        private const int SampleMs     = 1000;   // sampling window per step
        // The runner samples at 10 Hz during the sampling window, so we
        // typically get ~10 power readings per step. We take the median.
        private const int ThermalAbortC = 85;
        private const int UtilAbortPct  = 50;
        private const int IdleUtilPct   = 5;     // Idle scene — different util gate

        private readonly GpuPowerController _controller;
        private readonly IntPtr _nvmlDevice;

        private readonly D3D11BenchmarkRunner _runner = new();
        private bool _wasRunning;
        private bool _stopGuardHeld;
        private EventHandler? _processExitHandler;
        private bool _disposed;

        public GpuCalibrator(GpuPowerController controller, IntPtr nvmlDevice)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _nvmlDevice = nvmlDevice;
        }

        // ============================================================
        //  Public API
        // ============================================================

        public async Task<CalibrationResult> RunCalibrationAsync(
            IProgress<CalibrationProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (_disposed)
                return CalibrationResult.NotAvailable("Calibrator already disposed");

            // ---- Validate NVML ----
            // We need _nvmlDevice to be the same one the controller uses.
            if (_nvmlDevice == IntPtr.Zero)
                return CalibrationResult.NotAvailable("NVML device handle is null");

            // Verify we have clock-lock permission
            int probe = NvmlNative.nvmlDeviceSetGpuLockedClocks(_nvmlDevice, MinClockLimit, MinClockLimit);
            if (probe == NvmlResult.ErrorNoPermission)
                return CalibrationResult.NotAvailable("NVML clock-lock requires administrator privileges.");
            if (probe == NvmlResult.Success)
                NvmlNative.nvmlDeviceResetGpuLockedClocks(_nvmlDevice);
            else if (probe != NvmlResult.Success)
                return CalibrationResult.NotAvailable($"NVML clock-lock not supported (code {probe}).");

            // ---- Query max boost clock to cap the schedule ----
            int maxBoostMHz = MaxClockLimit;
            if (NvmlNative.nvmlDeviceGetMaxClockInfo(_nvmlDevice, 0, out uint mb) == NvmlResult.Success && mb > 0)
            {
                maxBoostMHz = Math.Min((int)mb, MaxClockLimit);
                Logger.WriteLine($"[Calibration] Detected max GPU boost: {maxBoostMHz} MHz");
            }

            // ---- Init D3D11 on the NVIDIA dGPU ----
            string gpuName = "";
            var nameBuf = new System.Text.StringBuilder(64);
            if (NvmlNative.nvmlDeviceGetName(_nvmlDevice, nameBuf, 64) == NvmlResult.Success)
                gpuName = nameBuf.ToString();

            if (!_runner.Initialize(gpuName, out string d3dFailure))
                return CalibrationResult.NoGpu(d3dFailure);

            // ---- Build the scene list ----
            var scenes = new ICalibrationScene[]
            {
                new AluBoundScene(),
                new GamingScene(),
                new TextureBoundScene(),
                new IdleScene()
            };

            // ---- Build the clock schedule (descending — thermally safer) ----
            int[] clockSteps = GenerateCalibrationSteps(maxBoostMHz);

            // ---- Pause the main power loop ----
            Logger.WriteLine("[Calibration] Pausing main power loop");
            _wasRunning = _controller.IsRunning;
            _controller.StopForCalibration();

            // ---- Register crash handler ----
            _processExitHandler = (s, e) =>
            {
                try { NvmlNative.nvmlDeviceResetGpuLockedClocks(_nvmlDevice); } catch { }
            };
            AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

            // ---- Storage ----
            var collected = new Dictionary<WorkloadClass, List<(int clock, double power, int temp, int util)>>();
            var startTime = DateTime.UtcNow;

            try
            {
                for (int sceneIdx = 0; sceneIdx < scenes.Length; sceneIdx++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return CalibrationResult.Cancelled(CountCollected(collected), DateTime.UtcNow - startTime);

                    var scene = scenes[sceneIdx];
                    Logger.WriteLine($"[Calibration] === Scene {sceneIdx + 1}/{scenes.Length}: {scene.Name} ({scene.TargetClass}) ===");

                    // Per-scene collected list
                    if (!collected.ContainsKey(scene.TargetClass))
                        collected[scene.TargetClass] = new List<(int, double, int, int)>();

                    double lastReportedPower = 0;
                    int lastReportedTemp = 0;
                    int lastReportedUtil = 0;

                    progress?.Report(new CalibrationProgress
                    {
                        SceneIndex = sceneIdx,
                        TotalScenes = scenes.Length,
                        SceneName = scene.Name,
                        WorkloadClass = scene.TargetClass,
                        StepIndex = 0,
                        TotalSteps = clockSteps.Length,
                        ClockMHz = 0,
                        PowerWatts = lastReportedPower,
                        TempC = lastReportedTemp,
                        UtilPct = lastReportedUtil,
                        Phase = "ramp"
                    });

                    // Initialize scene ONCE per scene (expensive — shader
                    // compilation, texture upload). The render loop below
                    // uses the same scene object across all clock steps.
                    try
                    {
                        _runner.InitializeScene(scene);
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine($"[Calibration] Scene init failed for {scene.Name}: {ex.Message}");
                        continue;  // skip this scene, try the next one
                    }

                    try
                    {
                        for (int stepIdx = 0; stepIdx < clockSteps.Length; stepIdx++)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return CalibrationResult.Cancelled(CountCollected(collected), DateTime.UtcNow - startTime);

                            int clock = clockSteps[stepIdx];

                            // Lock the clock
                            int lockResult = NvmlNative.nvmlDeviceSetGpuLockedClocks(
                                _nvmlDevice, MinClockLimit, (uint)clock);
                            if (lockResult != NvmlResult.Success)
                            {
                                Logger.WriteLine($"[Calibration] Step {stepIdx + 1}: clock lock failed (code {lockResult}), skipping");
                                continue;
                            }

                            // ---- Ramp phase ----
                            progress?.Report(new CalibrationProgress
                            {
                                SceneIndex = sceneIdx,
                                TotalScenes = scenes.Length,
                                SceneName = scene.Name,
                                WorkloadClass = scene.TargetClass,
                                StepIndex = stepIdx + 1,
                                TotalSteps = clockSteps.Length,
                                ClockMHz = clock,
                                PowerWatts = lastReportedPower,
                                TempC = lastReportedTemp,
                                UtilPct = lastReportedUtil,
                                Phase = "ramp"
                            });

                            // Cancellable wait for ramp
                            if (!await WaitAsync(RampMs, cancellationToken))
                                return CalibrationResult.Cancelled(CountCollected(collected), DateTime.UtcNow - startTime);

                            // ---- Sampling phase ----
                            var powerSamples = new List<double>();
                            var utilSamples = new List<int>();
                            int maxTempDuringSample = 0;
                            int lastTemp = lastReportedTemp;

                            _runner.RunSceneSampling(
                                scene,
                                TimeSpan.FromMilliseconds(SampleMs),
                                () =>
                                {
                                    if (NvmlNative.nvmlDeviceGetPowerUsage(_nvmlDevice, out uint pMw) == NvmlResult.Success)
                                        powerSamples.Add(pMw / 1000.0);
                                    if (NvmlNative.nvmlDeviceGetTemperature(_nvmlDevice, 0, out uint tC) == NvmlResult.Success)
                                    {
                                        lastTemp = (int)tC;
                                        maxTempDuringSample = Math.Max(maxTempDuringSample, (int)tC);
                                    }
                                    if (NvmlNative.nvmlDeviceGetUtilizationRates(_nvmlDevice, out NvmlUtilization u) == NvmlResult.Success)
                                        utilSamples.Add((int)u.gpu);

                                    lastReportedPower = powerSamples.Count > 0 ? powerSamples[^1] : lastReportedPower;
                                    lastReportedTemp = lastTemp;
                                    lastReportedUtil = utilSamples.Count > 0 ? utilSamples[^1] : lastReportedUtil;

                                    progress?.Report(new CalibrationProgress
                                    {
                                        SceneIndex = sceneIdx,
                                        TotalScenes = scenes.Length,
                                        SceneName = scene.Name,
                                        WorkloadClass = scene.TargetClass,
                                        StepIndex = stepIdx + 1,
                                        TotalSteps = clockSteps.Length,
                                        ClockMHz = clock,
                                        PowerWatts = lastReportedPower,
                                        TempC = lastReportedTemp,
                                        UtilPct = lastReportedUtil,
                                        Phase = "sampling"
                                    });
                                },
                                cancellationToken);

                            if (cancellationToken.IsCancellationRequested)
                                return CalibrationResult.Cancelled(CountCollected(collected), DateTime.UtcNow - startTime);

                            // ---- Abort checks ----
                            if (maxTempDuringSample >= ThermalAbortC)
                            {
                                Logger.WriteLine($"[Calibration] Thermal abort at {scene.Name} {clock}MHz: {maxTempDuringSample}°C");
                                return CalibrationResult.ThermalAbort(stepIdx + 1, scene.Name, DateTime.UtcNow - startTime);
                            }

                            Logger.WriteLine($"[Calibration] Util trace at {scene.Name} {clock}MHz: [{string.Join(", ", utilSamples)}]");

                            // NVML often reports warmup lag (e.g. 0% or 9% from DWM) for up to 500ms when waking up.
                            // By taking the top 4 samples of the 900ms window, we isolate the steady-state rendering utilization
                            // and completely discard the warmup phase, preventing false aborts.
                            var validUtils = utilSamples.OrderByDescending(u => u).Take(4).ToList();

                            int avgUtil = validUtils.Count > 0 ? (int)validUtils.Average() : 0;

                            if (scene.TargetClass != WorkloadClass.Idle && avgUtil < UtilAbortPct)
                            {
                                Logger.WriteLine($"[Calibration] Util abort at {scene.Name} {clock}MHz: {avgUtil}% (avg)");
                                return CalibrationResult.UtilAbort(stepIdx + 1, scene.Name, DateTime.UtcNow - startTime);
                            }

                            // ---- Record the sample (median power) ----
                            if (powerSamples.Count > 0)
                            {
                                powerSamples.Sort();
                                double medianPower = powerSamples[powerSamples.Count / 2];
                                collected[scene.TargetClass].Add((clock, medianPower, lastTemp, avgUtil));
                                Logger.WriteLine($"[Calibration] {scene.Name} @ {clock}MHz: {medianPower:F1}W, {lastTemp}°C, {avgUtil}% avg util");
                            }
                        }
                    }
                    finally
                    {
                        // Unbind + dispose scene resources before moving on
                        _runner.UnbindScene();
                        scene.Dispose();
                    }
                }

                // ---- Build the model ----
                var map = BuildModel(collected, gpuName);
                if (map.Workloads.Sum(kv => kv.Value.Count) == 0)
                {
                    return new CalibrationResult
                    {
                        Outcome = CalibrationOutcome.Error,
                        Duration = DateTime.UtcNow - startTime,
                        Message = "No samples collected — calibration produced an empty map."
                    };
                }

                // ---- Save ----
                PowerModelStore.Save(map);
                Logger.WriteLine($"[Calibration] Saved map: {map.Workloads.Sum(kv => kv.Value.Count)} points");

                return CalibrationResult.Success(map, map.Workloads.Sum(kv => kv.Value.Count), DateTime.UtcNow - startTime);
            }
            catch (OperationCanceledException)
            {
                return CalibrationResult.Cancelled(CountCollected(collected), DateTime.UtcNow - startTime);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[Calibration] Error: " + ex);
                return CalibrationResult.Error(ex);
            }
            finally
            {
                // ALWAYS reset clocks, even on abort
                try { NvmlNative.nvmlDeviceResetGpuLockedClocks(_nvmlDevice); }
                catch (Exception ex) { Logger.WriteLine("[Calibration] Reset clocks failed: " + ex.Message); }

                if (_processExitHandler != null)
                {
                    AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
                    _processExitHandler = null;
                }

                // Restart main loop if it was running before
                if (_wasRunning)
                {
                    Logger.WriteLine("[Calibration] Restarting main power loop");
                    _controller.RestartAfterCalibration();
                }
            }
        }

        // ============================================================
        //  Helpers
        // ============================================================

        private const int MinClockLimit = 210;
        private const int MaxClockLimit = 3000;

        /// <summary>
        /// Build a descending clock schedule with adaptive step sizes:
        /// 250 MHz at the top (saturated range), 100 MHz in the middle
        /// (transition range), 50 MHz at the bottom (steep V/F range).
        /// </summary>
        private static int[] GenerateCalibrationSteps(int maxBoostMHz)
        {
            var steps = new List<int>();

            // Top range: 250 MHz steps from rounded-up maxBoost down to 2000
            int top = ((maxBoostMHz + 249) / 250) * 250;
            for (int c = top; c >= 2000; c -= 250) steps.Add(c);

            // Middle range: 100 MHz steps from 1900 down to 800
            for (int c = 1900; c >= 800; c -= 100) steps.Add(c);

            // Bottom range: 50 MHz steps from 750 down to MinClockLimit
            for (int c = 750; c >= MinClockLimit; c -= 50) steps.Add(c);

            return steps.ToArray();
        }

        private static int CountCollected(Dictionary<WorkloadClass, List<(int, double, int, int)>> collected)
            => collected.Values.Sum(l => l.Count);

        private static async Task<bool> WaitAsync(int ms, CancellationToken token)
        {
            // Break the wait into 100ms chunks so cancel is responsive
            int remaining = ms;
            while (remaining > 0)
            {
                if (token.IsCancellationRequested) return false;
                int chunk = Math.Min(100, remaining);
                try { await Task.Delay(chunk, token); }
                catch (OperationCanceledException) { return false; }
                remaining -= chunk;
            }
            return true;
        }

        private static FeedforwardMapV2 BuildModel(
            Dictionary<WorkloadClass, List<(int clock, double power, int temp, int util)>> collected,
            string gpuName)
        {
            var map = new FeedforwardMapV2
            {
                Version           = 2,
                GpuName           = gpuName,
                CalibratedAtUtc   = DateTime.UtcNow,
                LastLearnedAtUtc  = DateTime.UtcNow,
                LastDecayAtUtc    = DateTime.UtcNow,
                TotalSamples      = collected.Values.Sum(l => l.Count)
            };

            foreach (var kv in collected)
            {
                var buckets = map.GetWorkload(kv.Key);
                // Snap to 25 MHz grid, deduplicate, sort by clock
                var grouped = kv.Value
                    .GroupBy(s => ((s.clock + 12) / 25) * 25)
                    .OrderBy(g => g.Key);

                foreach (var g in grouped)
                {
                    var samples = g.ToList();
                    double medianPower;
                    if (samples.Count == 1)
                    {
                        medianPower = samples[0].power;
                    }
                    else
                    {
                        var sorted = samples.OrderBy(s => s.power).ToList();
                        medianPower = sorted[sorted.Count / 2].power;
                    }
                    int avgTemp = (int)samples.Average(s => s.temp);
                    int avgUtil = (int)samples.Average(s => s.util);

                    buckets.Add(new CalibrationBucket
                    {
                        ClockMHz      = g.Key,
                        PowerEstimate = medianPower,
                        Weight        = 1.0,   // freshly calibrated
                        N             = samples.Count,
                        LastSeenUtc   = DateTime.UtcNow,
                        LastTempC     = avgTemp
                    });
                }
            }

            return map;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _runner.Dispose();
            GC.SuppressFinalize(this);
        }

        ~GpuCalibrator() => Dispose();
    }
}
