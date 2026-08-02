using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GHelper;
using GHelper.Helpers;
using OmenCore.Hardware.Adaptive;
using OmenCore.Hardware.Calibration;

namespace OmenCore.Hardware
{
    /// <summary>
    /// v5: Adaptive hybrid GPU power controller.
    ///
    /// Layers (from outermost to innermost):
    ///   1. Feedforward    — best-guess starting clock from learned model
    ///   2. PI controller  — corrects residual error, smooths drift
    ///   3. Adaptive learner — Bayesian update of model from observations
    ///
    /// The user sets a target power once. The system learns the GPU's
    /// behavior across workload classes (idle/light/gaming/compute/membound)
    /// and refines its feedforward map continuously.
    /// </summary>
    public class GpuPowerController : IDisposable
    {
        // ---------- Singleton ----------
        private static GpuPowerController? _instance;
        private static int _lastTargetWatts;

        // ---------- Loop plumbing ----------
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private int _stopGuard;

        // ---------- Shared state (caller ↔ loop) ----------
        private volatile int  _targetPowerWatts = 0;
        private volatile bool _isRunning = false;

        // ---------- Controller state (loop thread only) ----------
        private int    _currentMaxClock = MaxClockLimit;
        private double _prevError;
        private double _filteredPower;
        private bool   _controllerPrimed;

        // ---------- Eco polling ----------
        private long   _lastEcoCheckTick;
        private bool   _cachedEcoMode;
        private int    _logCounter;

        // ---------- Workload classification (loop thread only) ----------
        private readonly WorkloadClassifier _classifier = new();
        private WorkloadClass _currentWorkloadClass = WorkloadClass.Transient;
        private WorkloadClass _lastFfAppliedClass    = WorkloadClass.Transient;

        // ---------- Adaptive learning ----------
        private readonly AdaptiveLearner _learner = new();
        private FeedforwardMapV2? _model;
        private readonly Queue<double> _recentPowerWindow = new();
        private readonly Queue<int>    _recentTempWindow  = new();
        private long _lastLearnLogTick;
        private long _lastModelSaveTick;
        private bool _modelDirty;
        private const long SaveIntervalMs     = 30_000;  // flush every 30s if dirty
        private const long LearnLogIntervalMs = 60_000;  // log learning summary once/min

        // ---------- Dependencies ----------
        private readonly WmiBiosMonitor _monitor;

        // ---------- Tunables ----------
        private const int    MinClockLimit         = 210;
        private const int    MaxClockLimit         = 3000;
        private const int    LoopIntervalMs       = 500;
        private const int    EcoModeSkipMs        = 2000;
        private const int    EcoRecheckMs         = 10_000;
        private const int    SampleTimeoutMs      = 5000;
        private const int    NvmlInitMaxAttempts  = 5;

        // Controller gains
        private const double DeadbandWatts    = 1.5;
        private const double PowerEmaAlpha    = 0.4;
        private const double KpDown           = 18.0;
        private const double KpUp             = 9.0;
        private const double Ki               = 6.0;

        // Re-apply FF when workload class change is detected & stable
        private const int    FfReapplyClockDeltaMhz = 100;

        // ---------- NVML ----------
        private IntPtr  _nvmlDevice = IntPtr.Zero;
        private volatile bool _nvmlInitialized = false;
        private int     _nvmlInitAttempts;
        private readonly object _nvmlInitLock = new();
        private string  _gpuName        = "";
        private string  _driverVersion  = "";

        // ============================================================
        //  Public API
        // ============================================================

        public static GpuPowerController? Instance => _instance;

        /// <summary>Whether the main power loop is currently running.</summary>
        internal bool IsRunning => _isRunning;

        /// <summary>Current NVML device handle (for use by GpuCalibrator).</summary>
        internal IntPtr NvmlDevice => _nvmlDevice;

        /// <summary>Current model health snapshot, for UI display.</summary>
        public static (bool hasModel, int buckets, long samples, string lastClass) GetModelHealth()
        {
            var inst = _instance;
            if (inst == null) return (false, 0, 0, "");
            var snap = inst._learner.GetSnapshot();
            if (snap == null) return (false, 0, 0, "");
            var (buckets, samples, lastClass) = snap.HealthSummary();
            return (true, buckets, samples, lastClass);
        }

