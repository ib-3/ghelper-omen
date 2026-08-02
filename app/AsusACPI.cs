using GHelper;
using GHelper.USB;
using OmenCore.Hardware;
using OmenCore.Models;
using OmenCore.Services;
using GHelper.Helpers;
using PawnIO;
using System.Management;
using System.Runtime.InteropServices;

public enum AsusFan
{
    CPU = 0,
    GPU = 1,
    Mid = 2,
    XGM = 3
}

internal sealed class OmenBackend : IDisposable
{
    // ====================================================================================================
    // [TEMPERATURE FIX 2026-05-29] 
    // Integrated WmiBiosMonitor for high-precision CPU/GPU temperatures with freeze-detection and fallbacks.
    // Legacy BIOS-only code is preserved in comments for easy reversal.
    // ====================================================================================================

    private readonly LoggingService _logging;
    // Nullable: HpWmiBios construction can fail (e.g. CIM access denied) and TryCreate
    // defends in depth — accesses must be null-safe.
    private readonly HpWmiBios? _bios;
    // Nullable: both WMI and EC fan controllers can fail to initialize.
    private readonly IFanController? _fans;
    private readonly WmiBiosMonitor? _monitor; //  High-precision monitor
    private readonly IMsrAccess? _msrAccess;   // PawnIO MSR — null on Ryzen or if PawnIO not installed
    // PowerLimitController removed
    private OmenCore.Hardware.IMmioAccess? _mmioAccess;
    private OmenCore.Hardware.MmioPowerLimitProvider? _mmioLimits;
    private readonly byte[][] _curves = new byte[2][];
    private int? _targetCpuPl1;
    private int? _targetCpuPl2;
    private int _lastEvaluatedCpuPercent = -1;
    private int _lastEvaluatedGpuPercent = -1;

    public WmiBiosMonitor? Monitor => _monitor;

    //  Constructor now accepts WmiBiosMonitor + msrAccess
    private OmenBackend(LoggingService logging, HpWmiBios? bios, IFanController? fans, WmiBiosMonitor? monitor, IMsrAccess? msrAccess)
    {
        _logging = logging;
        _bios = bios;
        _fans = fans;
        _monitor = monitor;
        _msrAccess = msrAccess;
        // _powerLimitController removed
        _curves[(int)AsusFan.CPU] = new byte[16];
        _curves[(int)AsusFan.GPU] = new byte[16];

        // Read CPU's actual max power from MSR 0x614 or MMIO for the slider max (Intel only)
        if (msrAccess?.IsAvailable == true && !PawnIO.CpuInfo.IsAMD)
        {
            int maxPower = -1;
            bool fromMmio = false;

            try
            {
                var mmioAccess = new OmenCore.Hardware.PawnIOMmioAccess((OmenCore.Hardware.PawnIOMsrAccess)msrAccess);
                if (mmioAccess.IsAvailable)
                {
                    var mmioLimits = new OmenCore.Hardware.MmioPowerLimitProvider(mmioAccess);
                    if (mmioLimits.IsAvailable)
                    {
                        maxPower = mmioLimits.ReadMaxPowerWatts();
                        var limits = mmioLimits.GetPowerLimits();
                        if (limits.Pl2Watts > maxPower && limits.Pl2Watts < 500)
                            maxPower = (int)Math.Round(limits.Pl2Watts);
                        fromMmio = true;
                    }
                }
            }
            catch { }

            if (maxPower <= 0)
                maxPower = msrAccess.ReadMaxPowerWatts();

            if (maxPower > 0 && maxPower < 500)
            {
                bool isLaptop = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
                int maxAllowed = isLaptop ? 127 : 500;
                AsusACPI.MaxTotal = Math.Min(Math.Max(maxPower, 127), maxAllowed);
                Logger.WriteLine($"[OmenBackend] MaxTotal set to {AsusACPI.MaxTotal}W (detected {maxPower}W from {(fromMmio ? "MMIO" : "MSR")}, isLaptop: {isLaptop})");
            }
        }
    }

    /* [LEGACY CONSTRUCTOR]
    private OmenBackend(LoggingService logging, HpWmiBios bios, IFanController fans, IMsrAccess? msrAccess)
    {
        _logging = logging;
        _bios = bios;
        _fans = fans;
        _msrAccess = msrAccess;
        _curves[(int)AsusFan.CPU] = DefaultCurve(AsusFan.CPU);
        _curves[(int)AsusFan.GPU] = DefaultCurve(AsusFan.GPU);
    }
    */

    public bool IsAvailable => (_bios?.IsAvailable ?? false) || (_fans?.IsAvailable ?? false);
    public bool IsOmenV2() => _bios != null && _bios.ThermalPolicy >= OmenCore.Hardware.HpWmiBios.ThermalPolicyVersion.V2;
    private bool HasCpuPowerLimitControl
    {
        get
        {
            if (CpuInfo.IsAMD)
                return GHelper.Mode.ModeControl.IsPawnInstalled();
            
            return _msrAccess?.IsAvailable == true || (_bios?.IsAvailable ?? false);
        }
    }

    // ── Lighting ──────────────────────────────────────────────────────────
    private OmenCore.Hardware.OmenLightingService? _lightingService;

    /// <summary>
    /// Lighting service — lazily probed the first time this is accessed.
    /// Returns null if WMI BIOS is not available.
    /// </summary>
    public OmenCore.Hardware.OmenLightingService? GetLightingService()
    {
        if (_bios == null || !_bios.IsAvailable) return null;
        if (_lightingService == null)
        {
            _lightingService = new OmenCore.Hardware.OmenLightingService(_bios, _logging);
            _lightingService.Probe();
        }
        return _lightingService;
    }

    public static OmenBackend? TryCreate()
    {
        if (!IsHpOmenSystem()) return null;

        LoggingService? logging = null;
        HpWmiBios? bios = null;
        IFanController? fans = null;
        NvapiService? nvapi = null;
        WmiBiosMonitor? monitor = null;
        IMsrAccess? msrAccess = null;

        try
        {
            logging = new LoggingService();
            logging.Initialize();
            logging.LogEmitted += Logger.WriteLine;

            try
            {
                bios = new HpWmiBios(logging);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"OMEN backend: HpWmiBios construction threw: {ex.GetType().Name}: {ex.Message}");
                bios = null;
            }

            try
            {
                int maxFanOverride = AppConfig.Get("omen_max_fan_level", 0);
                
                var wmiController = new OmenCore.Hardware.WmiFanController(null, logging, maxFanOverride, injectedWmiBios: bios);
                var wmiFans = new OmenCore.Hardware.WmiFanControllerWrapper(wmiController, logging);

                var ecAccess = OmenCore.Hardware.EcAccessFactory.GetEcAccess();
                if (ecAccess != null && ecAccess.IsAvailable && bios.ThermalPolicy < OmenCore.Hardware.HpWmiBios.ThermalPolicyVersion.V2)
                {
                    var registerMap = new System.Collections.Generic.Dictionary<string, int>(); 
                    var baseController = new OmenCore.Hardware.FanController(ecAccess, registerMap, null, logging, bios);
                    if (baseController.IsEcReady)
                    {
                        fans = new OmenCore.Hardware.EcFanControllerWrapper(baseController, null, logging);
                        Logger.WriteLine($"OMEN backend: Using EC Fan Controller for true hardware RPM (V0/V1).");
                    }
                    else
                    {
                        fans = wmiFans;
                        Logger.WriteLine($"OMEN backend: Using WMI Fan Controller (EC init failed).");
                    }
                }
                else
                {
                    fans = wmiFans;
                    string reason = (bios.ThermalPolicy >= OmenCore.Hardware.HpWmiBios.ThermalPolicyVersion.V2) ? "ThermalPolicy V2+" : "EC not available";
                    Logger.WriteLine($"OMEN backend: Using WMI Fan Controller ({reason}).");
                }
                
                Program.OmenFans = fans;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"OMEN backend: Fan controller init failed: {ex.GetType().Name}: {ex.Message}");
                Logger.WriteLine($"OMEN backend: Fan controller stack: {ex.StackTrace}");
                fans = null;
            }

