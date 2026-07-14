using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using GHelper.Helpers;
using Microsoft.Win32;

namespace GHelper
{
    internal static class StartupChecks
    {
        private const string PawnIoDownloadUrl = "https://pawnio.eu/";
        private const string PawnIoRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

        public static void Run()
        {
            try
            {
                CheckAdministrator();
                CheckPawnIO();
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"[StartupChecks] Error during startup checks: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void CheckAdministrator()
        {
            if (ProcessHelper.IsUserAdministrator())
            {
                Logger.WriteLine("[StartupChecks] Running as administrator ✓");
                return;
            }

            string dismissedKey = "startup_admin_check_dismissed";
            if (AppConfig.Get(dismissedKey) == 1)
                return;

            Logger.WriteLine("[StartupChecks] NOT running as administrator — HP WMI BIOS access will fail");

            DialogResult result = MessageBox.Show(
                "G-Helper is not running as administrator.\n\n" +
                "On HP OMEN / Victus laptops, administrator privileges are required for:\n" +
                "  • HP WMI BIOS access (fan control, performance modes, GPU MUX)\n" +
                "  • ACPI thermal zone reads (CPU temperature fallback)\n" +
                "  • Battery care mode toggles\n\n" +
                "Without admin rights, most OMEN-specific features will not work and " +
                "CPU temperature may report 0°C or stuck values.\n\n" +
                "Restart G-Helper as administrator now?",
                "G-Helper — Administrator Required",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);

            if (result == DialogResult.Yes)
            {
                try
                {
                    ProcessHelper.RunAsAdmin(force: true);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"[StartupChecks] Failed to relaunch as admin: {ex.Message}");
                    MessageBox.Show(
                        "Could not relaunch as administrator automatically. Please right-click " +
                        "G-Helper.exe and select \"Run as administrator\".",
                        "G-Helper",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            else
            {
                AppConfig.Set(dismissedKey, 1);
                Logger.WriteLine("[StartupChecks] Admin warning dismissed by user — won't show again");
            }
        }

        private static void CheckPawnIO()
        {
            bool installed = IsPawnIOInstalled();
            if (installed)
            {
                Logger.WriteLine("[StartupChecks] PawnIO driver installed ✓");
                return;
            }

            string dismissedKey = "startup_pawnio_check_dismissed";
            if (AppConfig.Get(dismissedKey) == 1)
                return;

            Logger.WriteLine("[StartupChecks] PawnIO driver NOT installed — EC/MSR/SMN features will be unavailable");

            DialogResult result = MessageBox.Show(
                "The PawnIO driver is not installed.\n\n" +
                "PawnIO is a free, signed, Secure-Boot-compatible driver that G-Helper uses for:\n" +
                "  • Ryzen CPU temperature (direct SMN register read — same method HP uses)\n" +
                "  • EC fan control (manual fan curves on OMEN laptops)\n" +
                "  • CPU power-limit writes (PL1/PL2 sliders)\n" +
                "  • CPU undervolting (Curve Optimizer on supported Ryzen)\n\n" +
                "Without PawnIO, G-Helper will still run but with reduced functionality — " +
                "CPU temperature may fall back to inaccurate ACPI thermal-zone readings.\n\n" +
                "Open the PawnIO download page in your browser?",
                "G-Helper — PawnIO Driver Recommended",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);

            if (result == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(PawnIoDownloadUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"[StartupChecks] Failed to open PawnIO URL: {ex.Message}");
                }
            }

            AppConfig.Set(dismissedKey, 1);
            Logger.WriteLine("[StartupChecks] PawnIO warning shown — won't show again");
        }

        private static bool IsPawnIOInstalled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PawnIoRegistryKey);
                if (key != null)
                {
                    Logger.WriteLine("[StartupChecks] PawnIO detected via registry uninstall key");
                    return true;
                }
            }
            catch { }

            try
            {
                string defaultDll = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "PawnIO", "PawnIOLib.dll");
                if (File.Exists(defaultDll))
                {
                    Logger.WriteLine($"[StartupChecks] PawnIO detected at {defaultDll}");
                    return true;
                }
            }
            catch { }

            try
            {
                string bundledDll = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "PawnIOLib.dll");
                if (File.Exists(bundledDll))
                {
                    Logger.WriteLine($"[StartupChecks] PawnIO detected (bundled) at {bundledDll}");
                    return true;
                }
            }
            catch { }

            return false;
        }
    }
}