        /// <summary>
        /// Run the in-app D3D11 benchmark to seed the feedforward model.
        /// Takes ~5 minutes for 4 scenes × ~25 clock steps. Requires admin
        /// privileges and an NVIDIA dGPU. The main power loop is paused for
        /// the duration and restarted afterwards.
        ///
        /// Caller should:
        ///   1. Confirm the user wants to run calibration (modal dialog)
        ///   2. Show a CalibrationProgressForm bound to the returned progress
        ///   3. Pass the form's CancellationToken
        ///   4. Inspect CalibrationResult.Outcome and display a message
        /// </summary>
        public static async Task<CalibrationResult> RunCalibrationAsync(
            IProgress<CalibrationProgress>? progress,
            CancellationToken cancellationToken)
        {
            var inst = _instance;
            if (inst == null)
                return CalibrationResult.NotAvailable("Controller not initialized");
            if (!inst._nvmlInitialized)
                return CalibrationResult.NotAvailable("NVML not initialized");

            using var calibrator = new GpuCalibrator(inst, inst._nvmlDevice);
            return await calibrator.RunCalibrationAsync(progress, cancellationToken);
        }

        public static void Initialize(WmiBiosMonitor monitor)
        {
            var inst = new GpuPowerController(monitor);
            var old  = Interlocked.Exchange(ref _instance, inst);
            old?.Dispose();
            if (_lastTargetWatts > 0)
                inst.SetTargetPowerCore(_lastTargetWatts, 120);
        }

        public static void SetTargetPower(int watts, int maxSmiLimit = 120)
        {
            var inst = _instance;
            if (inst == null) return;
            inst.SetTargetPowerCore(watts, maxSmiLimit);
        }

        private void SetTargetPowerCore(int watts, int maxSmiLimit)
        {
            _lastTargetWatts = watts;

            if (!EnsureNvmlInitialized()) return;

            uint maxPowerMw = 0;
            int qres = NvmlNative.nvmlDeviceGetPowerManagementLimit(_nvmlDevice, out maxPowerMw);
            int maxVbiosPower = (qres == NvmlResult.Success && maxPowerMw > 0)
                ? (int)(maxPowerMw / 1000)
                : maxSmiLimit;

            if (watts <= 0 || watts >= maxVbiosPower)
            {
                _targetPowerWatts  = 0;
                _lastTargetWatts   = 0;
                if (_isRunning) Stop();
                return;
            }

            _targetPowerWatts = watts;
            _prevError        = 0;
            _filteredPower    = 0;
            _controllerPrimed = false;

            if (!_isRunning) Start();
        }

        // ============================================================
        //  Construction & NVML bootstrap
        // ============================================================

        private GpuPowerController(WmiBiosMonitor monitor)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            InitializeNvml();
            // Load model AFTER nvml so we can validate GPU name
            LoadModel();
        }