            try
            {
                nvapi = new NvapiService(logging);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"OMEN backend: NvapiService init failed: {ex.GetType().Name}: {ex.Message}");
                nvapi = null;
            }

            try
            {
                if (bios != null && bios.ThermalPolicy >= OmenCore.Hardware.HpWmiBios.ThermalPolicyVersion.V2)
                {
                    //  Provide logging and nvapi instances
                    monitor = new WmiBiosMonitor(logging, nvapi);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"OMEN backend: WmiBiosMonitor init failed: {ex.GetType().Name}: {ex.Message}");
                Logger.WriteLine($"OMEN backend: WmiBiosMonitor stack: {ex.StackTrace}");
                monitor = null;
            }

            // MSR access for Intel CPU power limits (PawnIO). Not used on AMD.
            if (!PawnIO.CpuInfo.IsAMD)
            {
                try
                {
                    msrAccess = MsrAccessFactory.Create(logging);
                    if (msrAccess?.IsAvailable == true)
                        Logger.WriteLine("OMEN backend: MSR access available (PawnIO) — Intel power limits enabled.");
                    else
                        Logger.WriteLine("OMEN backend: MSR access unavailable (PawnIO not loaded?) — Intel power limits disabled.");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"OMEN backend: MSR access init failed: {ex.GetType().Name}: {ex.Message}");
                    msrAccess = null;
                }
            }

            Logger.WriteLine($"OMEN backend: BIOS={bios?.Status ?? "null"}, Fans={fans?.Status ?? "null"}, Monitor={monitor?.MonitoringSource ?? "null"}");

            //  Return backend with monitor + MSR access
            return new OmenBackend(logging, bios, fans, monitor, msrAccess);

            /* [LEGACY TRYCREATE RETURN]
            Logger.WriteLine($"OMEN backend: BIOS={bios?.Status ?? "null"}, Fans={fans?.Status ?? "null"}");
            return new OmenBackend(logging, bios, fans, null);
            */
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"OMEN backend init failed: {ex.GetType().Name}: {ex.Message}");
            Logger.WriteLine($"OMEN backend init stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Logger.WriteLine($"OMEN backend init inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            return null;
        }
    }

    public bool HasPerKeyRgb()
    {
        return _bios?.GetKeyboardType() == OmenCore.Hardware.HpWmiBios.KbdType.PerKeyRgb;
    }

    public void SetColor(System.Drawing.Color color)
    {
        // For 4-zone we use SetColorTable with 128-byte array
        byte[] colors = new byte[128];
        colors[0] = 4; // number of zones is usually at index 0 for 4-zone (or wait, let's use the helper if exists)
        
        // Let's just use the helper: SetColorTable(byte[] zoneColors, bool ensureBacklightOn = true)
        // HpWmiBios.SetColorTable expects just the RGB triplets for each zone.
        byte[] zoneColors = new byte[12]; // 4 zones * 3 bytes (R,G,B)
        for (int i = 0; i < 4; i++)
        {
            zoneColors[i * 3] = color.R;
            zoneColors[i * 3 + 1] = color.G;
            zoneColors[i * 3 + 2] = color.B;
        }
        _bios?.SetColorTable(zoneColors);
    }

    public bool TryIsSupported(uint deviceId, out bool supported)
    {
        supported = deviceId switch
        {
            AsusACPI.DevsCPUFanCurve or AsusACPI.DevsGPUFanCurve => IsAvailable,
            AsusACPI.DevsCPUFan or AsusACPI.DevsGPUFan => IsAvailable,
            AsusACPI.CPU_Fan or AsusACPI.GPU_Fan => IsAvailable,
            AsusACPI.PerformanceMode => IsAvailable,
            AsusACPI.BatteryLimit => _bios?.IsAvailable ?? false,
            
            //  Temps now supported as long as either BIOS or Monitor is available
            AsusACPI.Temp_CPU or AsusACPI.Temp_GPU => IsAvailable,
            /* LEGACY SUPPORT 
            AsusACPI.Temp_CPU or AsusACPI.Temp_GPU => _bios?.IsAvailable ?? false, 
            */
            
            AsusACPI.GPUEcoROG or AsusACPI.GPUEcoVivo => _bios?.IsAvailable ?? false, // OMEN has Optimus (iGPU-only) mode
            AsusACPI.GPUMuxROG or AsusACPI.GPUMuxVivo => _bios?.IsAvailable ?? false, // Hybrid/Discrete MUX
            AsusACPI.GPU_POWER => _bios?.IsAvailable ?? false,
            AsusACPI.PPT_GPUC0 => _bios?.IsAvailable ?? false, // Dynamic Boost via WMI Concurrent TDP
            AsusACPI.PPT_GPUC2 => _bios?.IsAvailable ?? false, // GPU Temp Target
            AsusACPI.PPT_APUA3 or AsusACPI.PPT_APUA0 or AsusACPI.PPT_APUC1 => HasCpuPowerLimitControl,
            _ => false
        };

        return supported;
    }

    public bool TryDeviceSet(uint deviceId, int status, string? logName, out int result)
    {
        result = -1;

        if (deviceId == AsusACPI.PerformanceMode)
        {
            string mode = status switch
            {
                AsusACPI.PerformanceTurbo => AppConfig.Is("omen_turbo_is_max") ? "Max" : "Performance",
                AsusACPI.PerformanceSilent => "Quiet",
                _ => "Balanced"
            };

            // PowerLimitController logic removed
            if (!AppConfig.IsApplyFans())
            {
                // Also set the appropriate base performance mode
                result = (_fans?.SetPerformanceMode(mode) ?? false) ? 1 : -1;
                
                if (mode == "Max")
                {
                    _fans?.ApplyMaxCooling();
                }
                
                Logger.WriteLine($"{logName ?? "OmenMode"} = {mode} : OK (BIOS Native Curve)");
            }
            else
            {
                // Decouple fan mode from performance mode (like omencore)
                // This prevents the BIOS/EC from overriding custom fan curves with static values
                if (mode == "Max")
                {
                    _fans?.SetPerformanceMode("Performance");
                    _fans?.ApplyMaxCooling();
                }
                
                result = 1;
                Logger.WriteLine($"{logName ?? "OmenMode"} = {mode} : OK (Fan curve unlinked)");
            }
            return true;
        }

        if (deviceId == AsusACPI.BatteryLimit)
        {
            result = (_bios?.SetBatteryCareMode(status < 100) ?? false) ? 1 : -1;
            Logger.WriteLine($"{logName ?? "OmenBatteryLimit"} = {(status < 100 ? "80%" : "100%")} : {(result == 1 ? "OK" : result)}");
            return true;
        }

        // GPU MUX: 0 = Discrete (Ultimate), 1 = Hybrid (Standard)
        if (deviceId == AsusACPI.GPUMuxROG || deviceId == AsusACPI.GPUMuxVivo)
        {
            var targetMode = status == 0 ? HpWmiBios.GpuMode.Discrete : HpWmiBios.GpuMode.Hybrid;
            result = _bios?.SetGpuMode(targetMode) == true ? 1 : -1;
            Logger.WriteLine($"{logName ?? "OmenGpuMux"} = {targetMode} : {(result == 1 ? "OK" : result)}");
            ApplyGpuPower(forceDState: status == 0 ? 0 : 1);
            return true;
        }

        if (deviceId == AsusACPI.GPU_POWER || deviceId == AsusACPI.PPT_GPUC0 || deviceId == AsusACPI.PPT_GPUC2)
        {
            ApplyGpuPower();
            
            result = 1;
            return true;
        }

        if (deviceId == AsusACPI.PPT_APUA3 || deviceId == AsusACPI.PPT_APUA0 || deviceId == AsusACPI.PPT_APUC1)
        {
            result = SetCpuPowerLimit(deviceId, status) ? 1 : -1;
            Logger.WriteLine($"{logName ?? "OmenPowerLimit"} = {status}W : {(result == 1 ? "OK" : result)}");
            return true;
        }

        return false;
    }

    public void ApplyGpuPower(int forceDState = -1)
    {
        if (_bios == null || !_bios.IsAvailable) return;
        int dynamicBoost = AppConfig.GetMode("gpu_boost");

        if (AppConfig.IsOmen())
        {
            if (AppConfig.GetMode("omen_unleashed") == 1)
            {
                dynamicBoost = 255;
            }
            else if (!AppConfig.Is("dev_mode"))
            {
                dynamicBoost = 0;
            }
        }

        int powerTarget = AppConfig.GetMode("gpu_power");
        int tempTarget = AppConfig.GetMode("gpu_temp");
        
        int currentGpuMode = AppConfig.Get("gpu_mode");
        int currentPerfMode = AppConfig.Get("performance_mode");
        
        int dState = forceDState;
        if (dState < 0)
        {
            // Map D-State natively to Performance Mode, just like OGH does.
            // 2 = Turbo (D0), 1 = Balanced (D1), 0 = Silent (D2/3)
            dState = currentPerfMode == 2 ? 0 : (currentPerfMode == 1 ? 1 : 3);
        }

        if (tempTarget < 70) tempTarget = 87; // default temp limit

        // Eco forces to 30w
        if (currentGpuMode == AsusACPI.GPUModeEco || dState == 3)
        {
            powerTarget = 30;
            dynamicBoost = 0;
        }
        // Balanced unlocks, but ultimate mode same as balanced just with dynamic boost enabled
        else if (currentGpuMode == AsusACPI.GPUModeStandard || dState == 1)
        {
            dynamicBoost = 0;
        }

        HpWmiBios.GpuPowerLevel level = HpWmiBios.GpuPowerLevel.Medium;
        if (dynamicBoost > 0) 
            level = HpWmiBios.GpuPowerLevel.Maximum; // Enable PPAB (Dynamic Boost)
        else if (powerTarget > 0 && powerTarget <= 30) 
            level = HpWmiBios.GpuPowerLevel.Minimum; // Standard TGP

        _bios.SetGpuPower(level, tempTarget, dState);
        
        // Do not send the Concurrent TDP (TPP offset) in Eco Mode
        if (dState != 3) 
        {
            _bios.SetConcurrentTdp(Math.Max(0, dynamicBoost));
        }
        
        Logger.WriteLine($"OmenGpuPowerAuto Level:{level} Boost:{dynamicBoost}W TGP:{powerTarget}W Temp:{tempTarget}C DState:{dState}");
    }

    public bool TryDeviceSet(uint deviceId, byte[] parameters, string? logName, out int result)
    {
        result = -1;
        return false;
    }

    /// <summary>
    /// Called from AsusACPI.SetGPUEco — bypasses the ASUS eco-flag roundtrip.
    /// eco=0 → Hybrid (Standard), eco=1 → Optimus/iGPU-only (Eco).
    /// </summary>
    public bool TrySetGpuEco(int eco, out int result)
    {
        result = -1;
        if (_bios == null || !_bios.IsAvailable) return false;
        // eco=1 → iGPU-only (Optimus), eco=0 → Hybrid (both GPUs)
        var targetMode = eco == 1 ? HpWmiBios.GpuMode.Optimus : HpWmiBios.GpuMode.Hybrid;
        result = _bios.SetGpuMode(targetMode) ? 1 : -1;
        Logger.WriteLine($"OmenGpuEco eco={eco} → {targetMode} : {(result == 1 ? "OK" : result)}");
        
        ApplyGpuPower(forceDState: eco == 1 ? 3 : 1);
        return true;
    }

    private static OmenCore.Hardware.RyzenTemperatureProvider? _ryzenTempProvider = null;

    public bool TryDeviceGet(uint deviceId, out int result)
    {
        //  Temperature interception logic using high-precision monitor
        if (deviceId == AsusACPI.Temp_CPU || deviceId == AsusACPI.Temp_GPU)
        {
            // Skip NVAPI GPU queries if in Eco mode, fall through to WMI BIOS fallback
            if (deviceId == AsusACPI.Temp_GPU && HardwareControl.IsEcoMode())
            {
                // Fall through to WMI BIOS fallback which doesn't wake the dGPU
            }
            else
            {
                try
                {
                    if (_monitor == null)
                    {
                        Logger.WriteLine("[OmenBackend.TryDeviceGet] Temp requested but WmiBiosMonitor is null — falling back to BIOS temp.");
                    }
                    else
                    {
                        // Use the high-precision monitor which handles ACPI thermal zones and LHM fallbacks
                        var sample = Task.Run(() => _monitor.ReadSampleAsync(default)).GetAwaiter().GetResult();
                        double temp = (deviceId == AsusACPI.Temp_CPU) ? sample.CpuTemperatureC : sample.GpuTemperatureC;
                        if (temp > 0)
                        {
                            result = (int)Math.Round(temp);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"[OmenBackend.TryDeviceGet] Temp fetch failed: {ex.Message}");
                }
            }
        }

        result = deviceId switch
        {
            AsusACPI.PerformanceMode => 0,
            AsusACPI.BatteryLimit => (_bios?.GetBatteryCareMode() ?? false) ? 80 : 100,
            AsusACPI.CPU_Fan => ReadFanRpm(cpu: true),
            AsusACPI.GPU_Fan => ReadFanRpm(cpu: false),
            AsusACPI.DevsCPUFanCurve or AsusACPI.DevsGPUFanCurve => IsAvailable ? 1 : -1,
            AsusACPI.DevsCPUFan or AsusACPI.DevsGPUFan => IsAvailable ? 1 : -1,
            
            //  High-precision path above intercepting; switched to legacy fallbacks here
            AsusACPI.Temp_CPU => -1, // Force HardwareControl.cs to use the PerfCounter fallback (TSZ0) instead of broken HP WMI
            AsusACPI.Temp_GPU => (int)(_bios?.GetGpuTemperature() ?? -1),
            /* LEGACY GET
            AsusACPI.Temp_CPU => (int)(_bios?.GetTemperature() ?? -1),
            AsusACPI.Temp_GPU => (int)(_bios?.GetGpuTemperature() ?? -1),
            */

            // GPUEco: 1 = currently in Optimus/iGPU-only mode, 0 = not in eco mode
            AsusACPI.GPUEcoROG or AsusACPI.GPUEcoVivo => ReadGpuEcoFlag(),
            // GPUMux: Discrete=0 (Ultimate), Hybrid=1 (Standard)
            AsusACPI.GPUMuxROG or AsusACPI.GPUMuxVivo => ReadGpuMuxFlag(),
            AsusACPI.GPU_POWER => ReadGpuPowerFlag(),
            AsusACPI.PPT_GPUC2 => AppConfig.GetMode("gpu_temp"),
            AsusACPI.PPT_APUA3 => ReadCpuPowerLimit(false),
            AsusACPI.PPT_APUA0 or AsusACPI.PPT_APUC1 => ReadCpuPowerLimit(true),
            _ => int.MinValue
        };

        return result != int.MinValue;
    }

    private int ReadGpuEcoFlag()
    {
        try
        {
            var mode = _bios?.GetGpuMode();
            if (mode.HasValue)
                return mode.Value == HpWmiBios.GpuMode.Optimus ? 1 : 0;
        }
        catch { }
        return AppConfig.Get("gpu_mode") == AsusACPI.GPUModeEco ? 1 : 0;
    }

    private int ReadGpuMuxFlag()
    {
        try
        {
            var mode = _bios?.GetGpuMode();
            if (mode.HasValue)
                return mode.Value == HpWmiBios.GpuMode.Discrete ? 0 : 1;
        }
        catch { }
        return AppConfig.Get("gpu_mode") == AsusACPI.GPUModeUltimate ? 0 : 1;
    }

    private int ReadGpuPowerFlag()
    {
        try
        {
            var power = _bios?.GetGpuPower();
            if (power.HasValue)
            {
                // Map back to a UI wattage slider approximation
                if (power.Value.customTgp && power.Value.ppab) return 25; // Maximum
                if (power.Value.customTgp) return 15; // Medium
                return 5; // Minimum
            }
        }
        catch { }
        return 15;
    }

    private OmenCore.Hardware.AmdUndervoltProvider? _amdPowerProvider;
    private bool _amdSmuWriteFailLogged = false;

    private bool SetCpuPowerLimit(uint deviceId, int watts)
    {
        if (!HasCpuPowerLimitControl)
        {
            Logger.WriteLine("OmenPowerLimit: No backend available");
            return false;
        }

        if (deviceId == AsusACPI.PPT_APUA3)
            _targetCpuPl1 = watts;
        else if (deviceId == AsusACPI.PPT_APUA0)
            _targetCpuPl2 = watts;

        int pl1 = Math.Clamp(_targetCpuPl1 ?? AsusACPI.DefaultTotal, AsusACPI.MinTotal, AsusACPI.MaxTotal);
        int pl2 = Math.Clamp(_targetCpuPl2 ?? pl1, AsusACPI.MinTotal, 200);
        if (pl2 < pl1) pl2 = pl1;

        if (CpuInfo.IsAMD)
        {
            bool wmiSuccess = false;
            
            // 1. Preferred method: WMI BIOS
            if (_bios != null)
            {
                wmiSuccess = _bios.SetCpuPowerLimit(pl1, pl2);
            }

            // 2. Hardware-level method: SMU (RyzenAdj equivalent)
            if (_amdPowerProvider == null)
            {
                try
                {
                    _amdPowerProvider = new OmenCore.Hardware.AmdUndervoltProvider();
                }
                catch { Logger.WriteLine("OmenPowerLimit: Failed to init AmdUndervoltProvider, falling back."); }
            }

            if (_amdPowerProvider != null)
            {
                uint valueMw = (uint)(watts * 1000);
                OmenCore.Hardware.RyzenSmu.SmuStatus status = OmenCore.Hardware.RyzenSmu.SmuStatus.Failed;

                if (deviceId == AsusACPI.PPT_APUA3) // SPL
                    status = _amdPowerProvider.SetStapmLimit(valueMw);
                else if (deviceId == AsusACPI.PPT_APUA0) // sPPT
                    status = _amdPowerProvider.SetSlowPptLimit(valueMw);
                else if (deviceId == AsusACPI.PPT_APUC1) // fPPT
                    status = _amdPowerProvider.SetFastPptLimit(valueMw);

                bool smuSuccess = status == OmenCore.Hardware.RyzenSmu.SmuStatus.Ok;
                if (!smuSuccess)
                {
                    if (!_amdSmuWriteFailLogged)
                    {
                        Logger.WriteLine($"OmenPowerLimit: AMD SMU write FAILED ({status}) — {watts}W");
                        _amdSmuWriteFailLogged = true;
                    }
                }
                else
                {
                    if (_amdSmuWriteFailLogged)
                    {
                        _amdSmuWriteFailLogged = false;
                    }
                    Logger.WriteLine($"OmenPowerLimit: AMD SMU write OK — {watts}W");
                }

                // Always return here on AMD: the MSR path below is Intel-only
                return wmiSuccess || smuSuccess;
            }
            
            return wmiSuccess;
        }

        bool anySuccess = false;

        // Path 1: PawnIO MSR write (PL1 + PL2 via MSR 0x610)
        if (_msrAccess != null)
        {
            var status = _msrAccess.GetPowerLimitStatus();
            int currentPl1 = status.Pl1Watts > 0 ? (int)Math.Round(status.Pl1Watts) : AsusACPI.DefaultTotal;
            int currentPl2 = status.Pl2Watts > 0 ? (int)Math.Round(status.Pl2Watts) : Math.Max(currentPl1, AsusACPI.DefaultTotal);

            int msrPl1 = Math.Clamp(_targetCpuPl1 ?? currentPl1, AsusACPI.MinTotal, AsusACPI.MaxTotal);
            int msrPl2 = Math.Clamp(_targetCpuPl2 ?? currentPl2, AsusACPI.MinTotal, 200);
            if (msrPl2 < msrPl1) msrPl2 = msrPl1;

            bool msrSuccess = _msrAccess.SetPowerLimits(msrPl1, msrPl2);

            if (msrSuccess)
                Logger.WriteLine($"OmenPowerLimit: MSR write OK — PL1={msrPl1}W, PL2={msrPl2}W");
            else
                Logger.WriteLine("OmenPowerLimit: MSR write failed/unverified. Trying MMIO fallback...");

            anySuccess |= msrSuccess;

            // MMIO sync (write directly to MCHBAR+0x59A0)
            // as MMIO overrides MSR on Meteor Lake and newer platforms.
            if (_mmioLimits == null)
            {
                if (_mmioAccess == null)
                {
                    _mmioAccess = new OmenCore.Hardware.PawnIOMmioAccess((OmenCore.Hardware.PawnIOMsrAccess)_msrAccess);
                    if (!_mmioAccess.IsAvailable)
                    {
                        Logger.WriteLine("OmenPowerLimit: PawnIO MMIO fallback unavailable.");
                        _mmioAccess = null;
                    }
                }
                if (_mmioAccess != null)
                    _mmioLimits = new OmenCore.Hardware.MmioPowerLimitProvider(_mmioAccess);
            }

            bool mmioSuccess = false;
            if (_mmioLimits != null && _mmioLimits.IsAvailable && _mmioLimits.CanWriteLimits)
            {
                mmioSuccess = _mmioLimits.SetPowerLimits(msrPl1, pl2);
                Logger.WriteLine($"OmenPowerLimit: MMIO write {(mmioSuccess ? "OK" : "FAILED")} — PL1={msrPl1}W, PL2={pl2}W");
            }
            else if (_mmioLimits != null)
            {
                Logger.WriteLine("OmenPowerLimit: MMIO fallback not available or read-only.");
            }

            anySuccess |= mmioSuccess;
        }

        // Path 2: WMI BIOS fallback (PL1 only — no PL2 via WMI)
        if (_bios?.IsAvailable == true)
        {
            bool wmiSuccess = _bios.SetCpuPowerLimit(pl1);
            if (wmiSuccess)
                Logger.WriteLine($"OmenPowerLimit: WMI write OK — PL1={pl1}W");
            else
                Logger.WriteLine("OmenPowerLimit: WMI write failed/unverified.");
            anySuccess |= wmiSuccess;
        }

        return anySuccess;
    }

    public double GetCpuPackagePowerWatts()
    {
        double power = 0.0;

        // Primary: high-precision monitor (WmiBiosMonitor). May be null if init failed.
        if (_monitor != null)
        {
            try
            {
                var sample = Task.Run(() => _monitor.ReadSampleAsync(default)).GetAwaiter().GetResult();
                power = sample.CpuPowerWatts;
            }
            catch { }
        }

        if (power > 0.0)
            return power;

        // Fallback to MSR
        power = _msrAccess?.ReadCpuPackagePowerWatts() ?? 0.0;

        if (power <= 0.0)
        {
            if (_mmioLimits == null)
            {
                if (_mmioAccess == null)
                {
                    if (_msrAccess == null) return 0;
                    _mmioAccess = new OmenCore.Hardware.PawnIOMmioAccess((OmenCore.Hardware.PawnIOMsrAccess)_msrAccess);
                    if (!_mmioAccess.IsAvailable) { _mmioAccess = null; return 0; }
                }
                _mmioLimits = new OmenCore.Hardware.MmioPowerLimitProvider(_mmioAccess);
                Logger.WriteLine($"[TelemetryTrace] Initialized MMIO Provider: Available={_mmioLimits.IsAvailable}");
            }

            power = _mmioLimits.ReadCpuPackagePowerWatts();
        }

        return power;
    }

    private int ReadCpuPowerLimit(bool pl2)
    {
        if (!HasCpuPowerLimitControl || _msrAccess == null)
            return -1;

        var status = _msrAccess.GetPowerLimitStatus();
        double watts = pl2 ? status.Pl2Watts : status.Pl1Watts;
        if (watts <= 0) return -1;
        return (int)Math.Round(watts);
    }

    public bool TryDeviceGetBuffer(uint deviceId, uint status, out byte[]? result)
    {
        result = deviceId switch
        {
            AsusACPI.DevsCPUFanCurve => _curves[(int)AsusFan.CPU].ToArray(),
            AsusACPI.DevsGPUFanCurve => _curves[(int)AsusFan.GPU].ToArray(),
            _ => null
        };

        return result != null;
    }

    public bool TryGetBatteryDischarge(out decimal? discharge)
    {
        discharge = null;
        return false;
    }

    public bool TryGetFan(AsusFan device, out int fan)
    {
        fan = device switch
        {
            AsusFan.CPU => ReadFanRpm(cpu: true),
            AsusFan.GPU => ReadFanRpm(cpu: false),
            _ => -1
        };

        return device is AsusFan.CPU or AsusFan.GPU;
    }

    public void RestoreAutoControl()
    {
        _fans?.RestoreAutoControl();
    }

    public bool TryGetFanCurve(AsusFan device, int mode, out byte[]? curve)
    {
        curve = null;
        return false;
    }

    private double _ewmaCpuTemp = -1;
    private double _ewmaGpuTemp = -1;

    public bool TrySetFanCurve(AsusFan device, byte[] curve, out int result)
    {
        result = -1;
        if (device is not (AsusFan.CPU or AsusFan.GPU)) return false;

        // If "Max" mode is active, ignore all software curves and let ApplyMaxCooling keep fans at 100%
        if (AppConfig.Is("omen_turbo_is_max") && AppConfig.Get("performance_mode") == AsusACPI.PerformanceTurbo)
        {
            result = 1;
            return true;
        }

        // Force software loop to use the default curve if an empty curve is provided
        if ((curve.Length != 16 && curve.Length != 24) || curve.All(singleByte => singleByte == 0))
        {
            curve = AppConfig.GetDefaultCurve(device);
        }

        _curves[(int)device] = curve.ToArray();

        int cpuTempRaw = (int)(HardwareControl.GetCPUTemp() ?? _bios?.GetTemperature() ?? 0);
        int gpuTempRaw = (int)(HardwareControl.GetGPUTemp() ?? _bios?.GetGpuTemperature() ?? _bios?.GetTemperature() ?? 0);

        if (AppConfig.Is("fan_sync"))
        {
            int maxTemp = Math.Max(cpuTempRaw, gpuTempRaw);
            cpuTempRaw = maxTemp;
            gpuTempRaw = maxTemp;
        }

        // HP's Lamda_Increase and Lamda_Decrease constants from SwFanControlCustomFanCurve
        const double lamdaIncrease = 0.1;
        const double lamdaDecrease = 0.1;

        if (_ewmaCpuTemp < 0) _ewmaCpuTemp = cpuTempRaw;
        if (_ewmaGpuTemp < 0) _ewmaGpuTemp = gpuTempRaw;

        // EWMA formula from HP PowerContext.CalculateEWMA
        _ewmaCpuTemp = cpuTempRaw >= _ewmaCpuTemp 
            ? lamdaIncrease * cpuTempRaw + (1.0 - lamdaIncrease) * _ewmaCpuTemp 
            : lamdaDecrease * cpuTempRaw + (1.0 - lamdaDecrease) * _ewmaCpuTemp;

        _ewmaGpuTemp = gpuTempRaw >= _ewmaGpuTemp 
            ? lamdaIncrease * gpuTempRaw + (1.0 - lamdaIncrease) * _ewmaGpuTemp 
            : lamdaDecrease * gpuTempRaw + (1.0 - lamdaDecrease) * _ewmaGpuTemp;

        int cpuPercent = EvaluateCurve(_curves[(int)AsusFan.CPU], (int)Math.Round(_ewmaCpuTemp));
        int gpuPercent = EvaluateCurve(_curves[(int)AsusFan.GPU], (int)Math.Round(_ewmaGpuTemp));

        if (AppConfig.Get("gpu_mode") == AsusACPI.GPUModeEco)
        {
            gpuPercent = cpuPercent;
        }

        // Deduplicate calls to avoid WMI log spam when temperature fluctuates but curve flatlines
        if (cpuPercent == _lastEvaluatedCpuPercent && gpuPercent == _lastEvaluatedGpuPercent)
        {
            result = 1;
            return true;
        }

        _lastEvaluatedCpuPercent = cpuPercent;
        _lastEvaluatedGpuPercent = gpuPercent;

        result = _fans.SetFanSpeeds(cpuPercent, gpuPercent) ? 1 : -1;
        Logger.WriteLine($"OmenFanCurve {device}: CPU={cpuPercent}% GPU={gpuPercent}% (Temp {cpuTempRaw}C/{gpuTempRaw}C) : {(result == 1 ? "OK" : result)}");
        return true;
    }

    private int ReadFanRpm(bool cpu)
    {
        try
        {
            int rawRpm = 0;
            if (_monitor != null)
            {
                var sample = Task.Run(() => _monitor.ReadSampleAsync(default)).GetAwaiter().GetResult();
                rawRpm = cpu ? sample.Fan1Rpm : sample.Fan2Rpm;
            }

            // [FIX] If WMI gives us nothing (0 RPM), fallback to the IFanController which has access to LibreHardwareMonitor / EC
            if (rawRpm <= 0 && _fans != null)
            {
                var fanSpeeds = _fans.ReadFanSpeeds().ToList();
                if (cpu)
                {
                    var cpuFan = fanSpeeds.FirstOrDefault(f => f.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("System", StringComparison.OrdinalIgnoreCase));
                    if (cpuFan != null) rawRpm = cpuFan.SpeedRpm;
                    else if (fanSpeeds.Count > 0) rawRpm = fanSpeeds[0].SpeedRpm;
                }
                else
                {
                    var gpuFan = fanSpeeds.FirstOrDefault(f => f.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase));
                    if (gpuFan != null) rawRpm = gpuFan.SpeedRpm;
                    else if (fanSpeeds.Count > 1) rawRpm = fanSpeeds[1].SpeedRpm;
                }
            }

            // GHelper's FormatFan expects duty-cycle units (0-100).
            // When fanRpm mode is on it displays value*100 as RPM,
            // so divide actual RPM by 100: 2400 RPM → 24 → displayed as "2400 RPM".
            return Math.Max(0, rawRpm / 100);
        }
        catch { }

        return -1;
    }

    private static int EvaluateCurve(byte[]? curve, double temp)
    {
        if (curve == null || (curve.Length != 16 && curve.Length != 24)) return 0;

        int count = curve.Length / 2;
        int selected = curve[count];
        
        // Find the right segment and interpolate linearly
        if (temp <= curve[0]) return curve[count];
        if (temp >= curve[count - 1]) return curve[curve.Length - 1];

        for (int i = 0; i < count - 1; i++)
        {
            if (temp >= curve[i] && temp <= curve[i + 1])
            {
                double rangeTemp = curve[i + 1] - curve[i];
                if (rangeTemp == 0) return curve[i + count + 1]; // Avoid divide by zero

                double rangeSpeed = curve[i + count + 1] - curve[i + count];
                double progress = (temp - curve[i]) / rangeTemp;

                return Math.Clamp((int)Math.Round(curve[i + count] + (rangeSpeed * progress)), 0, 100);
            }
        }

        return curve[curve.Length - 1];
    }

    private static byte[] DefaultCurve(AsusFan fan)
    {
        byte[] temps = { 30, 50, 60, 70, 76, 80, 90, 100 };
        byte[] cpu = { 20, 35, 45, 55, 65, 75, 90, 100 };
        byte[] gpu = { 25, 35, 45, 55, 70, 80, 92, 100 };
        return temps.Concat(fan == AsusFan.GPU ? gpu : cpu).ToArray();
    }

    private static bool IsHpOmenSystem()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                string manufacturer = obj["Manufacturer"]?.ToString() ?? "";
                string model = obj["Model"]?.ToString() ?? "";
                string family = obj["SystemFamily"]?.ToString() ?? "";
                string combined = $"{manufacturer} {model} {family}";

                return combined.Contains("HP", StringComparison.OrdinalIgnoreCase) &&
                       (combined.Contains("OMEN", StringComparison.OrdinalIgnoreCase) ||
                        combined.Contains("Victus", StringComparison.OrdinalIgnoreCase) ||
                        combined.Contains("THETIGER", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"OMEN detect failed: {ex.Message}");
        }

        return false;
    }

    public bool VerifyCpuPowerLimitsWriteable()
    {
        if (PawnIO.CpuInfo.IsAMD) return true;

        if (!HasCpuPowerLimitControl) return false;

        // If we have MSR, check its lock status (and MMIO override)
        if (_msrAccess != null)
        {
            var msrStatus = _msrAccess.GetPowerLimitStatus();
            if (!msrStatus.IsLocked) return true;

            try
            {
                if (_mmioLimits == null)
                {
                    var mmioAccess = new OmenCore.Hardware.PawnIOMmioAccess((OmenCore.Hardware.PawnIOMsrAccess)_msrAccess);
                    if (mmioAccess.IsAvailable)
                    {
                        _mmioLimits = new OmenCore.Hardware.MmioPowerLimitProvider(mmioAccess);
                    }
                }

                if (_mmioLimits != null && _mmioLimits.IsAvailable)
                {
                    var mmioStatus = _mmioLimits.GetPowerLimits();
                    if (!mmioStatus.IsLocked) return true;
                }
            }
            catch { }
        }
        
        // If we don't have MSR, but we DO have BIOS WMI, assume writeable
        if (_bios?.IsAvailable == true) return true;

        return false;
    }

    public void Dispose()
    {
        try { _monitor?.Dispose(); } catch { }
        try { _fans?.Dispose(); } catch { }
        try { _bios?.Dispose(); } catch { }
    }

    public int OmenGetGpuMode()
    {
        try
        {
            var mode = _bios?.GetGpuMode();
            if (mode.HasValue) return (int)mode.Value;
        }
        catch { }
        return -1;
    }

    public bool OmenSetGpuMode(int mode)
    {
        try
        {
            if (_bios == null || !_bios.IsAvailable) return false;
            bool result = _bios.SetGpuMode((HpWmiBios.GpuMode)mode);
            Logger.WriteLine($"OmenGpuMode Set to {mode}: {result}");
            return result;
        }
        catch { return false; }
    }
}

