using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using GHelper.UI;
using OmenCore.Hardware;

namespace GHelper;

public class OmenLightingForm : RForm
{
	private readonly OmenLightingService _lighting;
	private Color[] _zoneColors = new Color[4] { Color.FromArgb(0, 168, 255), Color.FromArgb(0, 200, 150), Color.FromArgb(196, 30, 58), Color.FromArgb(139, 0, 255) };
	private Color[] _effectColors = new Color[4] { Color.Red, Color.Yellow, Color.Lime, Color.Cyan };
	private bool _hasPerKey;
	private bool _isPerKeyModeActive;
	private int _detectedKeyboardZoneCount = 1;

	private static readonly (string Name, OmenLightingEffect Effect)[] AllEffects = new(string, OmenLightingEffect)[13]
	{
		("Static", OmenLightingEffect.Static),
		("Breathing", OmenLightingEffect.Breathing),
		("Color Cycle", OmenLightingEffect.ColorCycle),
		("Wave", OmenLightingEffect.Wave),
		("Starlight", OmenLightingEffect.Starlight),
		("Ghosting", OmenLightingEffect.Ghosting),
		("Ripple", OmenLightingEffect.Ripple),
		("OMEN X", OmenLightingEffect.OmenX),
		("Raindrop", OmenLightingEffect.Raindrop),
		("Confetti", OmenLightingEffect.Confetti),
		("Sun", OmenLightingEffect.Sun),
		("Swipe", OmenLightingEffect.Swipe),
		("Audio Pulse", OmenLightingEffect.AudioPulse)
	};
	private (string Name, OmenLightingEffect Effect)[] _visibleKbdEffects = AllEffects;

	private IContainer components = null;

	private Panel panelSegment;
	private RButton btnZonedMode;
	private RButton btnPerKeyMode;
	
	private Panel panelVisualizer;
	private Panel groupKbdZones;
	private KeyboardVisualizer keyboardVisualizer;
	
	private Panel panelSettings;
	private Label labelEffectType;
	private RComboBox comboKbdEffect;
	
	private Label labelEffColors;
	private Panel panelEffColors;
	private Panel panelEffColor1;
	private Panel panelEffColor2;
	private Panel panelEffColor3;
	private Panel panelEffColor4;
	
	private Label labelKbdSp;
	private RComboBox comboKbdSpeed;
	
	private Label labelKbdDirection;
	private RComboBox comboKbdDirection;
	
	private Label labelKbdSize;
	private RComboBox comboKbdSize;
	
	private Label labelZoneCount;
	private Slider trackZoneCount;
	private Label labelZoneCountValue;
	
	private Label labelKbdBr;
	private Slider trackKbdBrightness;
	private Label labelKbdBrValue;

	public OmenLightingForm(OmenLightingService lighting, bool showLightbarOnly = false)
	{
		_lighting = lighting;
		InitializeComponent();
		BindCapabilities();
		InitTheme();
		BackColor = Color.FromArgb(32, 32, 32);
		panelVisualizer.BackColor = Color.FromArgb(24, 24, 24);
		panelSettings.BackColor = Color.FromArgb(24, 24, 24);
		panelSegment.BackColor = Color.FromArgb(24, 24, 24);
		
		ResizeFormToFitContent();
		WireEvents();
		UpdateModeUI();
		LoadEffectColors(GetSelectedEffect(comboKbdEffect));
		ComboKbdEffect_SelectedIndexChanged(null, EventArgs.Empty);
		UpdateKeyboardVisualizer();
	}

