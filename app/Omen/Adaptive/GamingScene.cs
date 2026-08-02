using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace OmenCore.Hardware.Calibration
{
    using OmenCore.Hardware.Adaptive;

    /// <summary>
    /// Mixed gaming scene: BRDF lighting with 1 texture sample + ~30 ALU ops.
    /// Bottleneck is balanced between texture fetch and compute. Models
    /// typical AAA game workloads.
    /// </summary>
    public sealed class GamingScene : PixelShaderSceneBase
    {
        private const string Shader = @"
Texture2D    albedoTex : register(t0);
SamplerState samp      : register(s0);

struct VSOut {
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

// Diffuse + specular BRDF. 1 texture sample, ~30 ALU ops per pixel.
// Roughly models what a modern game fragment shader does at 4K.
float4 MainPS(VSOut i) : SV_Target {
    float3 albedo = albedoTex.Sample(samp, i.uv).rgb;
    float3 normal = normalize(float3(i.uv * 2 - 1, 1));
    float3 viewDir = float3(0, 0, 1);
    float3 color = albedo * 0.05; // ambient

    // Light 1 (Directional)
    float3 lightDir1 = normalize(float3(0.5, 0.7, 0.3));
    float  ndotl1    = max(0, dot(normal, lightDir1));
    float3 halfDir1  = normalize(lightDir1 + viewDir);
    float  ndoth1    = max(0, dot(normal, halfDir1));
    float  spec1     = pow(ndoth1, 64);
    color += (albedo * ndotl1 + float3(spec1, spec1, spec1)) * 0.8;

    // Light 2 (Point light orbiting based on UV)
    float3 lightPos = float3(sin(i.uv.x * 10), cos(i.uv.y * 10), 0.5);
    float3 lightDir2 = normalize(lightPos - float3(i.uv, 0));
    float  ndotl2    = max(0, dot(normal, lightDir2));
    float3 halfDir2  = normalize(lightDir2 + viewDir);
    float  ndoth2    = max(0, dot(normal, halfDir2));
    float  spec2     = pow(ndoth2, 32);
    color += (albedo * ndotl2 + float3(spec2, spec2, spec2)) * 0.5;

    // ACES Film Tone Mapping
    float a = 2.51f;
    float b = 0.03f;
    float c = 2.43f;
    float d = 0.59f;
    float e = 0.14f;
    color = saturate((color*(a*color+b))/(color*(c*color+d)+e));

    // Gamma
    color = pow(color, 1.0 / 2.2);

    return float4(color, 1);
}";

        private ID3D11PixelShader? _ps;
        private ID3D11Texture2D? _albedoTex;
        private ID3D11ShaderResourceView? _albedoSrv;
        private ID3D11SamplerState? _sampler;

        public override string Name => "Mixed Gaming";
        public override WorkloadClass TargetClass => WorkloadClass.Gaming;
        public override string Description => "BRDF lighting: 1 texture sample + ~30 ALU per pixel. Models AAA gaming.";

        protected override void InitializeCore(ID3D11Device device, ID3D11DeviceContext context)
        {
            _ps = CompilePixelShader(device, Shader);

            // 2048x2048 noise texture (~16MB) — enough to populate L2 but
            // small enough that it doesn't dominate the scene as texture-bound.
            _albedoTex = CreateNoiseTexture(device, 2048);
            _albedoSrv = device.CreateShaderResourceView(_albedoTex);

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
            context.PSSetShaderResource(0, _albedoSrv);
            context.PSSetSampler(0, _sampler);
        }

        protected override void DisposeCore()
        {
            _sampler?.Dispose();
            _albedoSrv?.Dispose();
            _albedoTex?.Dispose();
            _ps?.Dispose();
        }
    }
}