public enum AsusMode
{
    Balanced = 0,
    Turbo = 1,
    Silent = 2
}

public enum AsusGPU
{
    Eco = 0,
    Standard = 1,
    Ultimate = 2
}

public class AsusACPI
{

    const string FILE_NAME = @"\\.\\ATKACPI";
    const uint CONTROL_CODE = 0x0022240C;

    const uint DSTS = 0x53545344;
    const uint DEVS = 0x53564544;
    const uint INIT = 0x54494E49;
    const uint WDOG = 0x474F4457;

    public const uint UniversalControl = 0x00100021;

    public const int Airplane = 0x88;
    public const int KB_Light_Up = 0xc4;
    public const int KB_Light_Down = 0xc5;
    public const int Brightness_Down = 0x10;
    public const int Brightness_Up = 0x20;
    public const int KB_Sleep = 0x6c;

    public const int KB_TouchpadToggle = 0x6b;
    public const int KB_MuteToggle = 0x7c;
    public const int KB_FNlockToggle = 0x4e;

    public const int KB_DUO_PgUpDn = 0x4B;
    public const int KB_DUO_SecondDisplay = 0x6A;

    public const int Touchpad_Toggle = 0x6B;

    public const int ChargerMode = 0x0012006C;

    public const int ChargerUSB = 2;
    public const int ChargerBarrel = 1;

