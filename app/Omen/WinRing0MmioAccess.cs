using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OmenCore.Hardware
{
    /// <summary>
    /// WinRing0 (1.2.0) driver wrapper for physical-memory MMIO and PCI config access.
    ///
    /// AUDIT-FIX SUMMARY (see audit report §17, §22):
    ///   1. Removed dead IOCTL constants (IOCTL_OLS_READ_MEMORY_DWORD, IOCTL_OLS_WRITE_MEMORY_DWORD).
    ///   2. Added IOCTL_OLS_READ_PCI_CONFIG so callers can read MCHBAR from B0:D0:F0+0x48 dynamically.
    ///   3. ReadMmioDword / WriteMmioDword now log IOCTL failures with GetLastError instead of
    ///      silently returning 0. Callers can distinguish "real 0" from "IOCTL failed" via the new
    ///      TryReadMmioDword / TryWriteMmioDword methods.
    ///   4. Added ValidateMchbarAddress() — rejects physical addresses outside the expected
    ///      MCHBAR window (0xFED0_0000 – 0xFED2_0000 on client platforms) to prevent accidental
    ///      writes to arbitrary physical memory.
    ///   5. Moved OlsWriteMemoryInput struct declaration above the method that uses it.
    /// </summary>
    public class WinRing0MmioAccess : IMmioAccess, IDisposable
    {
        // ─────────────────────────────────────────────────────────────────────────────
        // WinRing0 IOCTL codes  (device type = 0x9C40 for this fork; see audit §17)
        //
        //   CTL_CODE(DeviceType, Function, Method, Access)
        //     = (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
        //
        //   READ_PCI_CONFIG  = CTL_CODE(0x9C40, 0x831, METHOD_BUFFERED, FILE_READ_ACCESS)
        //                    = 0x9C4060C4
        //   READ_MEMORY      = CTL_CODE(0x9C40, 0x841, METHOD_BUFFERED, FILE_READ_ACCESS)
        //                    = 0x9C406104
        //   WRITE_MEMORY     = CTL_CODE(0x9C40, 0x842, METHOD_BUFFERED, FILE_WRITE_ACCESS)
        //                    = 0x9C40A108
        // ─────────────────────────────────────────────────────────────────────────────
        private const uint IOCTL_OLS_READ_PCI_CONFIG = 0x9C4060C4;
        private const uint IOCTL_OLS_READ_MEMORY     = 0x9C406104;
        private const uint IOCTL_OLS_WRITE_MEMORY    = 0x9C40A108;

        // Expected MCHBAR physical-address window on Intel client platforms
        // (Skylake / Kaby Lake / Coffee Lake / Comet Lake). Reject anything outside
        // this range to prevent accidental writes to arbitrary physical memory.
        private const ulong MCHBAR_WINDOW_LO = 0xFED0_0000UL;
        private const ulong MCHBAR_WINDOW_HI = 0xFED2_0000UL;
        private const uint  MCHBAR_WINDOW_MAX_SPAN = 0x0001_0000;   // 64 KB

        private SafeFileHandle? _handle;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct OlsMemoryInput
        {
            public ulong Address;
            public uint  UnitSize;
            public uint  Count;
        }

        // Declared BEFORE methods that use it (audit §22 fix #5)
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct OlsWriteMemoryInput
        {
            public ulong Address;
            public uint  UnitSize;
            public uint  Count;
            public uint  Value;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct OlsPciConfigInput
        {
            public uint PciAddress;   // OLS_PCI_ADDRESS encoding
            public uint RegOffset;
        }

        private static class Native
        {
            [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern SafeFileHandle CreateFile(
                string lpFileName,
                uint dwDesiredAccess,
                uint dwShareMode,
                IntPtr lpSecurityAttributes,
                uint dwCreationDisposition,
                uint dwFlagsAndAttributes,
                IntPtr hTemplateFile);

            [DllImport("kernel32", SetLastError = true)]
            public static extern bool DeviceIoControl(
                SafeFileHandle hDevice,
                uint dwIoControlCode,
                ref OlsMemoryInput inBuffer,
                int nInBufferSize,
                ref uint outBuffer,
                int nOutBufferSize,
                out int bytesReturned,
                IntPtr overlapped);

            [DllImport("kernel32", SetLastError = true)]
            public static extern bool DeviceIoControl(
                SafeFileHandle hDevice,
                uint dwIoControlCode,
                ref OlsWriteMemoryInput inBuffer,
                int nInBufferSize,
                IntPtr outBuffer,
                int nOutBufferSize,
                out int bytesReturned,
                IntPtr overlapped);

            [DllImport("kernel32", SetLastError = true)]
            public static extern bool DeviceIoControl(
                SafeFileHandle hDevice,
                uint dwIoControlCode,
                ref OlsPciConfigInput inBuffer,
                int nInBufferSize,
                ref uint outBuffer,
                int nOutBufferSize,
                out int bytesReturned,
                IntPtr overlapped);
        }

        public WinRing0MmioAccess()
        {
            try
            {
                _handle = Native.CreateFile(
                    @"\\.\WinRing0_1_2_0",
                    0xC0000000,           // GENERIC_READ | GENERIC_WRITE
                    3,                    // FILE_SHARE_READ | FILE_SHARE_WRITE
                    IntPtr.Zero,
                    3,                    // OPEN_EXISTING
                    0,
                    IntPtr.Zero);

                if (_handle == null || _handle.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    _handle = null;
                    Log($"CreateFile failed (Win32Error={err}). Driver not loaded or access denied.");
                }
            }
            catch (Exception ex)
            {
                _handle = null;
                Log($"Constructor exception: {ex.Message}");
            }
        }

        public bool IsAvailable => _handle != null && !_handle.IsInvalid;

        // ─────────────────────────────────────────────────────────────────────────────
        // Address validation — audit §15 (security) and §22 (fix #4)
        // ─────────────────────────────────────────────────────────────────────────────
        private static bool ValidateMchbarAddress(ulong address)
        {
            if (address < MCHBAR_WINDOW_LO || address >= MCHBAR_WINDOW_HI)
            {
                Log($"Rejecting MMIO address 0x{address:X} — outside MCHBAR window "
                    + $"[0x{MCHBAR_WINDOW_LO:X}–0x{MCHBAR_WINDOW_HI:X}).");
                return false;
            }
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // PCI config space read — needed for dynamic MCHBAR detection (audit §8, §22)
        // ─────────────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Read a 32-bit PCI config register.
        /// OLS_PCI_ADDRESS encoding (WinRing0):  bits 31:16 bus+device+function, bits 7:0 register.
        ///   PciAddress = (Bus << 16) | (Device << 11) | (Function << 8) | (RegOffset & 0xFC)
        /// For MCHBAR (B0:D0:F0 offset 0x48), call:  ReadPciConfigDword(0, 0, 0, 0x48)
        /// </summary>
        public uint ReadPciConfigDword(int bus, int device, int function, uint regOffset)
        {
            if (!IsAvailable) return 0;

            uint pciAddress = ((uint)bus << 16)
                            | ((uint)device << 11)
                            | ((uint)function << 8)
                            | (regOffset & 0xFCu);

            var input = new OlsPciConfigInput
            {
                PciAddress = pciAddress,
                RegOffset  = regOffset & 0xFCu,
            };

            uint value = 0;
            bool ok = Native.DeviceIoControl(
                _handle!,
                IOCTL_OLS_READ_PCI_CONFIG,
                ref input,
                Marshal.SizeOf<OlsPciConfigInput>(),
                ref value,
                4,
                out int bytesReturned,
                IntPtr.Zero);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                Log($"ReadPciConfigDword IOCTL failed (Win32Error={err}) "
                    + $"B{bus}:D{device}:F{function}+0x{regOffset:X2}");
                return 0;
            }
            return value;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // MMIO read/write  — now logs IOCTL failures instead of silently returning 0
        // ─────────────────────────────────────────────────────────────────────────────
        public uint ReadMmioDword(ulong address)
        {
            if (!IsAvailable) return 0;
            if (!ValidateMchbarAddress(address)) return 0;

            var input = new OlsMemoryInput
            {
                Address  = address,
                UnitSize = 4,
                Count    = 1,
            };

            uint value = 0;
            bool ok = Native.DeviceIoControl(
                _handle!,
                IOCTL_OLS_READ_MEMORY,
                ref input,
                Marshal.SizeOf<OlsMemoryInput>(),
                ref value,
                4,
                out int bytesReturned,
                IntPtr.Zero);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                Log($"ReadMmioDword IOCTL failed (Win32Error={err}) @ 0x{address:X}");
                return 0;
            }
            return value;
        }

        public void WriteMmioDword(ulong address, uint value)
        {
            if (!IsAvailable) return;
            if (!ValidateMchbarAddress(address)) return;

            var input = new OlsWriteMemoryInput
            {
                Address  = address,
                UnitSize = 4,
                Count    = 1,
                Value    = value,
            };

            bool ok = Native.DeviceIoControl(
                _handle!,
                IOCTL_OLS_WRITE_MEMORY,
                ref input,
                Marshal.SizeOf<OlsWriteMemoryInput>(),
                IntPtr.Zero,
                0,
                out int bytesReturned,
                IntPtr.Zero);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                Log($"WriteMmioDword IOCTL failed (Win32Error={err}) @ 0x{address:X} = 0x{value:X8}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // TryGet variants — let callers distinguish "real 0" from "IOCTL failed"
        // (audit §6 — Initialize() cascades failure into bad math if return value is
        //  blindly trusted as "0 means register is 0")
        // ─────────────────────────────────────────────────────────────────────────────
        public bool TryReadMmioDword(ulong address, out uint value)
        {
            value = 0;
            if (!IsAvailable) return false;
            if (!ValidateMchbarAddress(address)) return false;

            var input = new OlsMemoryInput { Address = address, UnitSize = 4, Count = 1 };
            bool ok = Native.DeviceIoControl(
                _handle!,
                IOCTL_OLS_READ_MEMORY,
                ref input,
                Marshal.SizeOf<OlsMemoryInput>(),
                ref value,
                4,
                out _,
                IntPtr.Zero);
            return ok;
        }

        public bool TryWriteMmioDword(ulong address, uint value)
        {
            if (!IsAvailable) return false;
            if (!ValidateMchbarAddress(address)) return false;

            var input = new OlsWriteMemoryInput { Address = address, UnitSize = 4, Count = 1, Value = value };
            return Native.DeviceIoControl(
                _handle!,
                IOCTL_OLS_WRITE_MEMORY,
                ref input,
                Marshal.SizeOf<OlsWriteMemoryInput>(),
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
        }

        public void Dispose()
        {
            if (_handle != null && !_handle.IsInvalid)
            {
                _handle.Dispose();
                _handle = null;
            }
        }

        private static void Log(string msg)
        {
            try { Logger.WriteLine($"[WinRing0MmioAccess] {msg}"); }
            catch { System.Diagnostics.Debug.WriteLine($"[WinRing0MmioAccess] {msg}"); }
        }
    }
}
