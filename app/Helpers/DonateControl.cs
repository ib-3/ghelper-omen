using GHelper.UI;
using System.Diagnostics;

namespace GHelper.Helpers
{
    public class DonateForm : RForm
    {
        public DonateForm()
        {
            Text = "Support & Credits";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowIcon = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            InitTheme(true);

            var layout = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(25)
            };

            var lblSupport = new Label { Text = "Support me by donating:", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 5) };
            var linkPaypal = new LinkLabel { Text = "Donate via PayPal", AutoSize = true, Font = new Font(Font, FontStyle.Regular), Margin = new Padding(5, 0, 0, 25), LinkColor = darkTheme ? Color.LightSkyBlue : Color.Blue };
            linkPaypal.LinkClicked += (s, e) => Process.Start(new ProcessStartInfo("https://paypal.me/iborbas") { UseShellExecute = true });

            layout.Controls.Add(lblSupport);
            layout.Controls.Add(linkPaypal);

            var lblThanks = new Label { Text = "Special thanks to the following open-source projects:", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 15) };
            layout.Controls.Add(lblThanks);

            void AddCredit(string name, string url, string description)
            {
                var link = new LinkLabel
                {
                    Text = name,
                    AutoSize = true,
                    Font = new Font(Font, FontStyle.Bold),
                    LinkBehavior = LinkBehavior.HoverUnderline,
                    LinkColor = darkTheme ? Color.LightSkyBlue : Color.Blue,
                    Margin = new Padding(0, 0, 0, 2)
                };
                link.LinkClicked += (s, e) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                var desc = new Label
                {
                    Text = description,
                    AutoSize = true,
                    ForeColor = darkTheme ? Color.LightGray : Color.DimGray,
                    Margin = new Padding(10, 0, 0, 12)
                };

                layout.Controls.Add(link);
                layout.Controls.Add(desc);
            }

            AddCredit("G-Helper", "https://github.com/seerge/g-helper", "Foundational project — UI, layout, and core logic.");
            AddCredit("PawnIO", "https://github.com/namazso/PawnIO", "Signed kernel driver for safe MSR/MMIO/SMU access.");
            AddCredit("Libre Hardware Monitor", "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor", "Robust hardware sensor and power telemetry reading.");
            AddCredit("Universal x86 Tuning Utility (UXTU)", "https://github.com/JamesCJ60/Universal-x86-Tuning-Utility", "Ryzen SMU undervolting and power limit endpoints.");
            AddCredit("OmenCore", "https://github.com/OmenHub/OmenCore", "Original HP OMEN WMI reverse engineering.");
            AddCredit("NvAPIWrapper", "https://github.com/falahati/NvAPIWrapper", "NVIDIA GPU API access.");

            Controls.Add(layout);
        }
    }

    public class DonateControl
    {
        private readonly SettingsForm _settings;
        private readonly RBadgeButton _button;

        public DonateControl(SettingsForm settings, RBadgeButton button)
        {
            _settings = settings;
            _button = button;
        }

        public void Init()
        {
            if (AppConfig.Is("hide_donate_button"))
            {
                _button.Visible = false;
                return;
            }

            _button.Click += Button_Click;
            
            // Automatically say Thank You!
            SetThankYou();
        }

        public void ApplyTheme()
        {
            // Now handled dynamically when the form is created
        }

        private void Button_Click(object? sender, EventArgs e)
        {
            using var form = new DonateForm();
            form.BackColor = _settings.BackColor;
            form.ForeColor = _settings.ForeColor;
            form.ShowDialog(_settings);
        }

        private void SetThankYou()
        {
            _button.Badge = 0;
            _button.Text = "Thank You!"; // Set manually as requested
        }
    }
}