	private void BindCapabilities()
	{
		OmenLightingCapabilities capabilities = _lighting.Capabilities;
		if (capabilities.HasKeyboardLighting)
		{
			_hasPerKey = capabilities.IsPerKey;
			int num = (_detectedKeyboardZoneCount = capabilities.EffectiveKeyboardZoneCount);
			
			if (_hasPerKey)
			{
				_isPerKeyModeActive = true;
				panelSegment.Visible = AppConfig.Is("omen_dev_show_zoned") && Application.ExecutablePath.EndsWith("dev.exe", StringComparison.OrdinalIgnoreCase);
			}
			else
			{
				_isPerKeyModeActive = false;
				panelSegment.Visible = true;
			}
			
			_visibleKbdEffects = AllEffects;
			
			_zoneColors = new Color[num];
			string savedZoneColors = AppConfig.GetString("omen_kbd_zone_colors");
			if (!string.IsNullOrEmpty(savedZoneColors))
			{
				string[] parts = savedZoneColors.Split(';');
				for (int i = 0; i < num; i++)
				{
					if (i < parts.Length)
					{
						string[] rgb = parts[i].Split(',');
						if (rgb.Length == 3 && int.TryParse(rgb[0], out int r) && int.TryParse(rgb[1], out int g) && int.TryParse(rgb[2], out int b))
						{
							_zoneColors[i] = Color.FromArgb(r, g, b);
							continue;
						}
					}
					_zoneColors[i] = Color.FromArgb(0, 168, 255);
				}
			}
			else
			{
				for (int i = 0; i < num; i++)
				{
					_zoneColors[i] = Color.FromArgb(0, 168, 255);
				}
			}
			
			trackZoneCount.Min = 1;
			trackZoneCount.Max = 10;
			trackZoneCount.Step = 1;
			trackZoneCount.Value = num;
			UpdateZoneCountLabel(num);
			BuildKeyboardZonePickers(num);
			
			UpdateModeUI();
		}
		
		comboKbdEffect.Items.Clear();
		(string, OmenLightingEffect)[] visibleKbdEffects = _visibleKbdEffects;
		for (int k = 0; k < visibleKbdEffects.Length; k++)
		{
			(string, OmenLightingEffect) tuple = visibleKbdEffects[k];
			comboKbdEffect.Items.Add(tuple.Item1);
		}
		
		int savedEffect = AppConfig.Get("omen_kbd_effect", 0);
		int indexToSelect = 0;
		for (int i = 0; i < _visibleKbdEffects.Length; i++)
		{
			if ((int)_visibleKbdEffects[i].Effect == savedEffect)
			{
				indexToSelect = i;
				break;
			}
		}

		if (comboKbdEffect.Items.Count > 0)
		{
			comboKbdSpeed.SelectedIndex = Math.Clamp(AppConfig.Get("omen_kbd_speed_idx", 1), 0, 2);
			comboKbdDirection.SelectedIndex = Math.Clamp(AppConfig.Get("omen_kbd_dir_idx", 0), 0, 3);
			comboKbdSize.SelectedIndex = Math.Clamp(AppConfig.Get("omen_kbd_size_idx", 1), 0, 2);
			comboKbdEffect.SelectedIndex = indexToSelect;
		}
		
		ResizeFormToFitContent();
	}

	private void UpdateModeUI()
	{
		if (_isPerKeyModeActive)
		{
			btnPerKeyMode.BackColor = Color.FromArgb(45, 45, 45);
			btnZonedMode.BackColor = Color.FromArgb(32, 32, 32);
			keyboardVisualizer.Visible = true;
			groupKbdZones.Visible = false;
			labelZoneCount.Visible = false;
			trackZoneCount.Visible = false;
			labelZoneCountValue.Visible = false;
		}
		else
		{
			btnZonedMode.BackColor = Color.FromArgb(45, 45, 45);
			btnPerKeyMode.BackColor = Color.FromArgb(32, 32, 32);
			keyboardVisualizer.Visible = false;
			groupKbdZones.Visible = true;
			labelZoneCount.Visible = true;
			trackZoneCount.Visible = true;
			labelZoneCountValue.Visible = true;
		}
		RelayoutSettingsPanel();
	}

	private void RelayoutSettingsPanel()
	{
		int currentY = 20;
		
		labelEffectType.Top = currentY;
		comboKbdEffect.Top = currentY + 30;
		
		labelEffColors.Top = currentY;
		panelEffColors.Top = currentY + 30;
		
		currentY += 80;
		
		OmenLightingEffect selectedEffect = GetSelectedEffect(comboKbdEffect);
		bool hasSpeed = selectedEffect != OmenLightingEffect.Static;
		bool hasDirection = selectedEffect == OmenLightingEffect.Wave || selectedEffect == OmenLightingEffect.Swipe;
		bool hasSize = selectedEffect == OmenLightingEffect.Wave;

		bool hasAnimControls = false;
		if (hasSpeed)
		{
			labelKbdSp.Top = currentY;
			comboKbdSpeed.Top = currentY + 30;
			hasAnimControls = true;
		}
		
		if (hasDirection)
		{
			labelKbdDirection.Top = currentY;
			comboKbdDirection.Top = currentY + 30;
			hasAnimControls = true;
		}
		
		if (hasSize)
		{
			labelKbdSize.Top = currentY;
			comboKbdSize.Top = currentY + 30;
			hasAnimControls = true;
		}
		
		if (hasAnimControls) currentY += 80;
		
		if (!_isPerKeyModeActive)
		{
			labelZoneCount.Top = currentY;
			trackZoneCount.Top = currentY + 30;
			labelZoneCountValue.Top = currentY;
			currentY += 80;
		}
		
		labelKbdBr.Top = currentY;
		trackKbdBrightness.Top = currentY + 30;
		labelKbdBrValue.Top = currentY;
		currentY += 80;
		
		panelSettings.Height = currentY + 10;
		ResizeFormToFitContent();
	}

