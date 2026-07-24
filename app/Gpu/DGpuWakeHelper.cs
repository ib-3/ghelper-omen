using System;
using System.Runtime.InteropServices;

namespace GHelper.Gpu
{
    public static class DGpuWakeHelper
    {
        [ComImport, Guid("7b7166ec-54cd-4e19-b228-3a1276ca2219"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
            int SetPrivateDataInterface(ref Guid Name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
            int GetParent(ref Guid riid, out IntPtr ppParent);
            int EnumAdapters(uint Adapter, out IntPtr ppAdapter);
            int MakeWindowAssociation(IntPtr WindowHandle, uint Flags);
            int GetWindowAssociation(out IntPtr pWindowHandle);
            int CreateSwapChain([MarshalAs(UnmanagedType.IUnknown)] object pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
            int CreateSoftwareAdapter(IntPtr Module, out IntPtr ppAdapter);
            int EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
            bool IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
            int SetPrivateDataInterface(ref Guid Name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
            int GetParent(ref Guid riid, out IntPtr ppParent);
            int EnumOutputs(uint Output, out IntPtr ppOutput);
            int GetDesc(out DXGI_ADAPTER_DESC pDesc);
            int CheckInterfaceSupport(ref Guid InterfaceName, out long pUMDVersion);
            int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public ulong Luid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public ulong Luid;
            public uint Flags;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IDXGIAdapter1 pAdapter,
            int driverType,
            IntPtr Software,
            uint Flags,
            IntPtr pFeatureLevels,
            uint FeatureLevels,
            uint SDKVersion,
            out IntPtr ppDevice,
            out int pFeatureLevel,
            out IntPtr ppImmediateContext);

        public static void ForceD0Hot()
        {
            try
            {
                Guid IID_IDXGIFactory1 = new Guid("7b7166ec-54cd-4e19-b228-3a1276ca2219");
                if (CreateDXGIFactory1(ref IID_IDXGIFactory1, out IDXGIFactory1 factory) == 0)
                {
                    uint adapterIndex = 0;
                    IDXGIAdapter1 dGpuAdapter = null;

                    while (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter) == 0)
                    {
                        if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc) == 0)
                        {
                            if ((desc.Flags & 2) != 0) // DXGI_ADAPTER_FLAG_SOFTWARE
                            {
                                adapterIndex++;
                                Marshal.ReleaseComObject(adapter);
                                continue;
                            }
                            if (desc.VendorId == 32902 || desc.VendorId == 0x8086) // Intel
                            {
                                adapterIndex++;
                                Marshal.ReleaseComObject(adapter);
                                continue;
                            }
                            if (desc.VendorId == 4098 || desc.VendorId == 0x1002) // AMD
                            {
                                adapterIndex++;
                                Marshal.ReleaseComObject(adapter);
                                continue;
                            }

                            dGpuAdapter = adapter;
                            break;
                        }
                        adapterIndex++;
                        Marshal.ReleaseComObject(adapter);
                    }

                    if (dGpuAdapter != null)
                    {
                        Logger.WriteLine("[DGpuWakeWatchdog] Attempting D3D11CreateDevice on NVIDIA adapter to force D0Hot...");
                        int hr = D3D11CreateDevice(dGpuAdapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7 /* D3D11_SDK_VERSION */, out IntPtr device, out _, out IntPtr context);
                        if (hr == 0)
                        {
                            Logger.WriteLine("[DGpuWakeWatchdog] D3D11CreateDevice succeeded! GPU should now be awake.");
                            if (context != IntPtr.Zero) Marshal.Release(context);
                            if (device != IntPtr.Zero) Marshal.Release(device);
                        }
                        else
                        {
                            Logger.WriteLine($"[DGpuWakeWatchdog] D3D11CreateDevice failed with code 0x{hr:X8}");
                        }
                        Marshal.ReleaseComObject(dGpuAdapter);
                    }
                    else
                    {
                        Logger.WriteLine("[DGpuWakeWatchdog] Could not find discrete NVIDIA adapter via DXGI.");
                    }

                    Marshal.ReleaseComObject(factory);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"[DGpuWakeWatchdog] Error in ForceD0Hot: {ex}");
            }
        }
    }
}
