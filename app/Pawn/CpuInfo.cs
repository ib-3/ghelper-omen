using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace PawnIO
{
    public static class CpuInfo
    {
        public static readonly bool IsAMD = DetectAMD();

        private static bool DetectAMD()
        {
            // (debug override removed)

            if (!X86Base.IsSupported) return false;
            var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);

            Span<uint> regs = stackalloc uint[] { (uint)ebx, (uint)edx, (uint)ecx };
            return MemoryMarshal.Cast<uint, byte>(regs).SequenceEqual("AuthenticAMD"u8);
        }


        private const string CpuRegKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        private static readonly Lazy<(string Name, string Caption)> _data =
            new Lazy<(string, string)>(Load, LazyThreadSafetyMode.ExecutionAndPublication);

        public static string Name    => _data.Value.Name;
        public static string Caption => _data.Value.Caption;

        // CPU undervolting / temperature limits.
        // Defaults match the original RyzenControl values; can be overridden via AppConfig.
        public static int MinCPUUV   => AppConfig.Get("min_uv",      -40);
        public static int MaxCPUUV   => AppConfig.Get("max_uv",        0);
        public static int MinIGPUUV  => AppConfig.Get("min_igpu_uv", -30);
        public static int MaxIGPUUV  => AppConfig.Get("max_igpu_uv",   0);
        public static int MinTemp    => AppConfig.Get("min_temp",     75);
        public static int DefaultTemp=> AppConfig.Get("max_temp",     96);

        public static bool IsSupportedUV()
        {
            if (!IsAMD) return true; // Intel CPUs (mostly HX/HK on Omen) use MSR, let the backend handle validation
            
            return Name.Contains("RYZEN AI MAX", StringComparison.OrdinalIgnoreCase) ||
                   Name.Contains("Ryzen AI 9", StringComparison.OrdinalIgnoreCase)   ||
                   Name.Contains("Ryzen 9", StringComparison.OrdinalIgnoreCase)      ||
                   Name.Contains("Ryzen 7", StringComparison.OrdinalIgnoreCase)      || // Allow Ryzen 7
                   Name.Contains("4900H", StringComparison.OrdinalIgnoreCase)        ||
                   Name.Contains("4800H", StringComparison.OrdinalIgnoreCase)        ||
                   Name.Contains("4600H", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSupportedUViGPU() => true; // Let backend handle if iGPU UV is supported

        private static (string Name, string Caption) Load()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(CpuRegKey);
                if (key is not null)
                {
                    var name = key.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? string.Empty;
                    var caption = key.GetValue("Identifier")?.ToString()?.Trim() ?? string.Empty;
                    return (name, caption);
                }
            }
            catch { }

            return (string.Empty, string.Empty);
        }
    }
}
