using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace OmenCore.Hardware.Calibration
{
    using OmenCore.Hardware.Adaptive;

    /// <summary>
    /// Texture-bound scene: 64 scattered texture samples per pixel from an
    /// 8192x8192 (256MB) texture. Bottlenecks L2 cache + texel fetch throughput.
    /// Models high-resolution texture streaming and texture-heavy rendering.
    /// </summary>
    public sealed class TextureBoundScene : PixelShaderSceneBase
    {
        private const string Shader = @"
Texture2D    bigTex : register(t0);
SamplerState samp   : register(s0);

struct VSOut {
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

// 64 scattered samples with deliberately cache-unfriendly offsets.
// The offsets span the whole texture so consecutive pixels hit
// different cache lines.
float4 MainPS(VSOut i) : SV_Target {
    float4 sum = 0;

    [loop]
    for (int s = 0; s < 64; s++) {
        // Pseudo-random offset per sample, scaled to span the texture.
        // The constants are irrational so consecutive pixels get different texels.
        float angle = s * 0.3927;  // ~pi/8
        float2 dir = float2(cos(angle), sin(angle));
        float2 offset = dir * (0.05 + 0.02 * s);
        sum += bigTex.Sample(samp, i.uv + offset);
    }

    return sum / 64.0;
}";

        private ID3D11PixelShader? _ps;
        private ID3D11Texture2D? _bigTex;
        private ID3D11ShaderResourceView? _bigSrv;
        private ID3D11SamplerState? _sampler;

        public override string Name => "Texture-bound";
        public override WorkloadClass TargetClass => WorkloadClass.MemBound;
        public override string Description => "64 scattered samples from a 256MB texture per pixel. Models texture streaming.";

        protected override void InitializeCore(ID3D11Device device, ID3D11DeviceContext context)
        {
            _ps = CompilePixelShader(device, Shader);

            // 8192x8192 BGRA8 = 256MB. Big enough to defeat any L2 cache
            // (typical L2 is 4-6MB on laptop GPUs).
            _bigTex = CreateNoiseTexture(device, 8192);
            _bigSrv = device.CreateShaderResourceView(_bigTex);

            var sampDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = 0
            };
            _sampler = device.CreateSamplerState(sampDesc);
        }

        public override void Bind(ID3D11DeviceContext context)
        {
            base.Bind(context);
            context.PSSetShader(_ps);
            context.PSSetShaderResource(0, _bigSrv);
            context.PSSetSampler(0, _sampler);
        }

        protected override void DisposeCore()
        {
            _sampler?.Dispose();
            _bigSrv?.Dispose();
            _bigTex?.Dispose();
            _ps?.Dispose();
        }
    }
}
