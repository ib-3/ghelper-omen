using System;
using System.Collections.Generic;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OmenCore.Hardware.Calibration
{
    /// <summary>
    /// Sets up a D3D11 device on the NVIDIA dGPU (not the Intel iGPU —
    /// critical on Optimus laptops) and runs a tight render loop while
    /// the calibrator samples power.
    /// </summary>
    public sealed class D3D11BenchmarkRunner : IDisposable
    {
        private IDXGIFactory2? _factory;
        private IDXGIAdapter1? _adapter;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private bool _disposed;

        /// <summary>
        /// Find the NVIDIA adapter by matching against the NVML device name.
        /// Falls back to any adapter whose description contains "NVIDIA" if
        /// the exact match fails.
        /// </summary>
        public bool Initialize(string nvmlGpuName, out string failureReason)
        {
            failureReason = "";

            try
            {
                _factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

                // Find NVIDIA adapter
                string? bestMatch = null;
                for (int i = 0; _factory.EnumAdapters1(i, out var adapter).Success; i++)
                {
                    var desc = adapter.Description1;
                    string name = desc.Description.TrimEnd('\0');

                    // Skip Microsoft Basic Render Driver and software adapters
                    if (desc.VendorId == 0x1414 && desc.DeviceId == 0x008C) { adapter.Dispose(); continue; }
                    if ((desc.Flags & AdapterFlags.Software) != 0) { adapter.Dispose(); continue; }

                    // VendorId 0x10DE = NVIDIA
                    if (desc.VendorId == 0x10DE)
                    {
                        if (_adapter != null) _adapter.Dispose(); // Dispose previous if any
                        bestMatch = name;
                        _adapter = adapter;
                        // If the description also matches NVML's name, we're certain
                        if (!string.IsNullOrEmpty(nvmlGpuName) &&
                            name.IndexOf(nvmlGpuName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            break;
                        }
                        // Otherwise keep this NVIDIA adapter but keep looking
                    }
                    else
                    {
                        adapter.Dispose();
                    }
                }

                if (_adapter == null)
                {
                    failureReason = "No NVIDIA adapter found. Calibration requires an NVIDIA dGPU.";
                    return false;
                }

                // Create D3D11 device on the NVIDIA adapter
                var featureLevels = new[]
                {
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0
                };

                D3D11.D3D11CreateDevice(
                    _adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.None,
                    featureLevels,
                    out _device,
                    out _,
                    out _context).CheckError();

                return true;
            }
            catch (Exception ex)
            {
                failureReason = "D3D11 init failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Initialize a scene once. Call before any RunSceneSampling calls.
        /// </summary>
        public void InitializeScene(ICalibrationScene scene)
        {
            if (_device == null || _context == null)
                throw new InvalidOperationException("Runner not initialized");
            scene.Initialize(_device, _context);
        }

        /// <summary>
        /// Run a scene at maximum throughput for the given duration, calling
        /// the sample callback at ~10 Hz so the calibrator can read power.
        /// The scene must already be initialized.
        /// </summary>
        public void RunSceneSampling(ICalibrationScene scene, TimeSpan duration, Action sampleCallback, System.Threading.CancellationToken token)
        {
            if (_device == null || _context == null)
                throw new InvalidOperationException("Runner not initialized");

            scene.Bind(_context);

            long startTick = Environment.TickCount64;
            int sampleIntervalMs = 100;  // sample at 10 Hz
            long nextSampleTick = startTick + sampleIntervalMs;

            while (true)
            {
                if (token.IsCancellationRequested) return;

                // Render frames as fast as possible
                scene.RenderFrame(_context);

                long now = Environment.TickCount64;
                if (now >= nextSampleTick)
                {
                    sampleCallback();
                    nextSampleTick = now + sampleIntervalMs;
                }

                if (now - startTick >= duration.TotalMilliseconds)
                    return;
            }
        }

        /// <summary>
        /// Unbind the current scene's resources. Call after the last
        /// RunSceneSampling for a scene, before initializing the next one.
        /// </summary>
        public void UnbindScene()
        {
            if (_context == null) return;
            _context.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
            _context.PSSetShader(null);
            _context.VSSetShader(null);
            _context.PSSetShaderResources(0, 0, null);
            _context.PSSetSamplers(0, 0, null);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _context?.Dispose();
            _device?.Dispose();
            _adapter?.Dispose();
            _factory?.Dispose();
        }

        ~D3D11BenchmarkRunner() => Dispose();
    }
}
