using GHelper.UI;
using OmenCore.Hardware;
using System.Drawing;
using System.Windows.Forms;

namespace GHelper
{
    /// <summary>
    /// OMEN Lighting Control — compact form for keyboard zone colours,
    /// animation effects, and lightbar control.
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

        // Colour gradient list for multi-colour effects
        private Color[] _effectColors = new Color[4]
        {
            Color.FromArgb(255, 0, 80),
            Color.FromArgb(255, 120, 0),
            Color.FromArgb(0, 200, 255),
            Color.FromArgb(100, 0, 255),
        };

        public OmenLightingForm(OmenLightingService lighting)
        {
            _lighting = lighting;
            InitializeComponent();
            InitTheme();
            BindCapabilities();
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

                // Show zone pickers only for 4-zone; show effect panel always
                panelZones.Visible = caps.IsFourZone;

                // Per-key note
                if (caps.IsPerKey)
                {
                    labelKbdStatus.Text += "  (Per-key — managed by Windows)";
                    groupKbdZones.Visible = false;
                    groupKbdEffect.Visible = false;
                    
                    var btnDynamicLighting = new Button
                    {
                        Text = "Open Windows Dynamic Lighting",
                        AutoSize = true,
                        Location = new Point(labelKbdStatus.Left, labelKbdStatus.Bottom + 20),
                        Padding = new Padding(5)
                    };
                    btnDynamicLighting.Click += (s, e) => {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:personalization-dynamiclighting") { UseShellExecute = true }); } catch { }
                    };
                    tabKeyboard.Controls.Add(btnDynamicLighting);
                }

                // Populate zone colour panels with initial colours
                UpdateZonePanelColors();
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

                // Resize bar colour array to match zone count
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
            var effectNames = new[] { "Breathing" }; // Users requested keeping only Breathing
            comboKbdEffect.Items.AddRange(effectNames);
            comboKbdEffect.SelectedIndex = 0;
            comboLbEffect.Items.AddRange(effectNames);
            comboLbEffect.SelectedIndex = 0;
        }

        private void WireEvents()
        {
            // Zone colour click
            foreach (Panel p in panelZones.Controls.OfType<Panel>())
                p.Click += ZonePanel_Click;

            // Lightbar zone click handled dynamically in BuildLightBarZonePickers

            // Effect colour pickers
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
        // Zone colour management
        // ──────────────────────────────────────────────────────────────────

        private void UpdateZonePanelColors()
        {
            var zonePanels = new[] { panelZone0, panelZone1, panelZone2, panelZone3 };
            var zoneLabels = new[] { "Right", "Mid-R", "Mid-L", "WASD" };
            for (int i = 0; i < zonePanels.Length; i++)
            {
                zonePanels[i].BackColor = _zoneColors[i];
                zonePanels[i].Tag = i;

                // Pick contrasting text colour
                var lbl = zonePanels[i].Controls.OfType<Label>().FirstOrDefault();
                if (lbl != null)
                {
                    lbl.Text = zoneLabels[i];
                    lbl.ForeColor = IsColorDark(_zoneColors[i]) ? Color.White : Color.Black;
                }
            }
        }

        private void ZonePanel_Click(object? sender, EventArgs e)
        {
            if (sender is Panel p && p.Tag is int zoneIndex)
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
                p.Click += (_, _) =>
                {
                    using var dlg = new ColorDialog();
                    dlg.Color = _lightBarColors[zCopy];
                    dlg.FullOpen = true;
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _lightBarColors[zCopy] = dlg.Color;
                        p.BackColor = dlg.Color;
                        lbl.ForeColor = IsColorDark(dlg.Color) ? Color.White : Color.Black;
                    }
                };
                panelLbZones.Controls.Add(p);
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
            return OmenLightingEffect.Static;
        }

        private void ComboKbdEffect_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var effect = GetSelectedEffect(comboKbdEffect);
            // Color pickers are useful for Static, Breathing, Wave, Strobe
            // ColorCycle auto-generates rainbow so hide them
            panelEffColors.Visible = effect != OmenLightingEffect.ColorCycle;
        }

        private void ComboLbEffect_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var effect = GetSelectedEffect(comboLbEffect);
            panelLbEffColors.Visible = effect != OmenLightingEffect.ColorCycle;
        }

        // ──────────────────────────────────────────────────────────────────
        // Apply handlers
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
                // Static — use per-zone colours
                ok = _lighting.SetLightBarZoneColors(_lightBarColors);
            }
            else
            {
                // Animated — pass effect + optional colour set
                Color[]? colors = (effect != OmenLightingEffect.ColorCycle) ? _effectColors : null;
                ok = _lighting.SetLightBarEffect(effect, brightness, speed, colors);
            }

            // Also set brightness separately (some firmware needs this)
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
