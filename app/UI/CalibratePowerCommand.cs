using System;
using System.Windows.Forms;
using GHelper.UI;
using OmenCore.Hardware;

namespace GHelper.UI.Commands
{
    /// <summary>
    /// Sample handler for the "Calibrate..." button on the GPU tab.
    /// Adapt to your actual button/event names — this shows the pattern.
    /// </summary>
    public static class CalibratePowerCommand
    {
        public static async void Run(IWin32Window owner)
        {
            // 1. Pre-flight: controller must be initialized
            if (GpuPowerController.Instance == null)
            {
                MessageBox.Show(owner,
                    "GPU power controller is not initialized.",
                    "Cannot calibrate",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // 2. Confirm with the user — calibration takes ~5 minutes and
            //    locks the GPU clock during that time.
            var confirm = MessageBox.Show(owner,
                "GPU power calibration will run a 4-scene benchmark that takes about 5 minutes.\n\n" +
                "During calibration:\n" +
                "  • Your GPU will be locked at various clock speeds\n" +
                "  • The GPU may briefly hit its thermal limit\n" +
                "  • Other GPU applications may stutter\n\n" +
                "You don't need to run any external benchmark — calibration is fully automatic.\n\n" +
                "Continue?",
                "Confirm GPU calibration",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (confirm != DialogResult.Yes) return;

            // 3. Show the progress form (non-modal so we can await)
            using var form = new CalibrationProgressForm();
            form.Show(owner);

            try
            {
                // 4. Run calibration. The form's ProgressReporter is consumed
                //    by the calibrator; its CancellationToken handles cancel.
                var result = await GpuPowerController.RunCalibrationAsync(
                    form.ProgressReporter,
                    form.CancellationToken);

                // 5. Report outcome
                MessageBoxIcon icon = result.IsSuccess
                    ? MessageBoxIcon.Information
                    : result.Outcome == OmenCore.Hardware.Calibration.CalibrationOutcome.Cancelled
                        ? MessageBoxIcon.Warning
                        : MessageBoxIcon.Error;

                MessageBox.Show(owner,
                    result.Message + (result.IsSuccess
                        ? $"\n\nPoints collected: {result.PointsCollected}\nDuration: {result.Duration.TotalSeconds:F0}s"
                        : ""),
                    result.IsSuccess ? "Calibration complete" : "Calibration incomplete",
                    MessageBoxButtons.OK,
                    icon);
            }
            finally
            {
                if (!form.IsDisposed) 
                {
                    form.IsFinished = true;
                    form.Close();
                }
            }
        }
    }
}