	private void ResizeFormToFitContent()
	{
		int num = 0;
		foreach (Control control in base.Controls)
		{
			if (control.Visible && control.Bottom > num)
			{
				num = control.Bottom;
			}
		}
		if (num > 0)
		{
			int val = num + 20;
			val = Math.Max(val, 180);
			val = Math.Min(val, 800);
			base.ClientSize = new Size(base.ClientSize.Width, val);
		}
	}

	private void WireEvents()
	{
		btnZonedMode.Click += delegate { _isPerKeyModeActive = false; UpdateModeUI(); BtnKbdApplyEffect_Click(null, EventArgs.Empty); };
		btnPerKeyMode.Click += delegate { _isPerKeyModeActive = true; UpdateModeUI(); BtnKbdApplyEffect_Click(null, EventArgs.Empty); };
		
		panelEffColor1.Click += EffectColor_Click;
		panelEffColor2.Click += EffectColor_Click;
		panelEffColor3.Click += EffectColor_Click;
		panelEffColor4.Click += EffectColor_Click;
		
		comboKbdEffect.SelectedIndexChanged += ComboKbdEffect_SelectedIndexChanged;
		trackZoneCount.ValueChanged += TrackZoneCount_ValueChanged;
		
		trackKbdBrightness.ValueChanged += delegate
		{
			labelKbdBrValue.Text = $"{trackKbdBrightness.Value}%";
			_lighting.SetKeyboardBrightness((byte)trackKbdBrightness.Value);
			float num = trackKbdBrightness.Value / 100f;
			keyboardVisualizer.Brightness = num;
			foreach (Control control in groupKbdZones.Controls)
			{
				if (control is RGlowPanel rGlowPanel)
				{
					rGlowPanel.GlowIntensity = num;
				}
			}
			BtnKbdApplyEffect_Click(null, EventArgs.Empty);
		};
		comboKbdSpeed.SelectedIndexChanged += delegate
		{
			BtnKbdApplyEffect_Click(null, EventArgs.Empty);
		};
		comboKbdDirection.SelectedIndexChanged += delegate
		{
			BtnKbdApplyEffect_Click(null, EventArgs.Empty);
		};
		comboKbdSize.SelectedIndexChanged += delegate
		{
			BtnKbdApplyEffect_Click(null, EventArgs.Empty);
		};
		
		if (comboKbdEffect.Items.Count > 0)
		{
			ComboKbdEffect_SelectedIndexChanged(null, EventArgs.Empty);
		}
	}

	private void EffectColor_Click(object? sender, EventArgs e)
	{
		if (sender is Panel p)
		{
			using GHelper.UI.ColorPickerForm colorDialog = new GHelper.UI.ColorPickerForm(p.BackColor);
			if (colorDialog.ShowDialog(this) == DialogResult.OK)
			{
				Color c = colorDialog.SelectedColor;
				p.BackColor = c;
				
				if (p == panelEffColor1) _effectColors[0] = c;
				if (p == panelEffColor2) _effectColors[1] = c;
				if (p == panelEffColor3) _effectColors[2] = c;
				if (p == panelEffColor4) _effectColors[3] = c;

				BtnKbdApplyEffect_Click(null, EventArgs.Empty);
				UpdateKeyboardVisualizer();
			}
		}
	}

