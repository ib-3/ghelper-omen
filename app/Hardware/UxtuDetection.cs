using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace OmenCore.Hardware
{
    public static class UxtuDetection
    {
        public static string? CachedUxtuCliPath { get; private set; }
        public static string? CachedRyzenAdjPath { get; private set; }

        public static bool IsInstalled()
        {
            return FindUxtuCli() != null || FindRyzenAdj() != null;
        }

        public static string? GetUxtuCliPath()
        {
            return FindUxtuCli() ?? FindRyzenAdj();
        }

        public static string? GetUxtuExePath()
        {
            return SearchForExecutable("Universal x86 Tuning Utility.exe");
        }

        public static string? FindUxtuCli()
        {
            if (CachedUxtuCliPath != null && File.Exists(CachedUxtuCliPath))
                return CachedUxtuCliPath;

            CachedUxtuCliPath = SearchForExecutable("uxtu-cli.exe");
            return CachedUxtuCliPath;
        }

        public static string? FindRyzenAdj()
        {
            if (CachedRyzenAdjPath != null && File.Exists(CachedRyzenAdjPath))
                return CachedRyzenAdjPath;

            CachedRyzenAdjPath = SearchForExecutable("ryzenadj.exe");
            return CachedRyzenAdjPath;
        }

        private static string? SearchForExecutable(string exeName)
        {
            // 1. Check current directory
            if (File.Exists(exeName)) return Path.GetFullPath(exeName);

            // 2. Check common installation paths
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            var pathsToCheck = new[]
            {
                Path.Combine(localAppData, "Universal x86 Tuning Utility", "bin", exeName),
                Path.Combine(localAppData, "Universal x86 Tuning Utility", exeName),
                Path.Combine(programFiles, "Universal x86 Tuning Utility", "bin", exeName),
                Path.Combine(programFiles, "Universal x86 Tuning Utility", exeName),
                Path.Combine(programFilesX86, "Universal x86 Tuning Utility", "bin", exeName),
                Path.Combine(programFilesX86, "Universal x86 Tuning Utility", exeName)
            };

            foreach (var path in pathsToCheck)
            {
                if (File.Exists(path))
                {
                    Logger.WriteLine($"[UxtuDetection] Found {exeName} at: {path}");
                    return path;
                }
            }

            // 3. Check registry for uninstall keys
            string[] registryKeys = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var keyPath in registryKeys)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                    if (key != null)
                    {
                        foreach (var subKeyName in key.GetSubKeyNames())
                        {
                            using var subKey = key.OpenSubKey(subKeyName);
                            if (subKey != null)
                            {
                                var displayName = subKey.GetValue("DisplayName")?.ToString();
                                if (displayName != null && displayName.Contains("Universal x86 Tuning Utility", StringComparison.OrdinalIgnoreCase))
                                {
                                    var installLocation = subKey.GetValue("InstallLocation")?.ToString();
                                    if (!string.IsNullOrEmpty(installLocation))
                                    {
                                        var cliPath1 = Path.Combine(installLocation, "bin", exeName);
                                        var cliPath2 = Path.Combine(installLocation, exeName);

                                        if (File.Exists(cliPath1)) return cliPath1;
                                        if (File.Exists(cliPath2)) return cliPath2;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            
            // Check current user registry
            try
            {
                using var hkcu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (hkcu != null)
                {
                    foreach (var subKeyName in hkcu.GetSubKeyNames())
                    {
                        using var subKey = hkcu.OpenSubKey(subKeyName);
                        if (subKey != null)
                        {
                            var displayName = subKey.GetValue("DisplayName")?.ToString();
                            if (displayName != null && displayName.Contains("Universal x86 Tuning Utility", StringComparison.OrdinalIgnoreCase))
                            {
                                var installLocation = subKey.GetValue("InstallLocation")?.ToString();
                                if (!string.IsNullOrEmpty(installLocation))
                                {
                                    var cliPath1 = Path.Combine(installLocation, "bin", exeName);
                                    var cliPath2 = Path.Combine(installLocation, exeName);

                                    if (File.Exists(cliPath1)) return cliPath1;
                                    if (File.Exists(cliPath2)) return cliPath2;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }
        
        public static void ClearCache()
        {
            CachedUxtuCliPath = null;
            CachedRyzenAdjPath = null;
        }
    }
}
