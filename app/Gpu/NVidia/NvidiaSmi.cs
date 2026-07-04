using System.Diagnostics;
using System.Globalization;

namespace GHelper.Gpu.NVidia;

public readonly struct NvidiaPowerLimits
{
    public NvidiaPowerLimits(int minimum, int current, int defaultLimit, int maximum)
    {
        Minimum = minimum;
        Current = current;
        Default = defaultLimit;
        Maximum = maximum;
    }

    public int Minimum { get; }
    public int Current { get; }
    public int Default { get; }
    public int Maximum { get; }
}

public static class NvidiaSmi
{

    public static int GetDefaultMaxGPUPower()
    {
        if (AppConfig.ContainsModel("GU605") || AppConfig.ContainsModel("GA605")) return 125;
        if (AppConfig.ContainsModel("GA403")) return 90;
        if (AppConfig.ContainsModel("FA607")) return 140;
        else return 175;
    }

    public static int GetMaxGPUPower()
    {
        var limits = GetPowerLimits();
        if (limits.HasValue) return limits.Value.Maximum;

        return GetDefaultMaxGPUPower();
    }

    public static NvidiaPowerLimits? GetPowerLimits()
    {
        string output = RunNvidiaSmiCommand("--query-gpu=power.min_limit,power.limit,power.default_limit,power.max_limit --format=csv,noheader,nounits");
        string[] parts = output.Trim().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return null;

        int min = ParseWatts(parts[0]);
        int current = ParseWatts(parts[1]);
        int defaultLimit = ParseWatts(parts[2]);
        int max = ParseWatts(parts[3]);

        if (min <= 0 || max <= 0 || max < min) return null;
        if (current <= 0) current = defaultLimit > 0 ? defaultLimit : max;
        if (defaultLimit <= 0) defaultLimit = current;

        return new NvidiaPowerLimits(min, Math.Clamp(current, min, max), Math.Clamp(defaultLimit, min, max), max);
    }

    public static bool SetPowerLimit(int watts)
    {
        var limits = GetPowerLimits();
        if (limits.HasValue)
            watts = Math.Clamp(watts, limits.Value.Minimum, limits.Value.Maximum);

        string output = RunNvidiaSmiCommand($"-pl {watts}", out int exitCode);
        if (exitCode == 0)
        {
            Logger.WriteLine($"NVIDIA Power Limit: {watts}W OK");
            return true;
        }

        Logger.WriteLine($"NVIDIA Power Limit: {watts}W failed ({exitCode}) {output.Trim()}");
        return false;
    }

    private static int ParseWatts(string value)
    {
        value = value.Trim().Replace(',', '.');
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return (int)Math.Round(parsed);

        return -1;
    }

    private static string RunNvidiaSmiCommand(string arguments = "-i 0 -q")
    {
        return RunNvidiaSmiCommand(arguments, out _);
    }

    private static string RunNvidiaSmiCommand(string arguments, out int exitCode)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            exitCode = process.ExitCode;
            return output + error;
        }
        catch (Exception ex)
        {
            //return File.ReadAllText(@"smi.txt");
            Debug.WriteLine(ex.Message);
            exitCode = -1;
        }

        return "";

    }
}