	private OmenLightingEffect GetSelectedEffect(ComboBox combo)
	{
		string text = combo.SelectedItem?.ToString();
		if (string.IsNullOrEmpty(text)) return OmenLightingEffect.Static;
		(string, OmenLightingEffect)[] allEffects = AllEffects;
		for (int i = 0; i < allEffects.Length; i++)
		{
			(string, OmenLightingEffect) tuple = allEffects[i];
			if (tuple.Item1 == text) return tuple.Item2;
		}
		return OmenLightingEffect.Static;
	}

	private int GetColorCountForEffect(OmenLightingEffect selectedEffect)
	{
		switch(selectedEffect)
		{
			case OmenLightingEffect.Wave:
			case OmenLightingEffect.Sun: 
			case OmenLightingEffect.Confetti: return 0;
			case OmenLightingEffect.Static:
			case OmenLightingEffect.Ghosting: return 1;
			case OmenLightingEffect.Breathing:
			case OmenLightingEffect.Starlight:
			case OmenLightingEffect.Raindrop:
			case OmenLightingEffect.AudioPulse:
			case OmenLightingEffect.Swipe: return 2;
			case OmenLightingEffect.Ripple:
			case OmenLightingEffect.ColorCycle:
			case OmenLightingEffect.OmenX: return 4;
		}
		return 4;
	}

	private void ComboKbdEffect_SelectedIndexChanged(object? sender, EventArgs e)
	{
		OmenLightingEffect selectedEffect = GetSelectedEffect(comboKbdEffect);
		
		bool hasSpeed = selectedEffect != OmenLightingEffect.Static;
		labelKbdSp.Visible = hasSpeed;
		comboKbdSpeed.Visible = hasSpeed;

		bool hasDirection = selectedEffect == OmenLightingEffect.Wave || selectedEffect == OmenLightingEffect.Swipe;
		labelKbdDirection.Visible = hasDirection;
		comboKbdDirection.Visible = hasDirection;

		bool hasSize = selectedEffect == OmenLightingEffect.Ripple || selectedEffect == OmenLightingEffect.Raindrop;
		labelKbdSize.Visible = hasSize;
		comboKbdSize.Visible = hasSize;

		int colorCount = GetColorCountForEffect(selectedEffect);
		panelEffColor1.Visible = colorCount >= 1;
		panelEffColor2.Visible = colorCount >= 2;
		panelEffColor3.Visible = colorCount >= 3;
		panelEffColor4.Visible = colorCount >= 4;
		labelEffColors.Visible = colorCount > 0;

		if (sender != null)
		{
			LoadEffectColors(selectedEffect);
			BtnKbdApplyEffect_Click(null, EventArgs.Empty);
		}
		RelayoutSettingsPanel();
	}

	private void SaveCurrentEffectState()
	{
		OmenLightingEffect effect = GetSelectedEffect(comboKbdEffect);
		AppConfig.Set("omen_kbd_effect", (int)effect);
		AppConfig.Set("omen_kbd_speed_idx", comboKbdSpeed.SelectedIndex);
		AppConfig.Set("omen_kbd_dir_idx", comboKbdDirection.SelectedIndex);
		AppConfig.Set("omen_kbd_size_idx", comboKbdSize.SelectedIndex);
		
		string colorsStr = string.Join(";", _effectColors.Select(c => $"{c.R},{c.G},{c.B}"));
		AppConfig.Set("omen_kbd_colors_" + (int)effect, colorsStr);

		string zoneColorsStr = string.Join(";", _zoneColors.Select(c => $"{c.R},{c.G},{c.B}"));
		AppConfig.Set("omen_kbd_zone_colors", zoneColorsStr);
	}

	private void LoadEffectColors(OmenLightingEffect effect)
	{
		string colorsStr = AppConfig.GetString("omen_kbd_colors_" + (int)effect);
		if (!string.IsNullOrEmpty(colorsStr))
		{
			string[] parts = colorsStr.Split(';');
			for (int i = 0; i < parts.Length && i < _effectColors.Length; i++)
			{
				string[] rgb = parts[i].Split(',');
				if (rgb.Length == 3 && int.TryParse(rgb[0], out int r) && int.TryParse(rgb[1], out int g) && int.TryParse(rgb[2], out int b))
				{
					_effectColors[i] = Color.FromArgb(r, g, b);
				}
			}
		}
		else
		{
			_effectColors[0] = Color.FromArgb(255, 0, 0);
			_effectColors[1] = Color.FromArgb(255, 255, 0);
			_effectColors[2] = Color.FromArgb(0, 255, 0);
			_effectColors[3] = Color.FromArgb(0, 255, 255);
		}
		
		panelEffColor1.BackColor = _effectColors[0];
		panelEffColor2.BackColor = _effectColors[1];
		panelEffColor3.BackColor = _effectColors[2];
		panelEffColor4.BackColor = _effectColors[3];
	}