    public const uint CPU_Fan = 0x00110013;
    public const uint GPU_Fan = 0x00110014;
    public const uint Mid_Fan = 0x00110031;

    public const uint BatteryDischarge = 0x0012005A;

    public const uint StatusMode = 0x00090031;
    public const uint PowerSavingMode = 0x00090032;

    public const uint PerformanceMode = 0x00120075; // Performance modes
    public const uint VivoBookMode = 0x00110019; // Vivobook performance modes

    public const uint GPUEcoROG = 0x00090020;
    public const uint GPUEcoVivo = 0x00090120;

    public const uint GPUXGConnected = 0x00090018;
    public const uint GPUXG = 0x00090019;

    public const uint GPUMuxROG = 0x00090016;
    public const uint GPUMuxVivo = 0x00090026;

    public const uint BatteryLimit = 0x00120057;

    public const uint ScreenOverdrive = 0x00050019;
    public const uint ScreenMiniled1 = 0x0005001E;
    public const uint ScreenMiniled2 = 0x0005002E;
    public const uint ScreenFHD = 0x0005001C;
    public const uint ScreenHDRControl = 0x00050071;

    public const uint ScreenOptimalBrightness = 0x0005002A;
    public const uint ScreenInit = 0x00050011; // ?

    public const uint DevsCPUFan = 0x00110022;
    public const uint DevsGPUFan = 0x00110023;

