using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using OmenCore.Models;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Ryzen System Management Unit (SMU) communication layer.
    /// Based on G-Helper/UXTU implementation, using PawnIO for Secure Boot compatibility.
    /// </summary>
    public sealed class RyzenSmu : IDisposable
    {
        public enum SmuStatus : int
        {
            Bad = 0x0,
            Ok = 0x1,
            Failed = 0xFF,
            UnknownCmd = 0xFE,
            CmdRejectedPrereq = 0xFD,
            CmdRejectedBusy = 0xFC
        }

        // SMU PCI configuration
        public uint SmuPciAddr { get; set; }
        public uint SmuOffsetAddr { get; set; }
        public uint SmuOffsetData { get; set; }

        // MP1 (Power Management) mailbox addresses
        public uint Mp1AddrMsg { get; set; }
        public uint Mp1AddrRsp { get; set; }
        public uint Mp1AddrArg { get; set; }

        // PSMU (Platform SMU) mailbox addresses  
        public uint PsmuAddrMsg { get; set; }
        public uint PsmuAddrRsp { get; set; }
        public uint PsmuAddrArg { get; set; }

        private readonly Mutex _smuMutex = new();
        private const ushort SmuTimeout = 8192;
        private bool _disposed;

        // PawnIO access
        private IntPtr _pawnIOLib = IntPtr.Zero;
        private IntPtr _handle = IntPtr.Zero;
        private bool _initialized;

        // PawnIO function delegates
        private delegate int PawnioOpen(out IntPtr handle);
        private delegate int PawnioExecute(IntPtr handle, string name, ulong[] input, IntPtr inSize, ulong[] output, IntPtr outSize, out IntPtr returnSize);
        private delegate int PawnioClose(IntPtr handle);

        private PawnioOpen? _pawnioOpen;
        private PawnioExecute? _pawnioExecute;
        private PawnioClose? _pawnioClose;

        public bool IsAvailable => _initialized && _handle != IntPtr.Zero;

        public RyzenSmu()
        {
            // Default PCI config for Ryzen
            SmuPciAddr = 0x00000000;
            SmuOffsetAddr = 0xB8;
            SmuOffsetData = 0xBC;
        }

        public bool Initialize()
{
    if (_initialized) return _handle != IntPtr.Zero;

    try
    {
        // 1. Resolve pathing directly in the root execution directory next to GHelper.exe
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string bundledLibPath = Path.Combine(appDir, "PawnIOLib.dll");
        string? libPath = null;

        if (File.Exists(bundledLibPath))
        {
            libPath = bundledLibPath;
        }
        else
        {
            // Fallback lookup if it's not present in the local application execution path
            string? pawnIOPath = FindPawnIOInstallation();
            if (pawnIOPath != null)
            {
                string installedLibPath = Path.Combine(pawnIOPath, "PawnIOLib.dll");
                if (File.Exists(installedLibPath))
                {
                    libPath = installedLibPath;
                }
            }
        }

        if (libPath == null) return false;

        // 2. Map unmanaged binary into process space
        _pawnIOLib = NativeMethods.LoadLibrary(libPath);
        if (_pawnIOLib == IntPtr.Zero) return false;

        // 3. Resolve native C++ function pointers
        IntPtr openPtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_open");
        IntPtr executePtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_execute");
        IntPtr closePtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_close");

        if (openPtr == IntPtr.Zero || executePtr == IntPtr.Zero || closePtr == IntPtr.Zero)
        {
            NativeMethods.FreeLibrary(_pawnIOLib);
            _pawnIOLib = IntPtr.Zero;
            return false;
        }

        // 4. Cast function pointers to actionable managed C# delegates
        _pawnioOpen = Marshal.GetDelegateForFunctionPointer<PawnioOpen>(openPtr);
        _pawnioExecute = Marshal.GetDelegateForFunctionPointer<PawnioExecute>(executePtr);
        _pawnioClose = Marshal.GetDelegateForFunctionPointer<PawnioClose>(closePtr);

        // 5. Open connection session to physical kernel device driver
        int hr = _pawnioOpen(out _handle);
        if (hr < 0 || _handle == IntPtr.Zero)
        {
            NativeMethods.FreeLibrary(_pawnIOLib);
            _pawnIOLib = IntPtr.Zero;
            _handle = IntPtr.Zero;
            _initialized = false;
            return false;
        }

        _initialized = true;
        return true;
    }
    catch
    {
        // Safe context collapse on unknown faults
        if (_pawnIOLib != IntPtr.Zero)
        {
            NativeMethods.FreeLibrary(_pawnIOLib);
            _pawnIOLib = IntPtr.Zero;
        }
        _handle = IntPtr.Zero;
        _initialized = false;
        return false;
    }
}

        private string? FindPawnIOInstallation()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (key != null)
                {
                    string? installLocation = key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                    {
                        return installLocation;
                    }
                }
            }
            catch { }

            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
            if (Directory.Exists(defaultPath)) return defaultPath;

            return null;
        }

        /// <summary>
        /// Send command to MP1 (Power Management) mailbox.
        /// </summary>
        public SmuStatus SendMp1(uint message, ref uint[] arguments)
        {
            return SendMsg(Mp1AddrMsg, Mp1AddrRsp, Mp1AddrArg, message, ref arguments);
        }

        /// <summary>
        /// Send command to PSMU (Platform SMU) mailbox.
        /// </summary>
        public SmuStatus SendPsmu(uint message, ref uint[] arguments)
        {
            return SendMsg(PsmuAddrMsg, PsmuAddrRsp, PsmuAddrArg, message, ref arguments);
        }

        private SmuStatus SendMsg(uint addrMsg, uint addrRsp, uint addrArg, uint msg, ref uint[] args)
        {
            if (!IsAvailable) return SmuStatus.Failed;

            ushort timeout = SmuTimeout;
            uint[] cmdArgs = new uint[6];
            int argsLength = Math.Min(args.Length, cmdArgs.Length);
            uint status = 0;

            for (int i = 0; i < argsLength; i++)
                cmdArgs[i] = args[i];

            if (!_smuMutex.WaitOne(5000))
                return SmuStatus.CmdRejectedBusy;

            try
            {
                // Clear response register
                bool success;
                do
                {
                    success = SmuWriteReg(addrRsp, 0);
                }
                while (!success && --timeout > 0);

                if (timeout == 0)
                {
                    SmuReadReg(addrRsp, ref status);
                    return (SmuStatus)status;
                }

                // Write arguments
                for (int i = 0; i < cmdArgs.Length; i++)
                    SmuWriteReg(addrArg + (uint)(i * 4), cmdArgs[i]);

                // Send message
                SmuWriteReg(addrMsg, msg);

                // Wait for completion
                if (!SmuWaitDone(addrRsp))
                {
                    SmuReadReg(addrRsp, ref status);
                    return (SmuStatus)status;
                }

                // Read back arguments
                for (int i = 0; i < args.Length; i++)
                    SmuReadReg(addrArg + (uint)(i * 4), ref args[i]);

                SmuReadReg(addrRsp, ref status);
                return (SmuStatus)status;
            }
            finally
            {
                _smuMutex.ReleaseMutex();
            }
        }

        private bool SmuWaitDone(uint addrRsp)
        {
            ushort timeout = SmuTimeout;
            uint data = 0;
            bool res;

            do
            {
                res = SmuReadReg(addrRsp, ref data);
            }
            while ((!res || data != 1) && --timeout > 0);

            return timeout > 0 && data == 1;
        }

        private bool SmuWriteReg(uint addr, uint data)
        {
            return WritePciConfigDword(SmuPciAddr, SmuOffsetAddr, addr) &&
                   WritePciConfigDword(SmuPciAddr, SmuOffsetData, data);
        }

        private bool SmuReadReg(uint addr, ref uint data)
        {
            if (!WritePciConfigDword(SmuPciAddr, SmuOffsetAddr, addr))
                return false;
            return ReadPciConfigDword(SmuPciAddr, SmuOffsetData, ref data);
        }

        /// <summary>
        /// Write to PCI configuration space using PawnIO.
        /// </summary>
        private bool WritePciConfigDword(uint pciAddr, uint regAddr, uint value)
        {
            if (!IsAvailable || _pawnioExecute == null) return false;

            try
            {
                // PawnIO PCI config write: bus, device, function, offset, value
                // PCI address 0x00000000 = bus 0, device 0, function 0
                uint bus = (pciAddr >> 8) & 0xFF;
                uint devFunc = pciAddr & 0xFF;
                uint device = devFunc >> 3;
                uint function = devFunc & 0x7;

                // Use PawnIO's PCI config write
                // Format: [bus, device, function, offset, value]
                ulong[] input = { bus, device, function, regAddr, value };
                ulong[] output = new ulong[1];

                int hr = _pawnioExecute(_handle, "ioctl_pci_write_config_dword", input, (IntPtr)5, output, (IntPtr)1, out _);
                return hr >= 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Read from PCI configuration space using PawnIO.
        /// </summary>
        private bool ReadPciConfigDword(uint pciAddr, uint regAddr, ref uint data)
        {
            if (!IsAvailable || _pawnioExecute == null) return false;

            try
            {
                uint bus = (pciAddr >> 8) & 0xFF;
                uint devFunc = pciAddr & 0xFF;
                uint device = devFunc >> 3;
                uint function = devFunc & 0x7;

                ulong[] input = { bus, device, function, regAddr };
                ulong[] output = new ulong[1];

                int hr = _pawnioExecute(_handle, "ioctl_pci_read_config_dword", input, (IntPtr)4, output, (IntPtr)1, out _);
                if (hr >= 0)
                {
                    data = (uint)output[0];
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _smuMutex.Dispose();

            if (_handle != IntPtr.Zero && _pawnioClose != null)
            {
                _pawnioClose(_handle);
                _handle = IntPtr.Zero;
            }

            if (_pawnIOLib != IntPtr.Zero)
            {
                NativeMethods.FreeLibrary(_pawnIOLib);
                _pawnIOLib = IntPtr.Zero;
            }

            _initialized = false;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool FreeLibrary(IntPtr hModule);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
            public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        }
    }
}