        private void LoadModel()
        {
            try
            {
                _model = PowerModelStore.Load(_gpuName, _driverVersion);
                if (_model != null)
                {
                    _learner.SetModel(_model);
                    var (buckets, samples, _) = _model.HealthSummary();
                    Logger.WriteLine($"[Adaptive] Model loaded: {buckets} buckets, {samples} samples");
                }
                else
                {
                    Logger.WriteLine("[Adaptive] No model loaded — will learn from scratch.");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[Adaptive] Failed to load model: " + ex.Message);
            }
        }

        private bool EnsureNvmlInitialized()
        {
            if (_nvmlInitialized) return true;
            lock (_nvmlInitLock)
            {
                if (_nvmlInitialized) return true;
                if (_nvmlInitAttempts >= NvmlInitMaxAttempts) return false;
                InitializeNvml();
                return _nvmlInitialized;
            }
        }

        private void InitializeNvml()
        {
            _nvmlInitAttempts++;
            try
            {
                int result = NvmlNative.nvmlInit_v2();
                if (result != NvmlResult.Success)
                {
                    Logger.WriteLine($"NVML Init Error: {result} (attempt {_nvmlInitAttempts}/{NvmlInitMaxAttempts})");
                    return;
                }

                result = NvmlNative.nvmlDeviceGetHandleByIndex_v2(0u, out _nvmlDevice);
                if (result != NvmlResult.Success)
                {
                    Logger.WriteLine($"NVML Device Handle Error: {result}");
                    _nvmlDevice = IntPtr.Zero;
                    return;
                }

                var name = new StringBuilder(64);
                if (NvmlNative.nvmlDeviceGetName(_nvmlDevice, name, (uint)name.Capacity) == NvmlResult.Success)
                {
                    _gpuName = name.ToString();
                }

                int drvMajor = 0, drvMinor = 0;
                if (NvmlNative.nvmlSystemGetDriverVersion(name, (uint)name.Capacity) == NvmlResult.Success)
                {
                    _driverVersion = name.ToString();
                }

                Logger.WriteLine($"NVML Initialized. Controlling: {_gpuName} (Driver: {_driverVersion})");

                int probe = NvmlNative.nvmlDeviceSetGpuLockedClocks(_nvmlDevice, MinClockLimit, MinClockLimit);
                if (probe == NvmlResult.Success)
                {
                    NvmlNative.nvmlDeviceResetGpuLockedClocks(_nvmlDevice);
                    _nvmlInitialized = true;
                }
                else if (probe == NvmlResult.ErrorNoPermission)
                {
                    Logger.WriteLine("NVML clock-lock requires administrator privileges.");
                }
                else
                {
                    Logger.WriteLine($"NVML clock-lock not supported on this driver (code {probe}).");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Failed to load nvml.dll: {ex.Message}");
            }
        }

        // ============================================================
        //  Loop lifecycle
        // ============================================================

        private void Start()
        {
            if (!_nvmlInitialized)
            {
                Logger.WriteLine("Cannot start GPU Power Controller: NVML not initialized.");
                return;
            }

            Interlocked.Exchange(ref _stopGuard, 0);

            _cts = new CancellationTokenSource();
            _isRunning = true;

            // FF lookup at start: use last known workload class (or Gaming as default)
            int ffClock = -1;
            if (_model != null)
            {
                var cls = _lastFfAppliedClass != WorkloadClass.Transient
                    ? _lastFfAppliedClass
                    : WorkloadClass.Gaming;
                int currentTemp = ReadTempC();
                ffClock = _model.LookupClock(cls, _targetPowerWatts, currentTemp);
                _lastFfAppliedClass = cls;
            }
            _currentMaxClock = (ffClock > 0) ? ffClock : MaxClockLimit;
            RunNativeClockLock(_currentMaxClock);

            _prevError         = 0;
            _filteredPower     = 0;
            _controllerPrimed  = false;
            _logCounter        = 0;
            _lastEcoCheckTick  = 0;
            _cachedEcoMode     = false;
            _classifier.Reset();
            _currentWorkloadClass = WorkloadClass.Transient;
            _recentPowerWindow.Clear();
            _recentTempWindow.Clear();
            _lastModelSaveTick   = Environment.TickCount64;
            _lastLearnLogTick    = 0;
            _modelDirty          = false;

            _loopTask = Task.Run(() => PowerControlLoop(_cts.Token));
            Logger.WriteLine($"GPU Power Controller Started (Target: {_targetPowerWatts}W, FF start: {_currentMaxClock} MHz, class: {_lastFfAppliedClass.ToLogString()})");
        }

        private void Stop()
        {
            if (Interlocked.CompareExchange(ref _stopGuard, 1, 0) != 0) return;
            try
            {
                _isRunning = false;
                try { _cts?.Cancel(); } catch { }
                try { _loopTask?.Wait(2000); } catch { }

                // Flush any pending model updates before resetting clocks
                FlushModelIfDirty(force: true);

                RunNativeClockLock(0);
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                Logger.WriteLine("GPU Power Controller Stopped");
            }
            finally
            {
                Interlocked.Exchange(ref _stopGuard, 0);
            }
        }

        /// <summary>
        /// Stop the main power loop. Called by GpuCalibrator before running
        /// the calibration benchmark so it has exclusive access to NVML.
        /// </summary>
        internal void StopForCalibration() => Stop();

        /// <summary>
        /// Called by GpuCalibrator after calibration completes to reload the
        /// freshly-saved model and restart the main power loop (if it was
        /// running before calibration).
        /// </summary>
        internal void RestartAfterCalibration()
        {
            // Reload model from disk (the calibrator just saved a new one)
            _model = null;
            _learner.ClearModel();
            LoadModel();

            // Restart the loop if we have a target
            if (_targetPowerWatts > 0 && _nvmlInitialized)
            {
                Start();
            }
        }

        // ============================================================
        //  Main control loop
        // ============================================================

        private async Task PowerControlLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_targetPowerWatts <= 0)
                    {
                        RunNativeClockLock(0);
                        break;
                    }

                    // Eco polling (wall-clock based)
                    long now = Environment.TickCount64;
                    if (now - _lastEcoCheckTick > EcoRecheckMs)
                    {
                        _cachedEcoMode    = HardwareControl.IsEcoMode();
                        _lastEcoCheckTick = now;
                    }
                    if (_cachedEcoMode)
                    {
                        await Task.Delay(EcoModeSkipMs, token);
                        continue;
                    }

                    // ---- Sample power (WMI, with hard timeout) ----
                    var sampleTask = _monitor.ReadSampleAsync(token);
                    var winner     = await Task.WhenAny(sampleTask, Task.Delay(SampleTimeoutMs, token));
                    if (winner != sampleTask)
                    {
                        Logger.WriteLine("[PowerControl] WMI sample read timed out");
                        await Task.Delay(LoopIntervalMs, token);
                        continue;
                    }
                    var sample = await sampleTask;
                    double rawPower = sample.GpuPowerWatts;
                    if (rawPower <= 0)
                    {
                        await Task.Delay(LoopIntervalMs, token);
                        continue;
                    }

                    // ---- Sample NVML telemetry: util, clock, temp ----
                    NvmlNative.nvmlDeviceGetUtilizationRates(_nvmlDevice, out NvmlUtilization util);
                    NvmlNative.nvmlDeviceGetClockInfo(_nvmlDevice, 0, out uint actualClockMHz);
                    NvmlNative.nvmlDeviceGetTemperature(_nvmlDevice, 0, out uint tempC);

                    // ---- Workload classification ----
                    var (rawClass, isStable) = _classifier.Classify(util.gpu, util.memory);
                    _currentWorkloadClass = rawClass;

                    // ---- EMA-filter the power ----
                    if (!_controllerPrimed)
                    {
                        _filteredPower    = rawPower;
                        _controllerPrimed = true;
                    }
                    else
                    {
                        _filteredPower = PowerEmaAlpha * rawPower
                                       + (1.0 - PowerEmaAlpha) * _filteredPower;
                    }

                    // ---- Maintain recent-power/temp windows for learning stability checks ----
                    _recentPowerWindow.Enqueue(rawPower);
                    if (_recentPowerWindow.Count > 6) _recentPowerWindow.Dequeue();
                    _recentTempWindow.Enqueue((int)tempC);
                    if (_recentTempWindow.Count > 6) _recentTempWindow.Dequeue();

                    // ---- Compute new clock via PI ----
                    double target = _targetPowerWatts;
                    double error  = target - _filteredPower;
                    double dt     = LoopIntervalMs / 1000.0;
                    int newClock  = ComputeControllerOutput(error, dt);

                    // ---- Optional: re-apply FF if workload class has shifted significantly ----
                    if (isStable && rawClass != WorkloadClass.Transient
                        && rawClass != _lastFfAppliedClass
                        && _model != null)
                    {
                        int ffClock = _model.LookupClock(rawClass, target, (int)tempC);
                        if (ffClock > 0 && Math.Abs(ffClock - _currentMaxClock) > FfReapplyClockDeltaMhz)
                        {
                            Logger.WriteLine($"[Adaptive] Workload shift {_lastFfAppliedClass.ToLogString()}→{rawClass.ToLogString()}: FF reapply {_currentMaxClock}→{ffClock} MHz");
                            _currentMaxClock   = ffClock;
                            _lastFfAppliedClass = rawClass;
                            // Reset PI state so it adapts to the new operating point
                            _prevError        = 0;
                            _controllerPrimed = false;
                            RunNativeClockLock(_currentMaxClock);
                            await Task.Delay(LoopIntervalMs, token);
                            continue;
                        }
                        _lastFfAppliedClass = rawClass;
                    }

                    // ---- Apply PI output ----
                    if (newClock != _currentMaxClock)
                    {
                        Logger.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "[PowerControl] {0:F1}W (raw {1:F1}W) / {2}W [{3}] -> {4} MHz",
                            _filteredPower, rawPower, target, rawClass.ToLogString(), newClock));
                        _currentMaxClock = newClock;
                        RunNativeClockLock(_currentMaxClock);
                    }
                    else if (_logCounter++ % 10 == 0)
                    {
                        Logger.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "[PowerControl] {0:F1}W (raw {1:F1}W) / {2}W [{3}], hold @ {4} MHz, {5}°C",
                            _filteredPower, rawPower, target, rawClass.ToLogString(), _currentMaxClock, tempC));
                    }

                    // ---- Adaptive learning (only when stable & in deadband) ----
                    if (isStable && rawClass != WorkloadClass.Transient && _learner.HasModel)
                    {
                        var outcome = _learner.TryLearn(
                            workloadClass:        rawClass,
                            observedClockMHz:     (int)actualClockMHz,
                            observedPowerWatts:   rawPower,
                            observedTempC:        (int)tempC,
                            targetWatts:          (int)target,
                            recentPowerSamples:   _recentPowerWindow.ToList(),
                            recentTempSamples:    _recentTempWindow.ToList(),
                            ecoModeActive:        false);

                        if (outcome == LearningOutcome.Learned)
                        {
                            _modelDirty = true;
                            // Periodic learn-log so user can see it's working
                            if (now - _lastLearnLogTick > LearnLogIntervalMs)
                            {
                                _lastLearnLogTick = now;
                                var snap = _learner.GetSnapshot();
                                if (snap != null)
                                {
                                    var (buckets, samples, _) = snap.HealthSummary();
                                    Logger.WriteLine($"[Adaptive] Learned: {rawClass.ToLogString()} @ {actualClockMHz}MHz = {rawPower:F1}W (total: {buckets} buckets, {samples} samples)");
                                }
                            }
                        }
                    }

                    // ---- Periodic model save ----
                    if (_modelDirty && (now - _lastModelSaveTick > SaveIntervalMs))
                    {
                        FlushModelIfDirty(force: false);
                        _lastModelSaveTick = now;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.WriteLine("Power Control Loop Error: " + ex.Message);
                }

                await Task.Delay(LoopIntervalMs, token);
            }
        }

        private void FlushModelIfDirty(bool force)
        {
            if (!_modelDirty && !force) return;
            var snap = _learner.GetSnapshot();
            if (snap == null) return;
            try
            {
                PowerModelStore.Save(snap);
                _modelDirty = false;
                if (force)
                    Logger.WriteLine("[Adaptive] Model flushed to disk (forced)");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[Adaptive] Model save failed: " + ex.Message);
            }
        }

        private int ReadTempC()
        {
            try
            {
                if (NvmlNative.nvmlDeviceGetTemperature(_nvmlDevice, 0, out uint t) == NvmlResult.Success)
                    return (int)t;
            }
            catch { }
            return 50;
        }

        private int ComputeControllerOutput(double error, double dt)
        {
            if (Math.Abs(error) <= DeadbandWatts)
            {
                _prevError = error;
                return _currentMaxClock;
            }

            double kp = error < 0 ? KpDown : KpUp;
            double pTerm = kp * (error - _prevError);
            double iTerm = Ki * error * dt;

            double delta = pTerm + iTerm;
            _prevError   = error;

            int newClock = _currentMaxClock + (int)Math.Round(delta);
            return Math.Clamp(newClock, MinClockLimit, MaxClockLimit);
        }

        private void RunNativeClockLock(int maxClockMhz)
        {
            if (!_nvmlInitialized || _nvmlDevice == IntPtr.Zero) return;

            try
            {
                int result = (maxClockMhz <= 0)
                    ? NvmlNative.nvmlDeviceResetGpuLockedClocks(_nvmlDevice)
                    : NvmlNative.nvmlDeviceSetGpuLockedClocks(_nvmlDevice, MinClockLimit, (uint)maxClockMhz);

                if (result != NvmlResult.Success && result != NvmlResult.ErrorNoPermission)
                {
                    Logger.WriteLine($"NVML Clock Limit Error Code: {result}");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Native Clock Lock Error: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                Stop();

            if (_nvmlInitialized)
            {
                try { NvmlNative.nvmlShutdown(); } catch { }
                _nvmlInitialized = false;
            }
        }

        ~GpuPowerController() => Dispose(false);
    }

    // ============================================================
    //  NVML constants + P/Invoke
    // ============================================================

    internal static class NvmlResult
    {
        public const int Success                  = 0;
        public const int ErrorUninitialized       = 1;
        public const int ErrorInvalidArgument     = 2;
        public const int ErrorNotSupported        = 3;
        public const int ErrorNoPermission        = 4;
        public const int ErrorAlreadyInitialized  = 5;
        public const int ErrorNotFound            = 6;
        public const int ErrorInsufficientSize    = 7;
        public const int ErrorInsufficientPower   = 8;
        public const int ErrorDriverNotLoaded     = 9;
        public const int ErrorTimeout             = 10;
        public const int ErrorIrIssue             = 11;
        public const int ErrorLibraryNotFound     = 12;
        public const int ErrorFunctionNotFound    = 13;
        public const int ErrorInUse               = 19;
        public const int ErrorUnknown             = 999;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvmlUtilization
    {
        public uint gpu;
        public uint memory;
    }

    internal static class NvmlNative
    {
        private const string DllName = "nvml.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlInit_v2();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetGpuLockedClocks(IntPtr device, out uint minGpuClockMHz, out uint maxGpuClockMHz);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minGpuClockMHz, uint maxGpuClockMHz);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceResetGpuLockedClocks(IntPtr device);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limit);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint powerMw);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetClockInfo(IntPtr device, uint clockType, out uint clockMHz);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, uint clockType, out uint clockMHz);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint tempC);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nvmlShutdown();
    }
}
