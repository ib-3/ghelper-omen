namespace OmenCore.Hardware.Adaptive
{
    /// <summary>
    /// Coarse classification of the current GPU workload.
    /// Used to key per-workload feedforward maps so the controller
    /// can pick a sensible starting clock for the current activity.
    /// </summary>
    public enum WorkloadClass
    {
        /// <summary>
        /// High variance or classifier not yet primed.
        /// Do NOT learn in this state; fall back to PI only.
        /// </summary>
        Transient = 0,

        /// <summary>Desktop, idle, background tasks. gpu&lt;5%, mem&lt;5%.</summary>
        Idle = 1,

        /// <summary>Video playback, light 2D, web. gpu&lt;30%, mem&lt;30%.</summary>
        Light = 2,

        /// <summary>Typical 3D gaming. gpu&gt;50% with moderate mem util.</summary>
        Gaming = 3,

        /// <summary>ML / rendering / compute shaders. gpu&gt;80%, mem&lt;30%.</summary>
        Compute = 4,

        /// <summary>Texture streaming, video encode. mem&gt;70%, gpu&lt;50%.</summary>
        MemBound = 5
    }

    public static class WorkloadClassExtensions
    {
        public static string ToLogString(this WorkloadClass cls)
        {
            return cls switch
            {
                WorkloadClass.Transient => "transient",
                WorkloadClass.Idle      => "idle",
                WorkloadClass.Light     => "light",
                WorkloadClass.Gaming    => "gaming",
                WorkloadClass.Compute   => "compute",
                WorkloadClass.MemBound  => "membound",
                _                       => "unknown"
            };
        }
    }
}
