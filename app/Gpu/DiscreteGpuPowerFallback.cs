using GHelper.Helpers;
using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace GHelper.Gpu;

public static class DiscreteGpuPowerFallback
{
    private const string FallbackGpuInstanceConfigKey = "gpu_fallback_instance";
    private const int CR_SUCCESS = 0;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
    private const uint CM_DRP_DEVICE_POWER_DATA = 0x0000001F;
    private const uint CM_PROB_DISABLED = 0x00000016;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint devInst, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_Property(
        uint devInst,
        uint property,
        out uint regDataType,
        byte[] buffer,
        ref uint length,
        uint flags);

    private static readonly string[] DiscreteGpuNameHints =
    {
        "NVIDIA", "GeForce", "RTX", "GTX", "Quadro",
        "Radeon RX", "Radeon Pro", "Arc A", "Arc Pro"
    };

    private static readonly string[] IntegratedGpuNameHints =
    {
        "Intel(R) UHD", "Intel(R) Iris", "Intel UHD", "Intel Iris",
        "Radeon Graphics", "Radeon(TM) Graphics", "AMD Radeon Graphics",
        "610M", "660M", "680M", "740M", "760M", "780M", "880M", "890M"
    };

    public static bool IsAvailable() => FindDiscreteGpuInstanceId(includeDisabled: true) != null;

    public static GpuPowerVerification VerifyEcoState(bool expectEco)
    {
        string? instanceId = AppConfig.GetString(FallbackGpuInstanceConfigKey, string.Empty);
        if (string.IsNullOrWhiteSpace(instanceId))
            instanceId = FindDiscreteGpuInstanceId(includeDisabled: true);

        if (string.IsNullOrWhiteSpace(instanceId))
            return new GpuPowerVerification(null, null, null, null, null, null, "No discrete GPU instance found");

        var wmi = ReadWmiState(instanceId);
        var cfg = ReadConfigManagerState(instanceId);
        var d3cold = ReadD3ColdRegistry(instanceId);

        string summary;
        if (expectEco)
        {
            if (wmi.ConfigManagerErrorCode == 22 || cfg.ProblemNumber == CM_PROB_DISABLED)
            {
                summary = $"dGPU disabled in Windows ({FormatPowerState(cfg.PowerState)}; {FormatD3Cold(d3cold)})";
            }
            else if (cfg.PowerState == DevicePowerState.D3)
            {
                summary = $"dGPU reports D3 ({FormatD3Cold(d3cold)})";
            }
            else if (wmi.Found && !string.Equals(wmi.Status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                summary = $"dGPU not OK: Status={wmi.Status}, Code={wmi.ConfigManagerErrorCode} ({FormatPowerState(cfg.PowerState)}; {FormatD3Cold(d3cold)})";
            }
            else
            {
                summary = $"dGPU still enabled/active-looking: Status={wmi.Status ?? "unknown"}, Code={wmi.ConfigManagerErrorCode?.ToString() ?? "unknown"}, {FormatPowerState(cfg.PowerState)} ({FormatD3Cold(d3cold)})";
            }
        }
        else
        {
            if (wmi.ConfigManagerErrorCode == 22 || cfg.ProblemNumber == CM_PROB_DISABLED)
                summary = $"dGPU still disabled after Standard request ({FormatPowerState(cfg.PowerState)})";
            else if (wmi.Found && string.Equals(wmi.Status, "OK", StringComparison.OrdinalIgnoreCase))
                summary = $"dGPU enabled: Status=OK ({FormatPowerState(cfg.PowerState)})";
            else
                summary = $"dGPU enable state unclear: Status={wmi.Status ?? "unknown"}, Code={wmi.ConfigManagerErrorCode?.ToString() ?? "unknown"}, {FormatPowerState(cfg.PowerState)}";
        }

        return new GpuPowerVerification(
            instanceId,
            wmi.Status,
            wmi.ConfigManagerErrorCode,
            cfg.ProblemNumber,
            cfg.PowerState,
            d3cold,
            summary);
    }

    public static bool TryEnterEco()
    {
        if (!ProcessHelper.IsUserAdministrator())
        {
            Logger.WriteLine("GPU fallback: admin rights required to disable dGPU");
            return false;
        }

        string? instanceId = FindDiscreteGpuInstanceId(includeDisabled: false);
        if (instanceId == null)
        {
            Logger.WriteLine("GPU fallback: no enabled discrete GPU found");
            return false;
        }

        AppConfig.Set(FallbackGpuInstanceConfigKey, instanceId);
        Logger.WriteLine($"GPU fallback: disabling dGPU {instanceId} (D3cold if platform supports it; D3hot otherwise)");
        return RunPnPUtil($"/disable-device \"{instanceId}\"");
    }

    public static bool TryLeaveEco()
    {
        if (!ProcessHelper.IsUserAdministrator())
        {
            Logger.WriteLine("GPU fallback: admin rights required to enable dGPU");
            return false;
        }

        string? instanceId = AppConfig.GetString(FallbackGpuInstanceConfigKey, string.Empty);
        if (string.IsNullOrWhiteSpace(instanceId))
            instanceId = FindDiscreteGpuInstanceId(includeDisabled: true);

        if (instanceId == null)
        {
            Logger.WriteLine("GPU fallback: no discrete GPU found to enable");
            return false;
        }

        Logger.WriteLine($"GPU fallback: enabling dGPU {instanceId}");
        bool enabled = RunPnPUtil($"/enable-device \"{instanceId}\"");
        bool restarted = RunPnPUtil($"/restart-device \"{instanceId}\"");
        return enabled || restarted;
    }

    private static string? FindDiscreteGpuInstanceId(bool includeDisabled)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, Status FROM Win32_VideoController");

            foreach (ManagementObject gpu in searcher.Get())
            {
                string name = gpu["Name"]?.ToString() ?? string.Empty;
                string instanceId = gpu["PNPDeviceID"]?.ToString() ?? string.Empty;
                string status = gpu["Status"]?.ToString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(instanceId)) continue;
                if (!includeDisabled && !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase)) continue;
                if (!LooksDiscrete(name)) continue;