	private void BtnKbdApplyEffect_Click(object? sender, EventArgs e)
	{
		try {
			OmenLightingEffect selectedEffect = GetSelectedEffect(comboKbdEffect);
			byte brightness = (byte)trackKbdBrightness.Value;
			byte speed = (byte)comboKbdSpeed.SelectedIndex;
			byte direction = (byte)comboKbdDirection.SelectedIndex;
			byte size = (byte)comboKbdSize.SelectedIndex;
			int colorCount = GetColorCountForEffect(selectedEffect);
			Color[] colors = ((selectedEffect == OmenLightingEffect.Wave) ? null : _effectColors.Take(colorCount).ToArray());

			if (!_isPerKeyModeActive && selectedEffect == OmenLightingEffect.Static)
			{
				// We must tell the keyboard to enter Static mode first
				_lighting.SetKeyboardEffect(selectedEffect, brightness, speed, direction, size, colors);
				// Then apply the custom zone colors
				_lighting.SetKeyboardZoneColors(_zoneColors);
			}
			else
			{
				bool success = _lighting.SetKeyboardEffect(selectedEffect, brightness, speed, direction, size, colors);
			}
			SaveCurrentEffectState();
			UpdateKeyboardVisualizer();
		} catch (System.Exception ex) { }
	}

	private void UpdateKeyboardVisualizer()
	{
		if (_isPerKeyModeActive)
		{
			OmenLightingEffect selectedEffect = GetSelectedEffect(comboKbdEffect);
			int colorCount = GetColorCountForEffect(selectedEffect);
			if (selectedEffect == OmenLightingEffect.Wave || colorCount == 0)
			{
				keyboardVisualizer.ZoneColors = new Color[] { Color.Red, Color.Yellow, Color.Green, Color.Blue, Color.Magenta };
			}
			else
			{
				keyboardVisualizer.ZoneColors = _effectColors.Take(Math.Max(1, colorCount)).ToArray();
			}
			keyboardVisualizer.AnimationType = (int)selectedEffect;
			keyboardVisualizer.AnimationDirection = comboKbdDirection.SelectedIndex;
			keyboardVisualizer.AnimationSpeed = comboKbdSpeed.SelectedIndex == 0 ? 25 : (comboKbdSpeed.SelectedIndex == 1 ? 50 : 100);
			keyboardVisualizer.IsAnimated = (selectedEffect != OmenLightingEffect.Static);
		}
		else
		{
			keyboardVisualizer.IsAnimated = false;
			keyboardVisualizer.ZoneColors = _zoneColors;
		}
	}

	private void UpdateZonePanelColors()
	{
		string[] array = new string[0];
		if (!_hasPerKey || !_isPerKeyModeActive)
		{
			if (_zoneColors.Length == 1) array = new string[] { "Keyboard" };
			else if (_zoneColors.Length == 4) array = new string[] { "Left", "Center-Left", "Center-Right", "Right" };
		}
		foreach (Control control in groupKbdZones.Controls)
		{
			if (control is RGlowPanel rGlowPanel && rGlowPanel.Tag is int num)
			{
				rGlowPanel.GlowColor = _zoneColors[num];
				string labelText = ((num < array.Length) ? array[num] : $"Zone {num + 1}");
				rGlowPanel.LabelText = labelText;
			}
		}
	}

	private void BuildKeyboardZonePickers(int zones)
	{
		foreach (RGlowPanel item in groupKbdZones.Controls.OfType<RGlowPanel>().ToList())
		{
			groupKbdZones.Controls.Remove(item);
		}
		zones = Math.Clamp(zones, 1, 10);
		int num = Math.Max(60, (groupKbdZones.Width - 60) / Math.Max(1, zones));
		int spacing = 15;
		int height = 120;
		int startX = (groupKbdZones.Width - (num * zones + spacing * (zones - 1))) / 2;
		
		for (int i = 0; i < zones; i++)
		{
			RGlowPanel rGlowPanel = new RGlowPanel
			{
				Width = num,
				Height = height,
				Left = startX + i * (num + spacing),
				Top = 40,
				GlowColor = _zoneColors[i],
				Tag = i,
				LabelText = $"Zone {i + 1}"
			};
			rGlowPanel.Click += ZoneGlowPanel_Click;
			groupKbdZones.Controls.Add(rGlowPanel);
		}
		UpdateZonePanelColors();
	}