    public const uint DevsCPUFanCurve = 0x00110024;
    public const uint DevsGPUFanCurve = 0x00110025;
    public const uint DevsMidFanCurve = 0x00110032;

    public const uint FanHysteresis = 0x00110034;
    public const int Temp_CPU = 0x00120094;
    public const int Temp_GPU = 0x00120097;

    public const int PPT_APUA0 = 0x001200A0;  // sPPT (slow boost limit) / PL2
    public const int PPT_EDCA1 = 0x001200A1;  // CPU EDC
    public const int PPT_TDCA2 = 0x001200A2;  // CPU TDC
    public const int PPT_APUA3 = 0x001200A3;  // SPL (sustained limit) / PL1

    public const int PPT_CPUB0 = 0x001200B0;  // CPU PPT on 2022 (PPT_LIMIT_APU)
    public const int PPT_CPUB1 = 0x001200B1;  // Total PPT on 2022 (PPT_LIMIT_SLOW)

    public const int PPT_GPUC0 = 0x001200C0;  // NVIDIA GPU Boost
    public const int PPT_APUC1 = 0x001200C1;  // fPPT (fast boost limit)
    public const int PPT_GPUC2 = 0x001200C2;  // NVIDIA GPU Temp Target (75.. 87 C) 

    public const uint CORES_CPU = 0x001200D2; // Intel E-core and P-core configuration in a format 0x0[E]0[P]
    public const uint CORES_MAX = 0x001200D3; // Maximum Intel E-core and P-core availability

