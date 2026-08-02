using Vortice.Direct3D11;

namespace OmenCore.Hardware.Calibration
{
    using OmenCore.Hardware.Adaptive;

    /// <summary>
    /// ALU-bound scene: 512-deep FMA/Trig chain per pixel.
    /// Bottlenecks the compute units. Models ML training, software rendering,
    /// and other ALU-heavy workloads.
    /// </summary>
    public sealed class AluBoundScene : PixelShaderSceneBase
    {
        private const string Shader = @"
Texture2D<float4> NoiseTex : register(t0);
SamplerState Sampler       : register(s0);

struct VSOut {
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

// 512-deep chain. [loop] prevents compiler from unrolling and optimizing.
float4 MainPS(VSOut i) : SV_Target {
    float4 noise = NoiseTex.Sample(Sampler, i.uv);
    float x = i.uv.x + noise.x + 0.0001;
    float y = i.uv.y + noise.y + 0.0001;

    [loop]
    for (int k = 0; k < 512; k++) {
        x = mad(x, 1.0000017, y);
        y = mad(y, 0.9999983, -x);
        x = frac(x * 1.5) + sin(y);
        
        // Read noise periodically to force compiler to maintain loop state
        if ((k % 128) == 0) {
            y += NoiseTex.Sample(Sampler, float2(frac(x), frac(y))).r * 0.01;
        }
    }

    return float4(x, y, x * y, 1);
}";

        private ID3D11PixelShader? _ps;
        private ID3D11Texture2D? _noiseTex;
        private ID3D11ShaderResourceView? _noiseSrv;
        private ID3D11SamplerState? _sampler;

        public override string Name => "ALU-bound";
        public override WorkloadClass TargetClass => WorkloadClass.Compute;
        public override string Description => "512-deep math chain per pixel. Models ML training and software rendering.";

        protected override void InitializeCore(ID3D11Device device, ID3D11DeviceContext context)
        {
            _ps = CompilePixelShader(device, Shader);
            _noiseTex = CreateNoiseTexture(device, 512);
            _noiseSrv = device.CreateShaderResourceView(_noiseTex);
            
            var sampDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
            };
            _sampler = device.CreateSamplerState(sampDesc);
        }

        public override void Bind(ID3D11DeviceContext context)
        {
            base.Bind(context);
            context.PSSetShader(_ps);
            context.PSSetShaderResources(0, 1, new[] { _noiseSrv });
            context.PSSetSamplers(0, 1, new[] { _sampler });
        }

        protected override void DisposeCore()
        {
            _ps?.Dispose();
            _noiseSrv?.Dispose();
            _noiseTex?.Dispose();
            _sampler?.Dispose();
        }
    }
}
