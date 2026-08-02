using System;
using System.Runtime.InteropServices;

namespace OmenCore.Hardware.Calibration
{
    /// <summary>
    /// Creates a 1x1 message-only window for the D3D11 swap chain.
    /// The swap chain requires an HWND; we make it invisible and off-screen.
    /// </summary>
    internal static class Win32HiddenWindow
    {
        private const int HWND_MESSAGE = -3;
        private const uint WS_POPUP = 0x80000000;

        private static IntPtr _hwnd = IntPtr.Zero;
        private static readonly object _lock = new();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? moduleName);

        private const int SW_HIDE = 0;

        /// <summary>Create (or reuse) the hidden window. Thread-safe.</summary>
        public static IntPtr Create()
        {
            lock (_lock)
            {
                if (_hwnd != IntPtr.Zero) return _hwnd;

                // Use the static control class — always registered by Windows.
                // We just need an HWND for the swap chain; no input handling.
                IntPtr hInst = GetModuleHandleW(null);
                _hwnd = CreateWindowExW(
                    0,
                    "Static",
                    "GpuCalibrationWindow",
                    WS_POPUP,
                    -32000, -32000,  // off-screen
                    1, 1,
                    new IntPtr(HWND_MESSAGE),
                    IntPtr.Zero,
                    hInst,
                    IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    // Fallback: try without HWND_MESSAGE parent (won't be message-only)
                    _hwnd = CreateWindowExW(
                        0,
                        "Static",
                        "GpuCalibrationWindow",
                        WS_POPUP,
                        -32000, -32000,
                        1, 1,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        hInst,
                        IntPtr.Zero);
                    if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
                }

                return _hwnd;
            }
        }

        public static void Destroy()
        {
            lock (_lock)
            {
                if (_hwnd != IntPtr.Zero)
                {
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
            }
        }
    }
}