    public const uint GPU_BASE  = 0x00120099;  // Base part GPU TGP
    public const uint GPU_POWER = 0x00120098;  // Additonal part of GPU TGP

    public const int APU_MEM = 0x000600C1;

    public const int TUF_KB_BRIGHTNESS = 0x00050021;
    public const int KBD_BACKLIGHT_OOBE = 0x0005002F;

    public const int TUF_KB = 0x00100056;
    public const int TUF_KB2 = 0x0010005a;

    public const int TUF_KB_STATE = 0x00100057;

    public const int MicMuteLed = 0x00040017;
    public const int SoundMuteLed = 0x0004001C;

    public const int SlateMode = 0x00120063;
    public const int TabletState = 0x00060077;
    public const int TentState = 0x00060062;
    public const int FnLock = 0x00100023;

    public const int ScreenPadToggle = 0x00050031;
    public const int ScreenPadBrightness = 0x00050032;

    public const int CameraShutter = 0x00060078;
    public const int CameraLed = 0x00060079;
    public const int StatusLed = 0x000600C2;

    public const int BootSound = 0x00130022;

    public const int Tablet_Notebook = 0;
    public const int Tablet_Tablet = 1;
    public const int Tablet_Tent = 2;
    public const int Tablet_Rotated = 3;

    public const int PerformanceBalanced = 0;
    public const int PerformanceTurbo = 1;
    public const int PerformanceSilent = 2;
    public const int PerformanceManual = 4;

    public const int GPUModeEco = 0;
    public const int GPUModeStandard = 1;
    public const int GPUModeUltimate = 2;

    public const int MinTotal = 5;

    public static int MaxTotal = 150;
    public static int DefaultTotal = 80;

    public const int MinCPU = 5;
    public static int MaxCPU = 100;
    public const int DefaultCPU = 80;

    public static int MinGPUBoost = 0;
    public static int MaxGPUBoost = 25;

    public static int MinGPUPower = 0;
    public static int MaxGPUPower = 70;

    public const int MinGPUTemp = 75;
    public const int MaxGPUTemp = 87;

    public const int PCoreMin = 4;
    public const int ECoreMin = 0;

    public const int PCoreMax = 16;
    public const int ECoreMax = 16;

    private bool? _allAMD = null;
    private readonly Dictionary<uint, bool> _supportCache = new();
    private readonly OmenBackend? _omen;

    public static uint GPUEco => AppConfig.IsVivoZenPro() ? GPUEcoVivo : GPUEcoROG;
    public static uint GPUMux => AppConfig.IsVivoZenPro() ? GPUMuxVivo : GPUMuxROG;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        ref uint lpBytesReturned,
        IntPtr lpOverlapped
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;

    private IntPtr handle;

