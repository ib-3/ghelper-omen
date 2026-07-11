using GHelper;
using GHelper.Battery;
using GHelper.Fan;
using GHelper.Gpu;
using GHelper.Gpu.AMD;
using GHelper.Gpu.NVidia;
using GHelper.Helpers;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

public static class HardwareControl
{

    public static IGpuControl? GpuControl;

    public static bool IsEcoMode()
    {
        // Primary: stored preference in AppConfig
        if (AppConfig.Get("gpu_mode") == AsusACPI.GPUModeEco) return true;
        // Fallback: live BIOS GPUEco register (covers cases where AppConfig hasn't been
        // written yet on this boot, e.g. the first call in Program.cs before InitGPUMode runs)
        try { return Program.acpi?.DeviceGet(AsusACPI.GPUEco) == 1; }
        catch { return false; }
    }


    public static float? cpuTemp = -1;
    public static float? gpuTemp = -1;

    public static decimal? batteryRate = 0;
    public static decimal batteryHealth = -1;
    public static decimal batteryCapacity = -1;

    public static decimal? designCapacity;
    public static decimal? fullCapacity;
    public static decimal? chargeCapacity;

    public static string? batteryCharge;

    public static string? cpuFan;
    public static string? gpuFan;
    public static string? midFan;

    public static int? gpuUse;

    static long lastUpdate;

    static bool isPZ13 = AppConfig.IsPZ13();

    static bool _chargeWatt = AppConfig.Is("charge_watt");

    static PerformanceCounter? _cpuTempCounter;