	private void TrackZoneCount_ValueChanged(object? sender, EventArgs e)
	{
		int num = Math.Clamp(trackZoneCount.Value, 1, 10);
		UpdateZoneCountLabel(num);
		if (_zoneColors.Length != num)
		{
			Color[] zoneColors = _zoneColors;
			_zoneColors = new Color[num];
			for (int i = 0; i < num; i++)
			{
				_zoneColors[i] = ((i < zoneColors.Length) ? zoneColors[i] : ((zoneColors.Length != 0) ? zoneColors[^1] : Color.FromArgb(0, 168, 255)));
			}
		}
		BuildKeyboardZonePickers(num);
		UpdateKeyboardVisualizer();
		BtnKbdApplyEffect_Click(null, EventArgs.Empty);
	}

	private void UpdateZoneCountLabel(int zones)
	{
		labelZoneCountValue.Text = $"{zones}";
	}

	private void ZoneGlowPanel_Click(object? sender, EventArgs e)
	{
		if (!(sender is RGlowPanel rGlowPanel)) return;
		object tag = rGlowPanel.Tag;
		if (!(tag is int)) return;
		int num = (int)tag;
		
		using GHelper.UI.ColorPickerForm colorDialog = new GHelper.UI.ColorPickerForm(_zoneColors[num]);
		if (colorDialog.ShowDialog(this) == DialogResult.OK)
		{
			_zoneColors[num] = colorDialog.SelectedColor;
			UpdateZonePanelColors();
			UpdateKeyboardVisualizer();
			BtnKbdApplyEffect_Click(null, EventArgs.Empty);
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && components != null)
		{
			components.Dispose();
		}
		base.Dispose(disposing);
	}