    // Event handling attempt

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);

    private IntPtr eventHandle;
    private bool _connected = false;

    public double GetCpuPackagePowerWatts()
    {
        double power = _omen?.GetCpuPackagePowerWatts() ?? 0.0;
        Logger.WriteLine($"[TelemetryTrace] AsusACPI.GetCpuPackagePowerWatts returning {power:F2}W (Omen is null? {_omen == null})");
        return power;
    }

    public void RunListener()
    {

        eventHandle = CreateEvent(IntPtr.Zero, false, false, "ATK4001");

        byte[] outBuffer = new byte[16];
        byte[] data = new byte[8];

        data[0] = BitConverter.GetBytes(eventHandle.ToInt32())[0];
        data[1] = BitConverter.GetBytes(eventHandle.ToInt32())[1];

        Control(0x222400, data, outBuffer);
        Logger.WriteLine("ACPI :" + BitConverter.ToString(data) + "|" + BitConverter.ToString(outBuffer));

        while (true)
        {
            WaitForSingleObject(eventHandle, Timeout.Infinite);
            Control(0x222408, new byte[0], outBuffer);
            int code = BitConverter.ToInt32(outBuffer);
            Logger.WriteLine("ACPI Code: " + code);
        }
    }

    public bool IsConnected()
    {
        return _connected || _omen?.IsAvailable == true;
    }

    public void InitializeGpuPowerController()
    {
#if DEBUG
        // if (_omen is OmenBackend backend && backend.Monitor != null)
        // {
        //     Task.Run(() => OmenCore.Hardware.GpuPowerController.Initialize(backend.Monitor));
        // }
#endif
    }

    public AsusACPI()
    {
        _omen = OmenBackend.TryCreate();

        try
        {
            handle = CreateFile(
                FILE_NAME,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero
            );

            //handle = new IntPtr(-1);
            //throw new Exception("ERROR");
            _connected = true;

        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Can't connect to ACPI: {ex.Message}");
        }

        // MaxTotal is now set dynamically from MSR 0x614 in OmenBackend constructor.
        // Device-specific MaxTotal overrides removed — the CPU itself reports its max.

        if (AppConfig.IsG14AMD())
        {
            DefaultTotal = 125;
        }

        if (AppConfig.IsAlly())
        {
            DefaultTotal = 30;
        }

        if (AppConfig.IsX13())
        {
            DefaultTotal = 50;
        }

        if (AppConfig.DynamicBoost5())
        {
            MaxGPUBoost = 5;
        }

        if (AppConfig.DynamicBoost15())
        {
            MaxGPUBoost = 15;
        }

        if (AppConfig.DynamicBoost20())
        {
            MaxGPUBoost = 20;
        }

    }

    public void Control(uint dwIoControlCode, byte[] lpInBuffer, byte[] lpOutBuffer)
    {

        uint lpBytesReturned = 0;
        DeviceIoControl(
            handle,
            dwIoControlCode,
            lpInBuffer,
            (uint)lpInBuffer.Length,
            lpOutBuffer,
            (uint)lpOutBuffer.Length,
            ref lpBytesReturned,
            IntPtr.Zero
        );
    }

    public void Close()
    {
        _omen?.Dispose(); // Dispose OMEN backend to clean up WMI/NVAPI resources
        CloseHandle(handle);
    }


    protected byte[] CallMethod(uint MethodID, byte[] args)
    {
        byte[] acpiBuf = new byte[8 + args.Length];
        byte[] outBuffer = new byte[16];

        BitConverter.GetBytes((uint)MethodID).CopyTo(acpiBuf, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(acpiBuf, 4);
        Array.Copy(args, 0, acpiBuf, 8, args.Length);

        // if (MethodID == DEVS)  Debug.WriteLine(BitConverter.ToString(acpiBuf, 0, acpiBuf.Length));

        Control(CONTROL_CODE, acpiBuf, outBuffer);

        return outBuffer;

    }

    public byte[] DeviceInit()
    {
        byte[] args = new byte[8];
        return CallMethod(INIT, args);

    }

    public byte[] DeviceWatchDog()
    {
        byte[] args = new byte[8];
        return CallMethod(WDOG, args);

    }

    public int DeviceSet(uint DeviceID, int Status, string? logName)
    {
        if (_omen?.TryDeviceSet(DeviceID, Status, logName, out int omenResult) == true)
            return omenResult;

        byte[] args = new byte[8];
        BitConverter.GetBytes((uint)DeviceID).CopyTo(args, 0);
        BitConverter.GetBytes((uint)Status).CopyTo(args, 4);

        byte[] status = CallMethod(DEVS, args);
        int result = BitConverter.ToInt32(status, 0);

        if (logName is not null)
            Logger.WriteLine(logName + " = " + Status + " : " + (result == 1 ? "OK" : result));

        return result;
    }


    public int DeviceSet(uint DeviceID, byte[] Params, string? logName)
    {
        if (_omen?.TryDeviceSet(DeviceID, Params, logName, out int omenResult) == true)
            return omenResult;

        byte[] args = new byte[4 + Params.Length];
        BitConverter.GetBytes((uint)DeviceID).CopyTo(args, 0);
        Params.CopyTo(args, 4);

        byte[] status = CallMethod(DEVS, args);
        int result = BitConverter.ToInt32(status, 0);

        if (logName is not null)
            Logger.WriteLine(logName + " = " + BitConverter.ToString(Params) + " : " + (result == 1 ? "OK" : result));

        return BitConverter.ToInt32(status, 0);
    }


    public int DeviceGet(uint DeviceID)
    {
        if (_omen?.TryDeviceGet(DeviceID, out int omenResult) == true)
            return omenResult;

        byte[] args = new byte[8];
        BitConverter.GetBytes((uint)DeviceID).CopyTo(args, 0);
        byte[] status = CallMethod(DSTS, args);

        return BitConverter.ToInt32(status, 0) - 65536;

    }

    public byte[] DeviceGetBuffer(uint DeviceID, uint Status = 0)
    {
        if (_omen?.TryDeviceGetBuffer(DeviceID, Status, out byte[]? omenResult) == true && omenResult != null)
            return omenResult;

        byte[] args = new byte[8];
        BitConverter.GetBytes((uint)DeviceID).CopyTo(args, 0);
        BitConverter.GetBytes((uint)Status).CopyTo(args, 4);

        return CallMethod(DSTS, args);
    }


    public decimal? GetBatteryDischarge()
    {
        if (_omen?.TryGetBatteryDischarge(out decimal? omenDischarge) == true)
            return omenDischarge;

        var buffer = DeviceGetBuffer(BatteryDischarge);

        if (buffer[2] > 0)
        {
            buffer[2] = 0;
            return (decimal)BitConverter.ToInt16(buffer, 0) / 100;
        }
        else
        {
            return null;
        }
    }


    public int SetVivoMode(int mode)
    {
        if (mode == 1) mode = 2;
        else if (mode == 2) mode = 1;
        return Program.acpi.DeviceSet(VivoBookMode, mode, "VivoMode");
    }

    public int SetGPUEco(int eco)
    {
        // OMEN path: bypass the eco-flag roundtrip; switch GPU mode directly
        if (_omen?.TrySetGpuEco(eco, out int omenResult) == true)
            return omenResult;

        uint ecoEndpoint = GPUEco;

        int ecoFlag = DeviceGet(ecoEndpoint);
        if (ecoFlag < 0) return -1;

        if (ecoFlag == 1 && eco == 0)
            return DeviceSet(ecoEndpoint, eco, "GPUEco");

        if (ecoFlag == 0 && eco == 1)
            return DeviceSet(ecoEndpoint, eco, "GPUEco");

        return -1;
    }

    public int GetFan(AsusFan device)
    {
        if (_omen?.TryGetFan(device, out int omenFan) == true)
            return omenFan;

        int fan = -1;

        switch (device)
        {
            case AsusFan.GPU:
                fan = Program.acpi.DeviceGet(GPU_Fan);
                break;
            case AsusFan.Mid:
                fan = Program.acpi.DeviceGet(Mid_Fan);
                break;
            default:
                fan = Program.acpi.DeviceGet(CPU_Fan);
                break;
        }

        if (fan < 0)
        {
            fan += 65536;
            if (fan <= 0 || fan > 100) fan = -1;
        }

        return fan;
    }

    public bool IsMidFanSupported()
    {
        if (_omen?.IsAvailable == true) return false;
        return IsSupported(Mid_Fan);
    }

    public int SetFanRange(AsusFan device, byte[] curve)
    {
        if (_omen?.IsAvailable == true)
            return SetFanCurve(device, curve);

        if (curve.Length != 16) return -1;
        if (curve.All(singleByte => singleByte == 0)) return -1;

        byte min = (byte)(curve[8] * 255 / 100);
        byte max = (byte)(curve[15] * 255 / 100);
        byte[] range = { min, max };

        int result;
        switch (device)
        {
            case AsusFan.GPU:
                result = DeviceSet(DevsGPUFan, range, "FanRangeGPU");
                break;
            default:
                result = DeviceSet(DevsCPUFan, range, "FanRangeCPU");
                break;
        }

        return result;
    }


    public void RestoreFansToAuto()
    {
        _omen?.RestoreAutoControl();
    }

    public int SetFanCurve(AsusFan device, byte[] curve)
    {
        if (_omen?.TrySetFanCurve(device, curve, out int omenResult) == true)
            return omenResult;

        if (curve.Length != 16) return -1;
        if (curve.All(singleByte => singleByte == 0)) return -1;

        int result;

        int fanScale = AppConfig.Get("fan_scale", 100);

        if (fanScale != 100 && device == AsusFan.CPU) Logger.WriteLine("Custom fan scale: " + fanScale);

        for (int i = 8; i < curve.Length; i++) curve[i] = (byte)(Math.Max((byte)0, Math.Min((byte)100, curve[i])) * fanScale / 100);

        switch (device)
        {
            case AsusFan.GPU:
                result = DeviceSet(DevsGPUFanCurve, curve, "FanGPU");
                break;
            case AsusFan.Mid:
                result = DeviceSet(DevsMidFanCurve, curve, "FanMid");
                break;
            default:
                result = DeviceSet(DevsCPUFanCurve, curve, "FanCPU");
                break;
        }

        return result;
    }

    public byte[] GetFanCurve(AsusFan device, int mode = 0)
    {
        if (_omen?.TryGetFanCurve(device, mode, out byte[]? omenCurve) == true && omenCurve != null)
            return omenCurve;

        uint fan_mode;

        // because it's asus, and modes are swapped here
        switch (mode)
        {
            case 1: fan_mode = 2; break;
            case 2: fan_mode = 1; break;
            default: fan_mode = 0; break;
        }

        byte[] result;

        switch (device)
        {
            case AsusFan.GPU:
                result = DeviceGetBuffer(DevsGPUFanCurve, fan_mode);
                break;
            case AsusFan.Mid:
                result = DeviceGetBuffer(DevsMidFanCurve, fan_mode);
                break;
            default:
                result = DeviceGetBuffer(DevsCPUFanCurve, fan_mode);
                break;
        }

        //Logger.WriteLine($"GetFan {device} :" + BitConverter.ToString(result));

        return result;

    }

    public static bool IsInvalidCurve(byte[] curve)
    {
        return (curve.Length != 16 && curve.Length != 24) || IsEmptyCurve(curve);
    }

    public static bool IsEmptyCurve(byte[] curve)
    {
        return curve.All(singleByte => singleByte == 0);
    }

    public (int up, int down) GetFanHysteresis()
    {
        if (_omen?.IsAvailable == true) return (-1, -1);

        int value = DeviceGet(FanHysteresis);
        if (value < 0)
        {
            //Logger.WriteLine($"FanHysteresis Read: not supported ({value})");
            return (-1, -1);
        }
        int up = value & 0xFF;
        int down = (value >> 8) & 0xFF;
        Logger.WriteLine($"FanHysteresis Read: up={up} down={down} (raw=0x{value:X4})");
        return (up, down);
    }

    public int SetFanHysteresis(int up, int down)
    {
        if (_omen?.IsAvailable == true) return -1;

        int result = -1;
        int value = (down << 8) | up;

        if (IsSupported(FanHysteresis))
        {
            byte[] payload = new byte[16];
            int slots = AppConfig.Is("mid_fan") ? 3 : 2;
            for (int i = 0; i < slots; i++)
            {
                payload[i * 4]     = (byte)up;
                payload[i * 4 + 1] = (byte)down;
            }
            Logger.WriteLine($"FanHysteresis Write: up={up} down={down} (per-fan=0x{value:X4}, slots={slots})");
            result = DeviceSet(FanHysteresis, payload, "FanHysteresis");
        }

        return result;
    }

    public static byte[] FixFanCurve(byte[] curve)
    {
        if (curve.Length != 16 && curve.Length != 24) throw new Exception("Incorrect curve");

        int length = curve.Length / 2;

        var points = new Dictionary<byte, byte>();
        byte old = 0;

        for (int i = 0; i < length; i++)
        {
            if (curve[i] <= old) curve[i] = (byte)Math.Min(100, old + 6); // preventing 2 points in same spot from default asus profiles
            points[curve[i]] = curve[i + length];
            old = curve[i];
        }

        var pointsFixed = new Dictionary<byte, byte>();
        bool fix = false;

        int count = 0;
        foreach (var pair in points.OrderBy(x => x.Key))
        {
            if (count == 0 && pair.Key >= 40)
            {
                fix = true;
                pointsFixed.Add(30, 0);
            }

            if (count != 3 || !fix)
                pointsFixed.Add(pair.Key, pair.Value);
            count++;
        }

        count = 0;
        foreach (var pair in pointsFixed.OrderBy(x => x.Key))
        {
            int x = pair.Key;

            if (AppConfig.IsClampFanDots())
            {
                int minX = length == 8 ? (30 + (count * 10)) : (20 + (count * 6));
                int maxX = minX + (length == 8 ? 10 : 6);
                x = Math.Max(minX, Math.Min(maxX, x));
            }

            curve[count] = (byte)x;
            curve[count + length] = pair.Value;
            count++;
        }

        return curve;

    }

    public bool IsXGConnected()
    {
        if (_omen?.IsAvailable == true) return false;
        return DeviceGet(GPUXGConnected) == 1;
    }

    public bool IsAllAmdPPT()
    {
        if (_omen?.IsAvailable == true) return false;
        if (_allAMD is null) _allAMD = IsSupported(PPT_CPUB0) && !IsSupported(PPT_GPUC0) && !AppConfig.IsAMDiGPU();
        return (bool)_allAMD;
    }

    public bool IsOverdriveSupported()
    {
        return IsSupported(ScreenOverdrive);
    }

    public bool IsSupported(uint DeviceID)
    {
        if (_omen?.TryIsSupported(DeviceID, out bool omenSupported) == true)
            return omenSupported;

        if (!_supportCache.TryGetValue(DeviceID, out bool supported))
        {
            supported = DeviceGet(DeviceID) >= 0;
            _supportCache[DeviceID] = supported;
        }
        return supported;
    }

    public bool IsNVidiaGPU()
    {
        if (_omen?.IsAvailable == true) return true;
        return (!IsAllAmdPPT() && IsSupported(GPUEco) && !AppConfig.IsAlly());
    }

    public void SetAPUMem(int memory = 4)
    {
        if (memory < 0 || memory > 8) return;

        int mem = 0;

        switch (memory)
        {
            case 0:
                mem = 0;
                break;
            case 1:
                mem = 258;
                break;
            case 2:
                mem = 259;
                break;
            case 3:
                mem = 260;
                break;
            case 4:
                mem = 261;
                break;
            case 5:
                mem = 263;
                break;
            case 6:
                mem = 264;
                break;
            case 7:
                mem = 265;
                break;
            case 8:
                mem = 262;
                break;
        }

        Program.acpi.DeviceSet(APU_MEM, mem, "APU Mem");
    }

    public int GetAPUMem()
    {
        int memory = Program.acpi.DeviceGet(APU_MEM);
        if (memory < 0) return -1;

        switch (memory)
        {
            case 256:
                return 0;
            case 258:
                return 1;
            case 259:
                return 2;
            case 260:
                return 3;
            case 261:
                return 4;
            case 262:
                return 8;
            case 263:
                return 5;
            case 264:
                return 6;
            case 265:
                return 7;
            default:
                return 4;
        }
    }

    public (int, int) GetCores(bool max = false)
    {
        int value = Program.acpi.DeviceGet(max ? CORES_MAX : CORES_CPU);
        //value = max ? 0x406 : 0x605;

        if (value < 0) return (-1, -1);
        Logger.WriteLine("Cores" + (max ? "Max" : "") + ": 0x" + value.ToString("X4"));

        return ((value >> 8) & 0xFF, (value) & 0xFF);
    }

    public void SetCores(int eCores, int pCores)
    {
        if (eCores < ECoreMin || eCores > ECoreMax || pCores < PCoreMin || pCores > PCoreMax)
        {
            Logger.WriteLine($"Incorrect Core config ({eCores}, {pCores})");
            return;
        };

        int value = (eCores << 8) | pCores;
        Program.acpi.DeviceSet(CORES_CPU, value, "Cores (0x" + value.ToString("X4") + ")");
    }

    public string ScanRange()
    {
        int value;
        string appPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\GHelper";
        string logFile = appPath + "\\scan.txt";
        using (StreamWriter w = File.AppendText(logFile))
        {
            w.WriteLine($"Scan started {DateTime.Now}");
            for (uint i = 0x00000000; i <= 0x00160000; i += 0x10000)
            {
                for (uint j = 0x00; j <= 0xFF; j++)
                {
                    uint id = i + j;
                    value = DeviceGet(id);
                    if (value >= 0)
                    {
                        w.WriteLine(id.ToString("X8") + ": " + value.ToString("X4") + " (" + value + ")");
                    }
                }
            }
            w.WriteLine($"---------------------");
            w.Close();
        }

        return logFile;

    }

    public void TUFKeyboardBrightness(int brightness, string log = "TUF Backlight")
    {
        int param = 0x80 | (brightness & 0x7F);
        DeviceSet(TUF_KB_BRIGHTNESS, param, log);

    }

    public void TUFKeyboardRGB(AuraMode mode, Color color, int speed, string? log = "TUF RGB")
    {

        byte[] setting = new byte[6];

        setting[0] = (byte)0xb4;
        setting[1] = (byte)mode;
        setting[2] = color.R;
        setting[3] = color.G;
        setting[4] = color.B;
        setting[5] = (byte)speed;

        int result = DeviceSet(TUF_KB, setting, log);
        if (result != 1)
        {
            setting[0] = (byte)0xb3;
            DeviceSet(TUF_KB2, setting, log);
            setting[0] = (byte)0xb4;
            DeviceSet(TUF_KB2, setting, log);
        }

    }

    const int ASUS_WMI_KEYBOARD_POWER_BOOT = 0x03 << 16;
    const int ASUS_WMI_KEYBOARD_POWER_AWAKE = 0x0C << 16;
    const int ASUS_WMI_KEYBOARD_POWER_SLEEP = 0x30 << 16;
    const int ASUS_WMI_KEYBOARD_POWER_SHUTDOWN = 0xC0 << 16;
    public void TUFKeyboardPower(bool awake = true, bool boot = false, bool sleep = false, bool shutdown = false)
    {
        int state = 0xbd;

        if (boot) state = state | ASUS_WMI_KEYBOARD_POWER_BOOT;
        if (awake) state = state | ASUS_WMI_KEYBOARD_POWER_AWAKE;
        if (sleep) state = state | ASUS_WMI_KEYBOARD_POWER_SLEEP;
        if (shutdown) state = state | ASUS_WMI_KEYBOARD_POWER_SHUTDOWN;

        state = state | 0x01 << 8;

        DeviceSet(TUF_KB_STATE, state, "TUF_KB");
        if (AppConfig.IsVivoZenPro() && IsSupported(KBD_BACKLIGHT_OOBE)) DeviceSet(KBD_BACKLIGHT_OOBE, 1, "VIVO OOBE");
    }

    public bool IsOmen() => _omen?.IsAvailable == true;
    public bool IsOmenV2() => _omen?.IsOmenV2() == true;

    public int OmenGetGpuMode()
    {
        return _omen?.OmenGetGpuMode() ?? -1;
    }

    public bool OmenSetGpuMode(int mode)
    {
        return _omen?.OmenSetGpuMode(mode) ?? false;
    }

    public bool HasOmenPerKeyRgb()
    {
        return _omen?.HasPerKeyRgb() == true;
    }

    /// <summary>
    /// Returns the OMEN lighting service for the lighting control form.
    /// Returns null on non-OMEN systems or if WMI BIOS is unavailable.
    /// </summary>
    public OmenCore.Hardware.OmenLightingService? GetLightingService()
    {
        return _omen?.GetLightingService();
    }

    public void SetOmenColor(System.Drawing.Color color)
    {
        _omen?.SetColor(color);
    }

    public bool VerifyCpuPowerLimitsWriteable()
    {
        if (_omen != null)
        {
            return _omen.VerifyCpuPowerLimitsWriteable();
        }

        return IsSupported(PPT_APUA0) || CpuInfo.IsAMD;
    }

    private ManagementEventWatcher? watcher;
    private ManagementEventWatcher? omenWatcher;

    public void SubscribeToEvents(Action<object, EventArrivedEventArgs> EventHandler)
    {
        try
        {
            watcher = new ManagementEventWatcher();
            watcher.EventArrived += new EventArrivedEventHandler(EventHandler);
            watcher.Scope = new ManagementScope("root\\wmi");
            watcher.Query = new WqlEventQuery("SELECT * FROM AsusAtkWmiEvent");
            watcher.Start();
        }
        catch
        {
            Logger.WriteLine("Can't connect to ASUS WMI events");
        }
    }

    public void SubscribeToOmenEvents()
    {
        try
        {
            if (omenWatcher != null) return;

            Logger.WriteLine("Starting OMEN WMI listener...");
            
            ManagementScope scope = new ManagementScope(@"root\wmi");
            scope.Connect();
            
            WqlEventQuery query = new WqlEventQuery("SELECT * FROM hpqBEvnt");
            
            omenWatcher = new ManagementEventWatcher(scope, query);
            omenWatcher.EventArrived += (sender, e) =>
            {
                try
                {
                    int eventId = Convert.ToInt32(e.NewEvent["eventId"]);
                    int eventData = Convert.ToInt32(e.NewEvent["eventData"]);

                    Logger.WriteLine($"OMEN WMI Event: {eventId}, {eventData}");

                    if (eventId == 29 && (eventData == 8613 || eventData == 8614))
                    {
                        Logger.WriteLine("OMEN Key Detected! Toggling settings...");
                        Program.toast.RunToast("OMEN Key", ToastIcon.BrightnessUp);
                        
                        Program.settingsForm.BeginInvoke(delegate
                        {
                            Program.SettingsToggle();
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Error in OMEN event handler: " + ex.Message);
                }
            };
            
            omenWatcher.Start();
            Logger.WriteLine("✓ OMEN WMI listener active");
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Failed to start OMEN WMI events: " + ex.Message);
        }
    }

}
