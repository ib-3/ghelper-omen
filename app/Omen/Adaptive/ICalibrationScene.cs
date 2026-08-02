using Vortice.Direct3D11;

namespace OmenCore.Hardware.Calibration
{
    using OmenCore.Hardware.Adaptive;

    /// <summary>
    /// One benchmark scene that produces a specific workload pattern.
    /// The runner calls Initialize once, then RenderFrame many times during
    /// the sampling window. Each scene targets one WorkloadClass.
    /// </summary>
    public interface ICalibrationScene : IDisposable
    {
        /// <summary>Human-readable name for UI display.</summary>
        string Name { get; }

        /// <summary>Which workload class this scene characterizes.</summary>
        WorkloadClass TargetClass { get; }

        /// <summary>Short description shown in the UI.</summary>
        string Description { get; }

        /// <summary>
        /// Initialize scene-specific resources (shaders, textures, buffers).
        /// Called once before sampling begins.
        /// </summary>
        void Initialize(ID3D11Device device, ID3D11DeviceContext context);

        /// <summary>
        /// Render one frame. Called in a tight loop during the sampling window.
        /// Must be allocation-free after the first call.
        /// </summary>
        void RenderFrame(ID3D11DeviceContext context);

        /// <summary>
        /// Bind the scene's render target + shaders before RenderFrame is called.
        /// </summary>
        void Bind(ID3D11DeviceContext context);
    }
}
