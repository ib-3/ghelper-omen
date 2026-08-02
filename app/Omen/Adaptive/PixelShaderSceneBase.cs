using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OmenCore.Hardware.Calibration
{
    using OmenCore.Hardware.Adaptive;

    /// <summary>
    /// Base class for pixel-shader-based calibration scenes. Owns the 4K
    /// render target and a shared fullscreen-triangle vertex shader.
    /// Derived classes only need to provide a pixel shader and any
    /// textures they need.
    /// </summary>
    public abstract class PixelShaderSceneBase : ICalibrationScene
    {
        protected const int RenderWidth  = 3840;
        protected const int RenderHeight = 2160;
        protected const Format RenderFormat = Format.R8G8B8A8_UNorm;

        // Shared fullscreen-triangle vertex shader. Generates a triangle
        // that covers the whole screen from SV_VertexID alone, so we don't
        // need a vertex buffer.
        private const string FullscreenVS = @"
struct VSOut {
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOut MainVS(uint id : SV_VertexID) {
    VSOut o;
    float2 positions[3] = {
        float2(-1, -1),
        float2( 3, -1),
        float2(-1,  3)
    };
    float2 uvs[3] = {
        float2(0, 1),
        float2(2, 1),
        float2(0, -1)
    };
    o.pos = float4(positions[id], 0, 1);
    o.uv  = uvs[id];
    return o;
}";

        private ID3D11Texture2D? _renderTargetTex;
        private ID3D11Texture2D? _dummyStagingTex;
        private ID3D11RenderTargetView? _rtv;
        private ID3D11VertexShader? _vs;
        private Viewport _viewport;
        private bool _disposed;

        public abstract string Name { get; }
        public abstract WorkloadClass TargetClass { get; }
        public abstract string Description { get; }

        public void Initialize(ID3D11Device device, ID3D11DeviceContext context)
        {
            var desc = new Texture2DDescription
            {
                Width  = RenderWidth,
                Height = RenderHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = RenderFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None
            };
            _renderTargetTex = device.CreateTexture2D(desc);
            
            var stagingDesc = new Texture2DDescription
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = RenderFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            };
            _dummyStagingTex = device.CreateTexture2D(stagingDesc);
            
            _rtv = device.CreateRenderTargetView(_renderTargetTex);
            _viewport = new Viewport(0, 0, RenderWidth, RenderHeight);

            // Compile shared fullscreen VS
            var bytecode = Vortice.D3DCompiler.Compiler.Compile(
                FullscreenVS, "MainVS", string.Empty, "vs_5_0");
            _vs = device.CreateVertexShader(bytecode.Span);

            InitializeCore(device, context);
        }

        protected abstract void InitializeCore(ID3D11Device device, ID3D11DeviceContext context);

        public virtual void Bind(ID3D11DeviceContext context)
        {
            context.VSSetShader(_vs);
            context.OMSetRenderTargets(_rtv, null);
            context.RSSetViewport(_viewport);
            // Clear once per bind phase — the inner draw loop skips the clear
            // to maximize shader work per frame.
        }

        public virtual void RenderFrame(ID3D11DeviceContext context)
        {
            // Default implementation: draw the fullscreen triangle.
            // Scenes can override if they need custom draw logic.
            context.Draw(3, 0);
            
            // Force the GPU to execute the draw call by copying a pixel to a staging texture
            if (_dummyStagingTex != null && _renderTargetTex != null)
            {
                context.CopySubresourceRegion(_dummyStagingTex, 0, 0, 0, 0, _renderTargetTex, 0, new Box(0, 0, 0, 1, 1, 1));
            }
        }

        protected static ID3D11PixelShader CompilePixelShader(ID3D11Device device, string source, string entryPoint = "MainPS")
        {
            var bytecode = Vortice.D3DCompiler.Compiler.Compile(
                source, entryPoint, string.Empty, "ps_5_0");
            return device.CreatePixelShader(bytecode.Span);
        }

        protected static ID3D11Texture2D CreateNoiseTexture(ID3D11Device device, int size)
        {
            var rand = new Random(42);  // deterministic
            byte[] data = new byte[size * size * 4];
            rand.NextBytes(data);

            var desc = new Texture2DDescription
            {
                Width = size,
                Height = size,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None
            };

            var texture = device.CreateTexture2D(desc);
            var dataBox = new SubresourceData
            {
                DataPointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length),
                RowPitch = size * 4
            };
            System.Runtime.InteropServices.Marshal.Copy(data, 0, dataBox.DataPointer, data.Length);
            try
            {
                device.ImmediateContext.UpdateSubresource(texture, 0, null, dataBox.DataPointer, size * 4, data.Length);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(dataBox.DataPointer);
            }
            return texture;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeCore();
            _vs?.Dispose();
            _rtv?.Dispose();
            _renderTargetTex?.Dispose();
            _dummyStagingTex?.Dispose();
            GC.SuppressFinalize(this);
        }

        protected abstract void DisposeCore();

        ~PixelShaderSceneBase() => Dispose();
    }
}
