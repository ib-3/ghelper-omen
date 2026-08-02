using Vortice.Direct3D11;

namespace OmenCore.Hardware.Calibration
{
    using OmenCore.Hardware.Adaptive;

    /// <summary>
    /// Idle scene: just clears the render target once, then does nothing.
    /// Captures the GPU's static-leakage power at each clock. The GPU is
    /// kept awake by the 1x1 present in the runner, but no real work is done.
    /// </summary>
    public sealed class IdleScene : PixelShaderSceneBase
    {
        public override string Name => "Idle / Leakage";
        public override WorkloadClass TargetClass => WorkloadClass.Idle;
        public override string Description => "Measures static power draw at each clock (no workload).";

        protected override void InitializeCore(ID3D11Device device, ID3D11DeviceContext context)
        {
            // Nothing — no shader needed, we just don't draw.
        }

        public override void Bind(ID3D11DeviceContext context)
        {
            base.Bind(context);
            // Clear once, then leave alone. The render target stays the same
            // color for the entire sampling window — no actual GPU work.
            var rtvs = new ID3D11RenderTargetView[1];
            context.OMGetRenderTargets(1, rtvs, out var dsv);
            context.ClearRenderTargetView(
                rtvs[0],
                new Vortice.Mathematics.Color4(0.5f, 0.5f, 0.5f, 1.0f));
        }

        public override void RenderFrame(ID3D11DeviceContext context)
        {
            // Intentionally empty. We just want the GPU sitting at the locked
            // clock doing nothing, so we can measure leakage power.
            // A Draw(3,0) with a no-op shader would still burn ALU; we want
            // to isolate the "doing nothing" power floor.
        }

        protected override void DisposeCore() { }
    }
}
