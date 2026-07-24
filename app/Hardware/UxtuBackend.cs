using System;
using System.Diagnostics;

namespace OmenCore.Hardware
{
    public static class UxtuBackend
    {
        public static bool ApplyPowerLimits(int stapmW, int fastW, int slowW, int? tctlTemp = null, int? cHTCTemp = null, int? skinTemp = null)
        {
            var cliPath = UxtuDetection.FindRyzenAdj() ?? UxtuDetection.FindUxtuCli();
            if (cliPath == null) return false;

            try
            {
                // ryzenadj or uxtu-cli arguments are typically in milliWatts
                var stapmMw = stapmW * 1000;
                var fastMw = fastW * 1000;
                var slowMw = slowW * 1000;

                string args = $"--stapm-limit={stapmMw} --fast-limit={fastMw} --slow-limit={slowMw}";

                if (tctlTemp.HasValue) args += $" --tctl-temp={tctlTemp.Value}";
                if (cHTCTemp.HasValue) args += $" --cHTC-temp={cHTCTemp.Value}";
                if (skinTemp.HasValue) args += $" --apu-skin-temp={skinTemp.Value}";

                Logger.WriteLine($"[UxtuBackend] Running: \"{cliPath}\" {args}");

                var psi = new ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(5000); // 5 sec timeout
                    if (proc.ExitCode == 0)
                    {
                        Logger.WriteLine($"[UxtuBackend] Power limits applied successfully.");
                        return true;
                    }
                    else
                    {
                        var err = proc.StandardError.ReadToEnd();
                        Logger.WriteLine($"[UxtuBackend] Failed with exit code {proc.ExitCode}: {err}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"[UxtuBackend] Error executing CLI: {ex.Message}");
            }

            return false;
        }

        public static bool ApplyPreset(int mode, int? customLimitW = null, int? customTemp = null)
        {
            // Map GHelper mode to UXTU preset
            // 0 = Balanced, 1 = Turbo, 2 = Silent
            int tempLimit = 85;
            int powerW = 28;

            if (mode == 2) // Silent / Eco
            {
                tempLimit = 75;
                powerW = 15;
            }
            else if (mode == 1) // Turbo / Extreme
            {
                tempLimit = 95;
                powerW = 65; // Safe default for extreme if no custom limit
            }

            // Override with GHelper slider values if enabled
            if (customTemp.HasValue && customTemp.Value > 0)
                tempLimit = customTemp.Value;

            if (customLimitW.HasValue && customLimitW.Value > 0)
                powerW = customLimitW.Value;

            return ApplyPowerLimits(powerW, powerW, powerW, tempLimit, tempLimit, tempLimit);
        }

        public static bool ApplyCurveOptimizer(int offset)
        {
            var cliPath = UxtuDetection.FindRyzenAdj() ?? UxtuDetection.FindUxtuCli();
            if (cliPath == null) return false;

            try
            {
                // ryzenadj offsets: --set-coper=0x1000xx (xx = hex of offset) - it's complex.
                // Usually UXTU handles this differently. Let's just pass --set-coper if ryzenadj supports it, 
                // or just log that we rely on native code for UV.
                // Actually, standard ryzenadj CO is: --set-coper=0x001000XX where XX is the magnitude.
                // It's safer to only use UxtuBackend for power limits, and let native do UV if possible, or attempt basic UV here.
                
                // For simplicity as requested by the user, if we just need to set CO:
                // We will try --set-coper=VALUE
                // But wait, the standard command in UXTU for all cores is --set-coper=0x00100000 + (offset magnitude)
                // Let's rely on native for CO or just leave it out of this simple backend and return false so ModeControl falls back for UV.
                Logger.WriteLine($"[UxtuBackend] Curve Optimizer through CLI not fully implemented, falling back to native.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"[UxtuBackend] Error executing CLI: {ex.Message}");
            }

            return false;
        }
    }
}