	private void InitializeComponent()
	{
		this.panelSegment = new System.Windows.Forms.Panel();
		this.btnZonedMode = new GHelper.UI.RButton();
		this.btnPerKeyMode = new GHelper.UI.RButton();
		this.panelVisualizer = new System.Windows.Forms.Panel();
		this.groupKbdZones = new System.Windows.Forms.Panel();
		this.keyboardVisualizer = new GHelper.UI.KeyboardVisualizer();
		this.panelSettings = new System.Windows.Forms.Panel();
		this.labelEffectType = new System.Windows.Forms.Label();
		this.comboKbdEffect = new GHelper.UI.RComboBox();
		
		this.labelEffColors = new System.Windows.Forms.Label();
		this.panelEffColors = new System.Windows.Forms.Panel();
		this.panelEffColor1 = new System.Windows.Forms.Panel();
		this.panelEffColor2 = new System.Windows.Forms.Panel();
		this.panelEffColor3 = new System.Windows.Forms.Panel();
		this.panelEffColor4 = new System.Windows.Forms.Panel();
		
		this.labelKbdSp = new System.Windows.Forms.Label();
		this.comboKbdSpeed = new GHelper.UI.RComboBox();
		this.labelKbdDirection = new System.Windows.Forms.Label();
		this.comboKbdDirection = new GHelper.UI.RComboBox();
		this.labelKbdSize = new System.Windows.Forms.Label();
		this.comboKbdSize = new GHelper.UI.RComboBox();
		
		this.labelZoneCount = new System.Windows.Forms.Label();
		this.trackZoneCount = new GHelper.UI.Slider();
		this.labelZoneCountValue = new System.Windows.Forms.Label();
		this.labelKbdBr = new System.Windows.Forms.Label();
		this.trackKbdBrightness = new GHelper.UI.Slider();
		this.labelKbdBrValue = new System.Windows.Forms.Label();
		
		base.SuspendLayout();
		
		this.Text = "Laptop Lighting";
		base.ClientSize = new System.Drawing.Size(650, 750);
		base.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
		base.MaximizeBox = false;
		base.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
		this.Font = new System.Drawing.Font("Segoe UI", 9f);
		
		this.panelSegment.Location = new System.Drawing.Point(20, 20);
		this.panelSegment.Size = new System.Drawing.Size(610, 40);
		
		this.btnZonedMode.Text = "Zoned";
		this.btnZonedMode.Size = new System.Drawing.Size(305, 40);
		this.btnZonedMode.Location = new System.Drawing.Point(0, 0);
		
		this.btnPerKeyMode.Text = "Per-Key";
		this.btnPerKeyMode.Size = new System.Drawing.Size(305, 40);
		this.btnPerKeyMode.Location = new System.Drawing.Point(305, 0);
		
		this.panelSegment.Controls.Add(this.btnZonedMode);
		this.panelSegment.Controls.Add(this.btnPerKeyMode);
		base.Controls.Add(this.panelSegment);
		
		// Increased top padding so keyboardVisualizer doesn't overlap Segmented panel above
		this.panelVisualizer.Location = new System.Drawing.Point(20, 80);
		this.panelVisualizer.Size = new System.Drawing.Size(610, 250);
		
		this.keyboardVisualizer.Location = new System.Drawing.Point(40, 50);
		this.keyboardVisualizer.Size = new System.Drawing.Size(530, 150);
		this.panelVisualizer.Controls.Add(this.keyboardVisualizer);
		
		this.groupKbdZones.Dock = DockStyle.Fill;
		this.panelVisualizer.Controls.Add(this.groupKbdZones);
		base.Controls.Add(this.panelVisualizer);
		
		this.panelSettings.Location = new System.Drawing.Point(20, 370);
		this.panelSettings.Size = new System.Drawing.Size(610, 320);
		
		this.labelEffectType.Text = "Lighting Mode";
		this.labelEffectType.AutoSize = true;
		this.labelEffectType.ForeColor = System.Drawing.Color.LightGray;
		this.labelEffectType.Location = new System.Drawing.Point(20, 20);
		
		this.comboKbdEffect.Location = new System.Drawing.Point(20, 40);
		this.comboKbdEffect.Size = new System.Drawing.Size(250, 30);
		this.comboKbdEffect.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
		
		this.labelEffColors.Text = "Color";
		this.labelEffColors.AutoSize = true;
		this.labelEffColors.ForeColor = System.Drawing.Color.LightGray;
		this.labelEffColors.Location = new System.Drawing.Point(320, 20);
		
		this.panelEffColors.Location = new System.Drawing.Point(320, 40);
		this.panelEffColors.Size = new System.Drawing.Size(250, 30);
		
		AddColorSwatch(this.panelEffColors, this.panelEffColor1, 0,   System.Drawing.Color.FromArgb(255,0,0));
		AddColorSwatch(this.panelEffColors, this.panelEffColor2, 58,  System.Drawing.Color.FromArgb(255,255,0));
		AddColorSwatch(this.panelEffColors, this.panelEffColor3, 116, System.Drawing.Color.FromArgb(0,255,0));
		AddColorSwatch(this.panelEffColors, this.panelEffColor4, 174, System.Drawing.Color.FromArgb(0,255,255));
		
		this.labelKbdSp.Text = "Speed";
		this.labelKbdSp.AutoSize = true;
		this.labelKbdSp.ForeColor = System.Drawing.Color.LightGray;
		this.labelKbdSp.Location = new System.Drawing.Point(20, 95);
		
		this.comboKbdSpeed.Location = new System.Drawing.Point(20, 115);
		this.comboKbdSpeed.Size = new System.Drawing.Size(250, 30);
		this.comboKbdSpeed.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
		this.comboKbdSpeed.Items.AddRange(new object[3] { "Slow", "Medium", "Fast" });
		
		this.labelKbdDirection.Text = "Direction";
		this.labelKbdDirection.AutoSize = true;
		this.labelKbdDirection.ForeColor = System.Drawing.Color.LightGray;
		this.labelKbdDirection.Location = new System.Drawing.Point(320, 95);
		
		this.comboKbdDirection.Location = new System.Drawing.Point(320, 115);
		this.comboKbdDirection.Size = new System.Drawing.Size(250, 30);
		this.comboKbdDirection.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
		this.comboKbdDirection.Items.AddRange(new object[4] { "Outside In", "Inside Out", "Left", "Right" });
		
		this.labelKbdSize.Text = "Size";
		this.labelKbdSize.AutoSize = true;
		this.labelKbdSize.ForeColor = System.Drawing.Color.LightGray;
		this.labelKbdSize.Location = new System.Drawing.Point(320, 95); // Assuming mutually exclusive with direction or stacked differently. We handle location dynamically in RelayoutSettingsPanel.
		
		this.comboKbdSize.Location = new System.Drawing.Point(320, 115);
		this.comboKbdSize.Size = new System.Drawing.Size(250, 30);
		this.comboKbdSize.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
		this.comboKbdSize.Items.AddRange(new object[3] { "Small", "Medium", "Large" });
		
		this.labelZoneCount.Text = "Zone Count (Zoned Mode)";
		this.labelZoneCount.AutoSize = true;
		this.labelZoneCount.ForeColor = System.Drawing.Color.LightGray;
		this.labelZoneCount.Location = new System.Drawing.Point(20, 160);
		
		this.trackZoneCount.Location = new System.Drawing.Point(20, 185);
		this.trackZoneCount.Size = new System.Drawing.Size(510, 25);
		this.labelZoneCountValue.Text = "4";
		this.labelZoneCountValue.AutoSize = true;
		this.labelZoneCountValue.ForeColor = System.Drawing.Color.White;
		this.labelZoneCountValue.Location = new System.Drawing.Point(550, 160);
		this.labelZoneCountValue.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
		
		this.labelKbdBr.Text = "Brightness";
		this.labelKbdBr.AutoSize = true;
		this.labelKbdBr.ForeColor = System.Drawing.Color.LightGray;
		this.labelKbdBr.Location = new System.Drawing.Point(20, 225);
		
		this.trackKbdBrightness.Location = new System.Drawing.Point(20, 250);
		this.trackKbdBrightness.Size = new System.Drawing.Size(510, 25);
		this.trackKbdBrightness.Min = 0;
		this.trackKbdBrightness.Max = 100;
		this.trackKbdBrightness.Step = 1;
		this.trackKbdBrightness.Value = 100;
		this.labelKbdBrValue.Text = "100%";
		this.labelKbdBrValue.AutoSize = true;
		this.labelKbdBrValue.ForeColor = System.Drawing.Color.White;
		this.labelKbdBrValue.Location = new System.Drawing.Point(550, 225);
		this.labelKbdBrValue.Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold);
		