    #region Native Battery API

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int InformationLevel,
        IntPtr InputBuffer,
        uint InputBufferLength,
        IntPtr OutputBuffer,
        uint OutputBufferLength);

    private const int SystemBatteryState = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_BATTERY_STATE
    {
        [MarshalAs(UnmanagedType.U1)] public bool AcOnLine;
        [MarshalAs(UnmanagedType.U1)] public bool BatteryPresent;
        [MarshalAs(UnmanagedType.U1)] public bool Charging;
        [MarshalAs(UnmanagedType.U1)] public bool Discharging;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public byte Spare4;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    private static SYSTEM_BATTERY_STATE? GetNativeBatteryState()
    {
        int size = Marshal.SizeOf<SYSTEM_BATTERY_STATE>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            uint status = CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, ptr, (uint)size);
            if (status == 0)
                return Marshal.PtrToStructure<SYSTEM_BATTERY_STATE>(ptr);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        ref uint lpInBuffer, uint nInBufferSize,
        ref uint lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlStatus(
        IntPtr hDevice, uint dwIoControlCode,
        ref BATTERY_WAIT_STATUS lpInBuffer, uint nInBufferSize,
        ref BATTERY_STATUS lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_WAIT_STATUS
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BATTERY_STATUS
    {
        public uint PowerState;
        public uint Capacity;
        public int Voltage;
        public int Rate;
    }

    private static readonly Guid GUID_DEVINTERFACE_BATTERY = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

    private static string? _batteryDevicePath;

    private static string? GetBatteryDevicePath()
    {
        if (_batteryDevicePath != null) return _batteryDevicePath;

        Guid batteryGuid = GUID_DEVINTERFACE_BATTERY;
        IntPtr deviceInfoSet = SetupDiGetClassDevs(ref batteryGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (deviceInfoSet == INVALID_HANDLE_VALUE) return null;

        try
        {
            SP_DEVICE_INTERFACE_DATA did = new();
            did.cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

            if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref batteryGuid, 0, ref did))
                return null;

            SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref did, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
            if (requiredSize == 0) return null;

            IntPtr detailData = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);

                if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref did, detailData, requiredSize, out _, IntPtr.Zero))
                    return null;

                _batteryDevicePath = Marshal.PtrToStringAuto(detailData + 4);
                return _batteryDevicePath;
            }
            finally
            {
                Marshal.FreeHGlobal(detailData);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static BATTERY_STATUS? QueryBatteryStatus()
    {
        string? devicePath = GetBatteryDevicePath();
        if (devicePath == null) return null;

        IntPtr handle = CreateFile(devicePath, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

        if (handle == INVALID_HANDLE_VALUE) return null;

        try
        {
            uint timeout = 0;
            uint batteryTag = 0;
            if (!DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG,
                ref timeout, 4, ref batteryTag, 4, out _, IntPtr.Zero) || batteryTag == 0)
                return null;

            BATTERY_WAIT_STATUS waitStatus = new() { BatteryTag = batteryTag };
            BATTERY_STATUS status = new();

            if (!DeviceIoControlStatus(handle, IOCTL_BATTERY_QUERY_STATUS,
                ref waitStatus, (uint)Marshal.SizeOf<BATTERY_WAIT_STATUS>(),
                ref status, (uint)Marshal.SizeOf<BATTERY_STATUS>(),
                out _, IntPtr.Zero))
                return null;

            return status;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    #endregion

    public static bool chargeWatt
    {
        get
        {
            return _chargeWatt;
        }
        set
        {
            AppConfig.Set("charge_watt", value ? 1 : 0);
            _chargeWatt = value;
        }
    }

    private static int GetGpuUse()
    {
        if (IsEcoMode()) return 0;

        try
        {
            int? gpuUse = GpuControl?.GetGpuUse();
            Logger.WriteLine("GPU usage: " + GpuControl?.FullName + " " + gpuUse + "%");
            if (gpuUse is not null) return (int)gpuUse;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }

        return 0;
    }

    static long _lastBatteryRead;
    static long _lastCapacityTime = 0;
    static decimal _lastCapacity = 0;

    public static void ReadBatteryState()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (Math.Abs(now - _lastBatteryRead) < 5000)
        {
            FormatBatteryCharge();
            return;
        }
        _lastBatteryRead = now;

        batteryRate = 0;
        chargeCapacity = 0;

        try
        {
            if (AppConfig.IsAlly())
            {
                decimal? discharge = Program.acpi.GetBatteryDischarge();
                if (discharge is not null)
                {
                    batteryRate = discharge;

                    // Capacity from cached power manager state is sufficient
                    var batteryState = GetNativeBatteryState();
                    if (batteryState.HasValue)
                    {
                        chargeCapacity = batteryState.Value.RemainingCapacity;

                        if (fullCapacity is null or 0 && batteryState.Value.MaxCapacity > 0)
                            fullCapacity = batteryState.Value.MaxCapacity;
                    }
                    FormatBatteryCharge();
                    return;
                }
            }

            var statusTask = Task.Run(QueryBatteryStatus);
            var directStatus = statusTask.Wait(1000) ? statusTask.Result : null;

            if (directStatus.HasValue)
            {
                chargeCapacity = directStatus.Value.Capacity;
                if (directStatus.Value.Rate != 0)
                    batteryRate = (decimal)directStatus.Value.Rate / 1000;
            }

            // [NEW] Manual calculation fallback for models that don't support rate querying
            if (batteryRate == 0 && chargeCapacity > 0)
            {
                if (_lastCapacity > 0 && _lastCapacityTime > 0)
                {
                    long timeDiff = now - _lastCapacityTime;
                    if (timeDiff >= 10000) // Minimum 10 seconds difference for accuracy
                    {
                        decimal capacityDiff = _lastCapacity - chargeCapacity.Value;
                        batteryRate = capacityDiff * 3600m / timeDiff;
                        
                        _lastCapacity = chargeCapacity.Value;
                        _lastCapacityTime = now;
                    }
                }
                else
                {
                    _lastCapacity = chargeCapacity.Value;
                    _lastCapacityTime = now;
                }
            }
            else if (chargeCapacity > 0)
            {
                 // Keep updating values so if IOCTL randomly fails next time, we have a recent delta
                 _lastCapacity = chargeCapacity.Value;
                 _lastCapacityTime = now;
            }

            // MaxCapacity doesn't change at runtime, only need it once
            if (fullCapacity is null or 0)
            {
                var batteryState = GetNativeBatteryState();
                if (batteryState.HasValue && batteryState.Value.MaxCapacity > 0)
                    fullCapacity = batteryState.Value.MaxCapacity;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Battery Reading: " + ex.Message);
        }

        FormatBatteryCharge();
    }

    private static void FormatBatteryCharge()
    {
        if (fullCapacity > 0 && chargeCapacity > 0)
        {
            batteryCapacity = Math.Min(100, (decimal)chargeCapacity / (decimal)fullCapacity * 100);
            if (batteryCapacity > 99 && BatteryControl.chargeFull) BatteryControl.UnSetBatteryLimitFull();
            batteryCharge = chargeWatt
                ? Math.Round((decimal)chargeCapacity / 1000, 1) + "Wh"
                : Math.Round(batteryCapacity, 1) + "%";
        }
    }

    public static void ReadDesignCapacity()
    {
        if (designCapacity > 0) return;

        try
        {

            ManagementScope scope = new ManagementScope("root\\WMI");
            ObjectQuery query = new ObjectQuery("SELECT DesignedCapacity FROM BatteryStaticData");

            using ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                using (obj)
                {
                    designCapacity = Convert.ToDecimal(obj["DesignedCapacity"]);
                }
            }

        }
        catch (Exception ex)
        {
            Debug.WriteLine("Design Capacity Reading: " + ex.Message);
        }
    }

    public static void RefreshBatteryHealth()
    {
        if (designCapacity is null) ReadDesignCapacity();
        if (fullCapacity is null or 0) ReadBatteryState();

        if (designCapacity is null || fullCapacity is null || designCapacity == 0 || fullCapacity == 0)
        {
            batteryHealth = -1;
            return;
        }

        decimal health = (decimal)fullCapacity / (decimal)designCapacity;
        Logger.WriteLine("Design Capacity: " + designCapacity + "mWh, Full Charge Capacity: " + fullCapacity + "mWh, Health: " + health + "%");
        batteryHealth = health * 100;
    }


    public static float? GetCPUTemp()
    {
        var last = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (Math.Abs(last - lastUpdate) < 2) return cpuTemp;
        lastUpdate = last;

        if (isPZ13)
        {
            var pzTemp = (float)GetCPUTempWMI();
            Logger.WriteLine($"[GetCPUTemp] Source: PZ13 WMI → {pzTemp}°C");
            return pzTemp;
        }

        // 1. Primary: WMI/OMEN path (LibreHardwareMonitor → HP WMI BIOS → ACPI)
        //    This is the accurate CPU package/core temperature source on HP Omen laptops.
        cpuTemp = Program.acpi.DeviceGet(AsusACPI.Temp_CPU);
        string source = "OmenBackend.DeviceGet(Temp_CPU)";

        // 2. Last-resort fallback: dynamic Performance Counter discovery.
        //    Only used when the primary WMI path returns nothing valid.
        //    NOTE: Performance Counters on HP Omen often report a generic/ambient zone
        //    rather than the actual CPU package, so this is intentionally a fallback only.
        if (cpuTemp == null || cpuTemp <= 0) try
        {
            if (_cpuTempCounter == null)
            {
                string instanceName = @"\_TZ.THRM"; // default fallback
                try
                {
                    var category = new PerformanceCounterCategory("Thermal Zone Information");
                    string[] instances = category.GetInstanceNames();
                    Logger.WriteLine($"[GetCPUTemp] PerfCounter instances found: [{string.Join(", ", instances)}]");
                    if (instances.Length > 0)
                    {
                        // Prefer instances containing "THRM", "TZ", or "CPU"
                        string? bestInstance = null;
                        foreach (string inst in instances)
                        {
                            if (inst.Contains("THRM", StringComparison.OrdinalIgnoreCase) ||
                                inst.Contains("TZ", StringComparison.OrdinalIgnoreCase) ||
                                inst.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                            {
                                bestInstance = inst;
                                break;
                            }
                        }
                        instanceName = bestInstance ?? instances[0];
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Failed to dynamically find Thermal Zone instances: " + ex.Message);
                }

                _cpuTempCounter = new PerformanceCounter("Thermal Zone Information", "Temperature", instanceName, true);
                Logger.WriteLine($"[GetCPUTemp] PerfCounter using instance: '{instanceName}'");
            }

            cpuTemp = _cpuTempCounter.NextValue() - 273;
            source = $"PerfCounter(fallback)";
        }
        catch (Exception)
        {
            //Debug.WriteLine("Failed reading CPU temp via Performance Counter: " + ex.Message);
        }

        Logger.WriteLine($"[GetCPUTemp] RESULT: {cpuTemp}°C via {source}");
        return cpuTemp;
    }

    static double GetCPUTempWMI()
    {
        try
        {
            string wmiNamespace = @"root\WMI";
            string wmiQuery = @"SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature WHERE InstanceName = 'ACPI\\QCOM0C5A\\1_0'";  // ACPI\\ThermalZone\\THRM_0
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(wmiNamespace, wmiQuery))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        double tempKelvin = Convert.ToDouble(obj["CurrentTemperature"]);
                        return (tempKelvin / 10) - 273.15;
                    }
                }
            }
        }
        catch (Exception)
        {
            //Logger.WriteLine("Error retrieving temperature: " + ex.Message);
        }
        return -1;
    }

    public static float? GetGPUTemp()
    {
        if (IsEcoMode()) return null;

        try
        {
            gpuTemp = GpuControl?.GetCurrentTemperature();

        }
        catch (Exception)
        {
            gpuTemp = -1;
            //Debug.WriteLine("Failed reading GPU temp :" + ex.Message);
        }

        if (gpuTemp is null || gpuTemp < 0)
        {
            int acpiTemp = Program.acpi.DeviceGet(AsusACPI.Temp_GPU);
            gpuTemp = (acpiTemp > 0 && acpiTemp < 125) ? acpiTemp : null;
        }

        return gpuTemp;
    }


    public static void ReadSensors(bool log = false)
    {
        gpuUse = -1;

        if (Program.acpi is null) return;

        cpuFan = FanSensorControl.FormatFan(AsusFan.CPU, Program.acpi.GetFan(AsusFan.CPU));
        gpuFan = FanSensorControl.FormatFan(AsusFan.GPU, Program.acpi.GetFan(AsusFan.GPU));
        midFan = FanSensorControl.FormatFan(AsusFan.Mid, Program.acpi.GetFan(AsusFan.Mid));

        cpuTemp = GetCPUTemp();
        gpuTemp = GetGPUTemp();

        if (log) Logger.WriteLine($"Temps: {cpuTemp} {gpuTemp} {cpuFan} {gpuFan} {midFan}");

        // [TEMPERATURE FIX 2026-05-29] Evaluate OMEN fan curves dynamically in software
        // since the OMEN firmware lacks hardware evaluation for custom curves.
        if (Program.acpi.IsOmen() && AppConfig.IsApplyFans())
        {
            Program.acpi.SetFanCurve(AsusFan.CPU, AppConfig.GetFanConfig(AsusFan.CPU));
            Program.acpi.SetFanCurve(AsusFan.GPU, AppConfig.GetFanConfig(AsusFan.GPU));
        }

        ReadBatteryState();
    }

    public static double GetBatteryChargePercentage()
    {
        try
        {
            SYSTEM_POWER_STATUS status = default;
            if (GetSystemPowerStatus(ref status) && status.BatteryLifePercent != 255)
            {
                return status.BatteryLifePercent;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Battery Percentage Reading: " + ex.Message);
        }
        return 0;
    }

    public static bool IsUsedGPU(int threshold = 10)
    {
        if (GetGpuUse() > threshold)
        {
            Thread.Sleep(1000);
            return (GetGpuUse() > threshold);
        }
        return false;
    }


    public static NvidiaGpuControl? GetNvidiaGpuControl()
    {
        if ((bool)GpuControl?.IsNvidia)
            return (NvidiaGpuControl)GpuControl;
        else
            return null;
    }

    public static void DisposeGpuControl()
    {
        GpuControl?.Dispose();
        GpuControl = null;
    }

    public static void RecreateGpuControlWithDelay(int delay = 5)
    {
        // Re-enabling the discrete GPU takes a bit of time,
        // so a simple workaround is to refresh again after that happens
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
            RecreateGpuControl();
        });
    }

    public static void RecreateGpuControl()
    {
        if (AppConfig.NoGpu() || IsEcoMode()) return;
        try
        {
            GpuControl?.Dispose();

            IGpuControl _gpuControl = new NvidiaGpuControl();

            if (_gpuControl.IsValid)
            {
                GpuControl = _gpuControl;
                Logger.WriteLine(GpuControl.FullName);
                return;
            }

            _gpuControl.Dispose();

            _gpuControl = new AmdGpuControl();
            if (_gpuControl.IsValid)
            {
                GpuControl = _gpuControl;
                if (GpuControl.FullName.Contains("6850M")) AppConfig.Set("xgm_special", 1);
                Logger.WriteLine(GpuControl.FullName);
                return;
            }
            _gpuControl.Dispose();

            Logger.WriteLine("dGPU not found");
            GpuControl = null;


        }
        catch (Exception ex)
        {
            Debug.WriteLine("Can't connect to GPU " + ex.ToString());
        }
    }


    public static void KillGPUApps()
    {

        List<string> tokill = new() { "EADesktop", "epicgameslauncher", "ASUSSmartDisplayControl" };

        foreach (string kill in tokill) ProcessHelper.KillByName(kill);

        if (AppConfig.Is("kill_gpu_apps") && GpuControl is not null)
        {
            GpuControl.KillGPUApps();
        }
    }

    public static void Dispose()
    {
        _cpuTempCounter?.Dispose();
        _cpuTempCounter = null;
    }
}
