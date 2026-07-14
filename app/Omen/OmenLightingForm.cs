using GHelper.UI;
using OmenCore.Hardware;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GHelper
{
    /// <summary>
    /// OMEN Lighting Control — compact form for keyboard zone colours,
    /// per-key RGB, animation effects, and lightbar control.
    /// </summary>
    public partial class OmenLightingForm : RForm
    {
        private readonly OmenLightingService _lighting;

        // Zone colour state (4-zone keyboards)
        private Color[] _zoneColors = new Color[4]
        {
            Color.FromArgb(0, 120, 255),   // Zone 0 — Right
            Color.FromArgb(0, 200, 120),   // Zone 1 — Middle-right
            Color.FromArgb(255, 120, 0),   // Zone 2 — Middle-left
            Color.FromArgb(200, 0, 255),   // Zone 3 — WASD
        };

        // Lightbar zone colour state
        private Color[] _lightBarColors = new Color[1]
        {
            Color.FromArgb(0, 180, 255),
        };

        // Colour gradient list for multi-colour effects (shared by keyboard
        // zone effects, lightbar effects, and per-key effects)
        private Color[] _effectColors = new Color[4]
        {
            Color.FromArgb(255, 0, 80),
            Color.FromArgb(255, 120, 0),
            Color.FromArgb(0, 200, 255),
            Color.FromArgb(100, 0, 255),
        };

        private bool _hasPerKey;

        public OmenLightingForm(OmenLightingService lighting, bool showLightbarOnly = false)
        {
            _lighting = lighting;
            InitializeComponent();
            BindCapabilities();
            InitTheme();

            // Fix TabPage background rendering in Dark Mode
            this.BackColor = RForm.formBack;
            panelZones.BackColor = RForm.formBack;

            tabControl.Visible = false;
            if (showLightbarOnly)
            {
                this.Text = "Omen Lightbar";
                foreach (Control c in tabLightBar.Controls.OfType<Control>().ToList())
                {
                    this.Controls.Add(c);
                }
            }
            else
            {
                if (!lighting.Capabilities.HasLightBar)
                {
                    // No lightbar
                }
                foreach (Control c in tabKeyboard.Controls.OfType<Control>().ToList())
                {
                    this.Controls.Add(c);
                }
            }

            // ── Capabilities ──────────────────────────────────────────────────
            _hasPerKey = lighting.Capabilities.IsPerKey;

            if (_hasPerKey)
            {
                labelKbdStatus.Text = "Keyboard: Per-key RGB";
                labelZone0.Text = "All Keys";
                panelZone1.Visible = false;
                panelZone2.Visible = false;
                panelZone3.Visible = false;
            }
            else
            {
                labelKbdStatus.Text = $"Keyboard: {lighting.Capabilities.KeyboardZoneCount}-zone RGB";
            }

            if (!lighting.Capabilities.HasLightBar)
            {
                labelLbStatus.Text = "No Lightbar detected.";
                groupLbZones.Visible = false;
                groupLbEffect.Visible = false;
            }

            // ── Effect combos ─────────────────────────────────────────────
            var effectNames = new[] { "Static", "Breathing", "Color Cycle", "Wave", "Blinking" };
            comboKbdEffect.Items.AddRange(effectNames);
            comboKbdEffect.SelectedIndex = 0;
            comboLbEffect.Items.AddRange(effectNames);
            comboLbEffect.SelectedIndex = 0;

            ResizeFormToFitContent();

            WireEvents();
        }

        // ──────────────────────────────────────────────────────────────────
        // Initialisation
        // ──────────────────────────────────────────────────────────────────

        private void BindCapabilities()
        {
            var caps = _lighting.Capabilities;

            // ── Keyboard tab ─────────────────────────────────────────────
            if (caps.HasKeyboardLighting)
            {
                labelKbdStatus.Text = $"Keyboard: {caps.KeyboardType}";

                if (caps.IsPerKey)
                {
                    _hasPerKey = true;
                    labelKbdStatus.Text += "  (Per-key RGB)";
                    caps.KeyboardZoneCount = 1;
                }

                int zones = Math.Max(1, caps.KeyboardZoneCount);
                _zoneColors = new Color[zones];
                for (int i = 0; i < zones; i++)
                    _zoneColors[i] = Color.FromArgb(0, 180, 255);

                BuildKeyboardZonePickers(zones);

                panelZones.Visible = true;
                if (!caps.IsFourZone && !caps.IsPerKey)
                {
                    // No effects maybe
                    groupKbdEffect.Visible = false;
                }
            }
            else
            {
                tabKeyboard.Enabled = false;
                labelKbdStatus.Text = "Keyboard lighting: not detected";
            }

            // ── Lightbar tab ─────────────────────────────────────────────
            if (caps.HasLightBar)
            {
                int zones = Math.Max(1, caps.LightBarZoneCount);
                labelLbStatus.Text = $"Light Bar: {zones} zone{(zones > 1 ? "s" : "")} detected";

                _lightBarColors = new Color[zones];
                for (int i = 0; i < zones; i++)
                    _lightBarColors[i] = Color.FromArgb(0, 180, 255);

                BuildLightBarZonePickers(zones);
            }
            else
            {
                tabLightBar.Enabled = false;
                labelLbStatus.Text = "Light Bar: not detected on this machine";
            }

            // ── Effect combos ─────────────────────────────────────────────
            var effectNames = new[] { "Breathing", "Color Cycle", "Static" };
            comboKbdEffect.Items.AddRange(effectNames);
            comboKbdEffect.SelectedIndex = 0;
            comboLbEffect.Items.AddRange(effectNames);
            comboLbEffect.SelectedIndex = 0;

            ResizeFormToFitContent();
        }

        // ──────────────────────────────────────────────────────────────────
        // Dynamic form sizing
        // ──────────────────────────────────────────────────────────────────

        private void ResizeFormToFitContent()
        {
            int maxBottom = 0;
            foreach (Control c in this.Controls)
            {
                if (c.Visible && c.Bottom > maxBottom && c != tabControl)
                    maxBottom = c.Bottom;
            }

            if (maxBottom <= 0) return;

            int neededHeight = maxBottom + 20; // 20px padding at the bottom
            neededHeight = Math.Max(neededHeight, 180);
            neededHeight = Math.Min(neededHeight, 720);

            this.ClientSize = new Size(this.ClientSize.Width, neededHeight);
        }

        private void WireEvents()
        {
            // Note: Keyboard zone panel clicks are wired dynamically in BuildKeyboardZonePickers.

            // Effect colour pickers (keyboard zone effects + lightbar effects)
            panelEffColor1.Click += EffectColor_Click;
            panelEffColor2.Click += EffectColor_Click;
            panelEffColor3.Click += EffectColor_Click;
            panelEffColor4.Click += EffectColor_Click;
            panelLbEffColor1.Click += LbEffectColor_Click;
            panelLbEffColor2.Click += LbEffectColor_Click;
            panelLbEffColor3.Click += LbEffectColor_Click;
            panelLbEffColor4.Click += LbEffectColor_Click;

            // Apply buttons
            btnKbdApplyZones.Click += BtnKbdApplyZones_Click;
            btnKbdApplyEffect.Click += BtnKbdApplyEffect_Click;
            btnLbApply.Click += BtnLbApply_Click;

            // Effect selection changes
            comboKbdEffect.SelectedIndexChanged += ComboKbdEffect_SelectedIndexChanged;
            comboLbEffect.SelectedIndexChanged += ComboLbEffect_SelectedIndexChanged;

            // Backlight toggle
            checkKbdBacklight.CheckedChanged += (_, _) =>
                _lighting.SetKeyboardBacklight(checkKbdBacklight.Checked);
        }

        // ──────────────────────────────────────────────────────────────────
        // Zone colour management (4-zone keyboards)
        // ──────────────────────────────────────────────────────────────────

        private void UpdateZonePanelColors()
        {
            var zoneLabels = _hasPerKey 
                ? new[] { "All Keys" }
                : new[] { "Right", "Mid-R", "Mid-L", "WASD", "Left-Mac", "Extra" };

            foreach (Control c in groupKbdZones.Controls)
            {
                if (c is Panel p && p.Tag is int i)
                {
                    p.BackColor = _zoneColors[i];

                    var lbl = p.Controls.OfType<Label>().FirstOrDefault();
                    if (lbl != null)
                    {
                        string defaultLabel = i < zoneLabels.Length ? zoneLabels[i] : $"Zone {i + 1}";
                        lbl.Text = defaultLabel;
                        lbl.ForeColor = IsColorDark(_zoneColors[i]) ? Color.White : Color.Black;
                    }
                }
            }
        }

        private void BuildKeyboardZonePickers(int zones)
        {
            // Clear existing panels first
            foreach (Control c in groupKbdZones.Controls.OfType<Panel>().ToList())
                groupKbdZones.Controls.Remove(c);

            int btnW = Math.Max(50, (groupKbdZones.Width - 110) / Math.Max(1, zones));
            int zGap = 8;
            int zTop = 25;
            int zH = 50;

            for (int z = 0; z < zones; z++)
            {
                var p = new Panel
                {
                    Width = btnW,
                    Height = zH,
                    Left = 8 + z * (btnW + zGap),
                    Top = zTop,
                    BackColor = _zoneColors[z],
                    BorderStyle = BorderStyle.FixedSingle,
                    Cursor = Cursors.Hand,
                    Tag = z
                };
                var lbl = new Label
                {
                    Text = "Zone " + (z + 1),
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand
                };
                lbl.Click += (s, e) => ZonePanel_Click(p, e);
                p.Click += ZonePanel_Click;
                p.Controls.Add(lbl);

                groupKbdZones.Controls.Add(p);
            }

            // Reposition Apply button
            btnKbdApplyZones.Location = new Point(8 + zones * (btnW + zGap) + zGap, zTop);
            
            UpdateZonePanelColors();
        }

        private void ZonePanel_Click(object? sender, EventArgs e)
        {
            Panel? p = sender as Panel;
            if (p is null) return;
            if (p.Tag is int zoneIndex)
            {
                using var dlg = new ColorDialog();
                dlg.Color = _zoneColors[zoneIndex];
                dlg.FullOpen = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _zoneColors[zoneIndex] = dlg.Color;
                    UpdateZonePanelColors();
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Lightbar zone pickers (dynamic)
        // ──────────────────────────────────────────────────────────────────

        private void BuildLightBarZonePickers(int zones)
        {
            panelLbZones.Controls.Clear();

            int btnW = Math.Max(50, (panelLbZones.Width - 10) / Math.Max(1, zones));
            for (int z = 0; z < zones; z++)
            {
                int zCopy = z;
                var p = new Panel
                {
                    Width = btnW,
                    Height = panelLbZones.Height - 10,
                    Left = 5 + z * (btnW + 4),
                    Top = 5,
                    BackColor = _lightBarColors[z],
                    BorderStyle = BorderStyle.FixedSingle,
                    Cursor = Cursors.Hand,
                    Tag = z
                };
                var lbl = new Label
                {
                    Text = $"Z{z + 1}",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = IsColorDark(_lightBarColors[z]) ? Color.White : Color.Black,
                };
                p.Controls.Add(lbl);
                p.Click += (s, e) => OpenLightBarColorDialog(zCopy, p, lbl);
                // Forward label clicks to the same handler (the docked
                // label would otherwise swallow clicks).
                lbl.Click += (s, e) => OpenLightBarColorDialog(zCopy, p, lbl);
                panelLbZones.Controls.Add(p);
            }
        }

        private void OpenLightBarColorDialog(int zoneIndex, Panel p, Label lbl)
        {
            using var dlg = new ColorDialog();
            dlg.Color = _lightBarColors[zoneIndex];
            dlg.FullOpen = true;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _lightBarColors[zoneIndex] = dlg.Color;
                p.BackColor = dlg.Color;
                lbl.ForeColor = IsColorDark(dlg.Color) ? Color.White : Color.Black;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Effect colour pickers
        // ──────────────────────────────────────────────────────────────────

        private void EffectColor_Click(object? sender, EventArgs e)
        {
            if (sender is Panel p)
                PickColor(p, ref _effectColors, GetEffectColorIndex(p));
        }

        private void LbEffectColor_Click(object? sender, EventArgs e)
        {
            if (sender is Panel p)
                PickColor(p, ref _effectColors, GetLbEffectColorIndex(p));
        }

        private int GetEffectColorIndex(Panel p)
        {
            if (p == panelEffColor1) return 0;
            if (p == panelEffColor2) return 1;
            if (p == panelEffColor3) return 2;
            return 3;
        }

        private int GetLbEffectColorIndex(Panel p)
        {
            if (p == panelLbEffColor1) return 0;
            if (p == panelLbEffColor2) return 1;
            if (p == panelLbEffColor3) return 2;
            return 3;
        }

        private void PickColor(Panel p, ref Color[] arr, int idx)
        {
            using var dlg = new ColorDialog { Color = arr[idx], FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                arr[idx] = dlg.Color;
                p.BackColor = dlg.Color;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Effect panel visibility
        // ──────────────────────────────────────────────────────────────────

        private OmenLightingEffect GetSelectedEffect(ComboBox combo)
        {
            var text = combo.SelectedItem?.ToString();
            if (text == "Breathing") return OmenLightingEffect.Breathing;
            if (text == "Color Cycle") return OmenLightingEffect.ColorCycle;
            if (text == "Wave") return OmenLightingEffect.Wave;
            if (text == "Blinking") return OmenLightingEffect.Strobe;
            return OmenLightingEffect.Static;
        }

        private void ComboKbdEffect_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var effect = GetSelectedEffect(comboKbdEffect);
            panelEffColors.Visible = effect != OmenLightingEffect.ColorCycle;
        }

        private void ComboLbEffect_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var effect = GetSelectedEffect(comboLbEffect);
            panelLbEffColors.Visible = effect != OmenLightingEffect.ColorCycle;
        }

        // ──────────────────────────────────────────────────────────────────
        // Apply handlers (zone keyboard + lightbar)
        // ──────────────────────────────────────────────────────────────────

        private void BtnKbdApplyZones_Click(object? sender, EventArgs e)
        {
            bool ok = _lighting.SetKeyboardZoneColors(_zoneColors);
            FlashButton(btnKbdApplyZones, ok);
        }

        private void BtnKbdApplyEffect_Click(object? sender, EventArgs e)
        {
            var effect = GetSelectedEffect(comboKbdEffect);
            byte brightness = (byte)trackKbdBrightness.Value;
            byte speed = (byte)trackKbdSpeed.Value;
            Color[]? colors = (effect != OmenLightingEffect.ColorCycle) ? _effectColors : null;

            bool ok = _lighting.SetKeyboardEffect(effect, brightness, speed, colors);
            FlashButton(btnKbdApplyEffect, ok);
        }

        private void BtnLbApply_Click(object? sender, EventArgs e)
        {
            var effect = GetSelectedEffect(comboLbEffect);
            byte brightness = (byte)trackLbBrightness.Value;
            byte speed = (byte)trackLbSpeed.Value;

            bool ok;
            if (effect == OmenLightingEffect.Static)
            {
                ok = _lighting.SetLightBarZoneColors(_lightBarColors);
            }
            else
            {
                Color[]? colors = (effect != OmenLightingEffect.ColorCycle) ? _effectColors : null;
                ok = _lighting.SetLightBarEffect(effect, brightness, speed, colors);
            }

            _lighting.SetLightBarBrightness(brightness);
            FlashButton(btnLbApply, ok);
        }

        // ──────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────

        private static bool IsColorDark(Color c)
            => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;

        private static void FlashButton(Button btn, bool success)
        {
            var orig = btn.BackColor;
            btn.BackColor = success ? Color.FromArgb(0, 180, 80) : Color.FromArgb(200, 50, 50);
            var timer = new System.Windows.Forms.Timer { Interval = 600 };
            timer.Tick += (_, _) => { btn.BackColor = orig; timer.Stop(); timer.Dispose(); };
            timer.Start();
        }
    }
}