		this.panelSettings.Controls.Add(this.labelEffectType);
		this.panelSettings.Controls.Add(this.comboKbdEffect);
		this.panelSettings.Controls.Add(this.labelEffColors);
		this.panelSettings.Controls.Add(this.panelEffColors);
		this.panelSettings.Controls.Add(this.labelKbdSp);
		this.panelSettings.Controls.Add(this.comboKbdSpeed);
		this.panelSettings.Controls.Add(this.labelKbdDirection);
		this.panelSettings.Controls.Add(this.comboKbdDirection);
		this.panelSettings.Controls.Add(this.labelKbdSize);
		this.panelSettings.Controls.Add(this.comboKbdSize);
		this.panelSettings.Controls.Add(this.labelZoneCount);
		this.panelSettings.Controls.Add(this.trackZoneCount);
		this.panelSettings.Controls.Add(this.labelZoneCountValue);
		this.panelSettings.Controls.Add(this.labelKbdBr);
		this.panelSettings.Controls.Add(this.trackKbdBrightness);
		this.panelSettings.Controls.Add(this.labelKbdBrValue);
		
		base.Controls.Add(this.panelSettings);
		
		this.panelSegment.Paint += Panel_Paint_Border;
		this.panelVisualizer.Paint += Panel_Paint_Border;
		this.panelSettings.Paint += Panel_Paint_Border;
		
		base.ResumeLayout(false);
	}

	private void Panel_Paint_Border(object? sender, PaintEventArgs e)
	{
		if (sender is Panel p)
		{
			using Pen pen = new Pen(Color.FromArgb(45, 45, 45), 1);
			e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
		}
	}

	private static void AddColorSwatch(Panel container, Panel swatch, int left, Color c)
	{
		swatch.Location = new Point(left, 2);
		swatch.Size = new Size(52, 26);
		swatch.BackColor = c;
		swatch.BorderStyle = BorderStyle.FixedSingle;
		swatch.Cursor = Cursors.Hand;
		container.Controls.Add(swatch);
	}
}
