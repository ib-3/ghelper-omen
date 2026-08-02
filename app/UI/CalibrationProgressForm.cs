using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using OmenCore.Hardware.Calibration;

namespace GHelper.UI
{
    /// <summary>
    /// Modal dialog showing live calibration progress with cancel support.
    /// Reads from IProgress&lt;CalibrationProgress&gt; reports.
    /// </summary>
    public class CalibrationProgressForm : Form
    {
        private readonly Label lblScene;
        private readonly Label lblPhase;
        private readonly ProgressBar progressBarOverall;
        private readonly ProgressBar progressBarScene;
        private readonly Label lblClock;
        private readonly Label lblPower;
        private readonly Label lblTemp;
        private readonly Label lblUtil;
        private readonly Button btnCancel;

        private readonly CancellationTokenSource _cts = new();
        private int _lastSceneIndex = -1;

        public CancellationToken CancellationToken => _cts.Token;
        public bool IsCancelled { get; private set; }
        public bool IsFinished { get; set; }

        public IProgress<CalibrationProgress> ProgressReporter { get; }

        public CalibrationProgressForm()
        {
            this.Text = "GPU Power Calibration";
            this.Size = new Size(480, 320);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ControlBox = true;
            this.FormClosing += OnFormClosing;

            int y = 15;

            lblScene = new Label
            {
                Text = "Initializing...",
                AutoSize = true,
                Location = new Point(20, y),
                Font = new Font("Segoe UI", 11, FontStyle.Bold)
            };
            this.Controls.Add(lblScene);
            y += 28;

            lblPhase = new Label
            {
                Text = "",
                AutoSize = true,
                Location = new Point(20, y),
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.Gray
            };
            this.Controls.Add(lblPhase);
            y += 22;

            // Overall progress
            var lblOverall = new Label
            {
                Text = "Overall",
                AutoSize = true,
                Location = new Point(20, y),
                Font = new Font("Segoe UI", 9)
            };
            this.Controls.Add(lblOverall);
            y += 18;

            progressBarOverall = new ProgressBar
            {
                Location = new Point(20, y),
                Size = new Size(420, 18),
                Style = ProgressBarStyle.Continuous
            };
            this.Controls.Add(progressBarOverall);
            y += 26;

            // Scene progress
            var lblSceneP = new Label
            {
                Text = "Current scene",
                AutoSize = true,
                Location = new Point(20, y),
                Font = new Font("Segoe UI", 9)
            };
            this.Controls.Add(lblSceneP);
            y += 18;

            progressBarScene = new ProgressBar
            {
                Location = new Point(20, y),
                Size = new Size(420, 18),
                Style = ProgressBarStyle.Continuous
            };
            this.Controls.Add(progressBarScene);
            y += 26;

            // Live telemetry
            lblClock = new Label
            {
                Text = "Clock: - MHz",
                AutoSize = true,
                Location = new Point(20, y),
                Font = new Font("Segoe UI", 9)
            };
            this.Controls.Add(lblClock);

            lblPower = new Label
            {
                Text = "Power: - W",
                AutoSize = true,
                Location = new Point(170, y),
                Font = new Font("Segoe UI", 9)
            };
            this.Controls.Add(lblPower);

            lblUtil = new Label
            {
                Text = "Util: - %",
                AutoSize = true,
                Location = new Point(320, y),
                Font = new Font("Segoe UI", 9)
            };
            this.Controls.Add(lblUtil);
            y += 22;

            lblTemp = new Label
            {
                Text = "Temp: - °C",
                AutoSize = true,
                Location = new Point(20, y),
                Font = new Font("Segoe UI", 9)
            };
            this.Controls.Add(lblTemp);
            y += 30;

            btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(190, y),
                Size = new Size(100, 32)
            };
            btnCancel.Click += BtnCancel_Click;
            this.Controls.Add(btnCancel);

            // Create a progress reporter that marshals to UI thread
            ProgressReporter = new Progress<CalibrationProgress>(OnProgress);
        }

        private void OnProgress(CalibrationProgress p)
        {
            // We're already on the UI thread because we used Progress<T>
            if (p.SceneIndex != _lastSceneIndex)
            {
                _lastSceneIndex = p.SceneIndex;
                lblScene.Text = $"Scene {p.SceneIndex + 1} of {p.TotalScenes}: {p.SceneName}";
            }

            lblPhase.Text = p.Phase switch
            {
                "scene_init" => "Preparing scene...",
                "ramp"       => "Waiting for clock to stabilize...",
                "sampling"   => "Sampling power...",
                _            => p.Phase
            };

            progressBarOverall.Maximum = p.OverallTotal > 0 ? p.OverallTotal : 1;
            progressBarOverall.Value  = Math.Min(p.OverallStep, progressBarOverall.Maximum);

            progressBarScene.Maximum = p.TotalSteps > 0 ? p.TotalSteps : 1;
            progressBarScene.Value   = Math.Min(p.StepIndex, progressBarScene.Maximum);

            lblClock.Text = $"Clock: {p.ClockMHz} MHz";
            lblPower.Text = $"Power: {p.PowerWatts:F1} W";
            lblUtil.Text  = $"Util: {p.UtilPct} %";
            lblTemp.Text  = $"Temp: {p.TempC} °C";

            lblTemp.ForeColor = p.TempC > 80 ? Color.Red : Color.Black;
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            IsCancelled = true;
            btnCancel.Enabled = false;
            lblScene.Text = "Cancelling... please wait.";
            try { _cts.Cancel(); } catch { }
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (IsFinished) return;

            if (!IsCancelled && !_cts.IsCancellationRequested)
            {
                // User clicked X — treat as cancel
                var r = MessageBox.Show(this,
                    "Cancel calibration in progress? The GPU will be reset.",
                    "Confirm cancel",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (r == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }
                BtnCancel_Click(sender, e);
                e.Cancel = true;  // keep open until calibration actually stops
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