                Logger.WriteLine($"GPU fallback: candidate {name} ({status})");
                return instanceId;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPU fallback: WMI GPU scan failed: {ex.Message}");
        }

        return null;
    }

    private static (bool Found, string? Status, uint? ConfigManagerErrorCode) ReadWmiState(string instanceId)
    {
        try
        {
            string escaped = instanceId.Replace("\\", "\\\\").Replace("'", "''");
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID = '{escaped}'");

            foreach (ManagementObject device in searcher.Get())
            {
                string? status = device["Status"]?.ToString();
                uint? code = device["ConfigManagerErrorCode"] is null
                    ? null
                    : Convert.ToUInt32(device["ConfigManagerErrorCode"]);
                return (true, status, code);
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPU verify: WMI state read failed: {ex.Message}");
        }

        return (false, null, null);
    }

    private static (uint? ProblemNumber, DevicePowerState? PowerState) ReadConfigManagerState(string instanceId)
    {
        try
        {
            if (LocateDevNode(instanceId, out uint devInst) != CR_SUCCESS)
                return (null, null);

            uint? problem = null;
            if (CM_Get_DevNode_Status(out _, out uint problemNumber, devInst, 0) == CR_SUCCESS)
                problem = problemNumber;

            uint length = 256;
            byte[] buffer = new byte[length];
            if (CM_Get_DevNode_Registry_Property(devInst, CM_DRP_DEVICE_POWER_DATA, out _, buffer, ref length, 0) == CR_SUCCESS &&
                length >= 8)
            {
                var state = (DevicePowerState)BitConverter.ToInt32(buffer, 4);
                return (problem, state);
            }

            return (problem, null);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPU verify: ConfigMgr state read failed: {ex.Message}");
            return (null, null);
        }
    }

    private static int LocateDevNode(string instanceId, out uint devInst)
    {
        int result = CM_Locate_DevNode(out devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
        if (result == CR_SUCCESS) return result;
        return CM_Locate_DevNode(out devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM);
    }

    private static D3ColdInfo ReadD3ColdRegistry(string instanceId)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters");

            return new D3ColdInfo(
                ReadOptionalDword(key, "D3ColdSupported"),
                ReadOptionalDword(key, "D3ColdEnabled"));
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GPU verify: D3Cold registry read failed: {ex.Message}");
            return new D3ColdInfo(null, null);
        }
    }

    private static bool? ReadOptionalDword(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name);
        if (value == null) return null;
        return Convert.ToInt32(value) != 0;
    }

    private static string FormatPowerState(DevicePowerState? state) =>
        state.HasValue ? $"PowerState={state.Value}" : "PowerState=unknown";

    private static string FormatD3Cold(D3ColdInfo info)
    {
        string supported = info.Supported.HasValue ? (info.Supported.Value ? "supported" : "not supported") : "support unknown";
        string enabled = info.Enabled.HasValue ? (info.Enabled.Value ? "enabled" : "disabled") : "enablement unknown";
        return $"D3cold {supported}, {enabled}";
    }

    private static bool LooksDiscrete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (IntegratedGpuNameHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            return false;

        return DiscreteGpuNameHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool RunPnPUtil(string args)
    {
        try
        {
            string output = ProcessHelper.RunCMD("pnputil", args);
            return output.Contains("success", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("enabled", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("restarted", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Logger.WriteLine($"GPU fallback: pnputil {args} failed: {ex.Message}");
            return false;
        }
    }
}

public record GpuPowerVerification(
    string? InstanceId,
    string? WmiStatus,
    uint? ConfigManagerErrorCode,
    uint? ConfigManagerProblemNumber,
    DevicePowerState? PowerState,
    D3ColdInfo? D3Cold,
    string Summary);

public record D3ColdInfo(bool? Supported, bool? Enabled);

public enum DevicePowerState
{
    Unspecified = 0,
    D0 = 1,
    D1 = 2,
    D2 = 3,
    D3 = 4,
    Maximum = 5
}
