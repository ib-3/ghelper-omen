using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Provides access to physical memory (MMIO) using the WinRing0 driver.
    /// Used for bypassing locked MSRs by writing directly to RAPL MMIO registers.
    /// </summary>
    public sealed class MmioAccess : IDisposable
    {
        private SafeFileHandle? _handle;
        private bool _disposed;

        public bool IsAvailable => _handle != null && !_handle.IsInvalid && !_handle.IsClosed;

        public bool Initialize(string devicePath = @"\\.\WinRing0_1_2_0")
        {
            _handle?.Dispose();
            _handle = Native.CreateFile(devicePath,
                Native.FILE_GENERIC_READ | Native.FILE_GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (!IsAvailable)
            {
                System.Diagnostics.Debug.WriteLine($"[MmioAccess] Failed to open device {devicePath}: {Marshal.GetLastWin32Error()}");
                return false;
            }

            return true;
        }

        public uint ReadMmio32(ulong address)
        {
            EnsureAvailable();

            var inBuf = new MemoryReadInput
            {
                Address = address,
                UnitSize = 4,   // dword
                Count = 1
            };
            var outBuf = new MemoryReadOutput { Value = 0 };

            bool ok = Native.DeviceIoControl(_handle!,
                Native.IOCTL_OLS_READ_MEMORY,
                ref inBuf, Marshal.SizeOf<MemoryReadInput>(),
                ref outBuf, Marshal.SizeOf<MemoryReadOutput>(),
                out _, IntPtr.Zero);

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"MMIO Read failed at 0x{address:X8}");
            }

            return outBuf.Value;
        }

        public void WriteMmio32(ulong address, uint value)
        {
            EnsureAvailable();

            var inBuf = new MemoryWriteInput
            {
                Address = address,
                UnitSize = 4,
                Count = 1,
                Value = value
            };

            bool ok = Native.DeviceIoControl(_handle!,
                Native.IOCTL_OLS_WRITE_MEMORY,
                ref inBuf, Marshal.SizeOf<MemoryWriteInput>(),
                IntPtr.Zero, 0,
                out _, IntPtr.Zero);

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"MMIO Write failed at 0x{address:X8}");
            }
        }

        /// <summary>
        /// Reads a 32-bit PCI config register. Needed to discover MCHBAR at B0/D0/F0 + 0x48.
        /// </summary>
        public uint ReadPciConfig(uint bus, uint device, uint function, uint register)
        {
            EnsureAvailable();

            // Pack BDF + register into the standard PCI_ADDRESS_REGISTER format used by WinRing0.
            uint pciAddress = 0x80000000u
                              | ((bus & 0xFF) << 16)
                              | ((device & 0x1F) << 11)
                              | ((function & 0x07) << 8)
                              | (register & 0xFC);

            var inBuf = new PciConfigInput { PciAddress = pciAddress };
            var outBuf = new PciConfigOutput { Value = 0 };

            bool ok = Native.DeviceIoControl(_handle!,
                Native.IOCTL_OLS_READ_PCI_CONFIG,
                ref inBuf, Marshal.SizeOf<PciConfigInput>(),
                ref outBuf, Marshal.SizeOf<PciConfigOutput>(),
                out _, IntPtr.Zero);

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"PCI Config Read failed at B{bus}/D{device}/F{function}+0x{register:X2}");
            }

            return outBuf.Value;
        }

        private void EnsureAvailable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MmioAccess));
            if (!IsAvailable) throw new InvalidOperationException("MMIO driver is not available");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _handle?.Dispose();
            _handle = null;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct MemoryReadInput
        {
            public ulong Address;
            public uint UnitSize;   // 1, 2, or 4
            public uint Count;      // number of units to read
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct MemoryReadOutput
        {
            public uint Value;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct MemoryWriteInput
        {
            public ulong Address;
            public uint UnitSize;
            public uint Count;
            public uint Value;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PciConfigInput
        {
            public uint PciAddress;  // 0x80000000 | (bus<<16) | (dev<<11) | (func<<8) | (reg & 0xFC)
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PciConfigOutput
        {
            public uint Value;
        }

        private static class Native
        {
            public const uint FILE_GENERIC_READ = 0x80000000;
            public const uint FILE_GENERIC_WRITE = 0x40000000;
            public const uint FILE_SHARE_READ = 0x00000001;
            public const uint FILE_SHARE_WRITE = 0x00000002;
            public const uint OPEN_EXISTING = 3;

            // Standard WinRing0 1.2.0 IOCTL codes. The previous values (0x9C4024D8 / 0x9C4024E4)
            // decoded to a non-standard device type (0x9C40) and did not match any published
            // WinRing0 build — every DeviceIoControl would have returned ERROR_INVALID_PARAMETER.
            public const uint IOCTL_OLS_READ_MEMORY     = 0x00222020;
            public const uint IOCTL_OLS_WRITE_MEMORY    = 0x00222024;
            public const uint IOCTL_OLS_READ_PCI_CONFIG = 0x00222018;

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
                ref MemoryReadInput inBuffer,
                int nInBufferSize,
                ref MemoryReadOutput outBuffer,
                int nOutBufferSize,
                out int bytesReturned,
                IntPtr overlapped);

            [DllImport("kernel32", SetLastError = true)]
            public static extern bool DeviceIoControl(
                SafeFileHandle hDevice,
                uint dwIoControlCode,
                ref MemoryWriteInput inBuffer,
                int nInBufferSize,
                IntPtr outBuffer,
                int nOutBufferSize,
                out int bytesReturned,
                IntPtr overlapped);

            [DllImport("kernel32", SetLastError = true)]
            public static extern bool DeviceIoControl(
                SafeFileHandle hDevice,
                uint dwIoControlCode,
                ref PciConfigInput inBuffer,
                int nInBufferSize,
                ref PciConfigOutput outBuffer,
                int nOutBufferSize,
                out int bytesReturned,
                IntPtr overlapped);
        }
    }
}
