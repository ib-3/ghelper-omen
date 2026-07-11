namespace GHelper
{
    partial class OmenLightingForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.tabControl = new TabControl();
            this.tabKeyboard = new TabPage();
            this.tabLightBar = new TabPage();

            // ── Keyboard tab controls ────────────────────────────────────
            this.labelKbdStatus  = new Label();
            this.checkKbdBacklight = new CheckBox();
            this.groupKbdZones   = new GroupBox();
            this.panelZones      = new Panel();
            this.panelZone0      = new Panel();
            this.panelZone1      = new Panel();
            this.panelZone2      = new Panel();
            this.panelZone3      = new Panel();
            this.labelZone0      = new Label();
            this.labelZone1      = new Label();
            this.labelZone2      = new Label();
            this.labelZone3      = new Label();
            this.btnKbdApplyZones = new Button();

            this.groupKbdEffect   = new GroupBox();
            this.labelEffectType  = new Label();
            this.comboKbdEffect   = new ComboBox();
            this.labelKbdBr       = new Label();
            this.trackKbdBrightness = new TrackBar();
            this.labelKbdSp       = new Label();
            this.trackKbdSpeed    = new TrackBar();
            this.panelEffColors   = new Panel();
            this.panelEffColor1   = new Panel();
            this.panelEffColor2   = new Panel();
            this.panelEffColor3   = new Panel();
            this.panelEffColor4   = new Panel();
            this.labelEffColors   = new Label();
            this.btnKbdApplyEffect = new Button();

            // ── LightBar tab controls ────────────────────────────────────
            this.labelLbStatus    = new Label();
            this.groupLbZones     = new GroupBox();
            this.panelLbZones     = new Panel();
            this.groupLbEffect    = new GroupBox();
            this.labelLbEffType   = new Label();
            this.comboLbEffect    = new ComboBox();
            this.labelLbBr        = new Label();
            this.trackLbBrightness = new TrackBar();
            this.labelLbSp        = new Label();
            this.trackLbSpeed     = new TrackBar();
            this.panelLbEffColors = new Panel();
            this.panelLbEffColor1 = new Panel();
            this.panelLbEffColor2 = new Panel();
            this.panelLbEffColor3 = new Panel();
            this.panelLbEffColor4 = new Panel();
            this.labelLbEffColors = new Label();
            this.btnLbApply       = new Button();

            this.SuspendLayout();

            // ════════════════════════════════════════════════════════════
            // Form
            // ════════════════════════════════════════════════════════════
            this.Text            = "OMEN Lighting";
            this.ClientSize      = new Size(520, 440);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox     = false;
            this.StartPosition   = FormStartPosition.CenterParent;
            this.Font            = new Font("Segoe UI", 9F);
            this.Padding         = new Padding(8);

            // ════════════════════════════════════════════════════════════
            // TabControl
            // ════════════════════════════════════════════════════════════
            this.tabControl.Dock = DockStyle.Fill;
            this.tabControl.Controls.Add(this.tabKeyboard);
            this.tabControl.Controls.Add(this.tabLightBar);
            this.Controls.Add(this.tabControl);

            // ════════════════════════════════════════════════════════════
            // TAB: Keyboard
            // ════════════════════════════════════════════════════════════
            this.tabKeyboard.Text    = "⌨  Keyboard";
            this.tabKeyboard.Padding = new Padding(8);

            // Status label
            this.labelKbdStatus.Text      = "Detecting…";
            this.labelKbdStatus.Dock      = DockStyle.Top;
            this.labelKbdStatus.Height    = 22;
            this.labelKbdStatus.Font      = new Font("Segoe UI", 9F, FontStyle.Bold);

            // Backlight toggle
            this.checkKbdBacklight.Text     = "Backlight on";
            this.checkKbdBacklight.Checked  = true;
            this.checkKbdBacklight.Location = new Point(8, 26);
            this.checkKbdBacklight.AutoSize = true;

            // ── Zone colour group ─────────────────────────────────────────
            this.groupKbdZones.Text     = "Zone Colors  (click a zone to change)";
            this.groupKbdZones.Location = new Point(8, 52);
            this.groupKbdZones.Size     = new Size(492, 90);

            // Zone panels (4 × side by side)
            int zW = 110, zH = 50, zTop = 24, zGap = 4;
            ConfigureZonePanel(this.panelZone0, this.labelZone0, "Right",  zTop, 8,         zW, zH, Color.FromArgb(0,120,255));
            ConfigureZonePanel(this.panelZone1, this.labelZone1, "Mid-R",  zTop, 8+zW+zGap, zW, zH, Color.FromArgb(0,200,120));
            ConfigureZonePanel(this.panelZone2, this.labelZone2, "Mid-L",  zTop, 8+2*(zW+zGap), zW, zH, Color.FromArgb(255,120,0));
            ConfigureZonePanel(this.panelZone3, this.labelZone3, "WASD",   zTop, 8+3*(zW+zGap), zW, zH, Color.FromArgb(200,0,255));
            this.groupKbdZones.Controls.Add(this.panelZone0);
            this.groupKbdZones.Controls.Add(this.panelZone1);
            this.groupKbdZones.Controls.Add(this.panelZone2);
            this.groupKbdZones.Controls.Add(this.panelZone3);

            this.btnKbdApplyZones.Text     = "Apply Zones";
            this.btnKbdApplyZones.Location = new Point(8+4*(zW+zGap), zTop+2);
            // Push into panel
            this.btnKbdApplyZones.Location = new Point(374, 26);
            this.btnKbdApplyZones.Size     = new Size(112, 46);
            this.btnKbdApplyZones.Font     = new Font("Segoe UI", 9F, FontStyle.Bold);
            this.groupKbdZones.Controls.Add(this.btnKbdApplyZones);

            // panelZones wraps the group
            this.panelZones.Controls.Add(this.groupKbdZones);
            this.panelZones.Location = new Point(0, 48);
            this.panelZones.Size     = new Size(504, 100);

            // ── Effect group ─────────────────────────────────────────────
            this.groupKbdEffect.Text     = "Lighting Effect";
            this.groupKbdEffect.Location = new Point(8, 155);
            this.groupKbdEffect.Size     = new Size(492, 190);

            // Effect type row
            this.labelEffectType.Text     = "Effect:";
            this.labelEffectType.Location = new Point(8, 24);
            this.labelEffectType.Size     = new Size(50, 22);
            this.comboKbdEffect.Location  = new Point(62, 22);
            this.comboKbdEffect.Size      = new Size(200, 24);
            this.comboKbdEffect.DropDownStyle = ComboBoxStyle.DropDownList;
            this.groupKbdEffect.Controls.Add(this.labelEffectType);
            this.groupKbdEffect.Controls.Add(this.comboKbdEffect);

            // Brightness
            this.labelKbdBr.Text          = "Brightness:";
            this.labelKbdBr.Location      = new Point(8, 52);
            this.labelKbdBr.Size          = new Size(70, 18);
            this.trackKbdBrightness.Location = new Point(80, 48);
            this.trackKbdBrightness.Size  = new Size(180, 24);
            this.trackKbdBrightness.Minimum = 0;
            this.trackKbdBrightness.Maximum = 100;
            this.trackKbdBrightness.Value   = 100;
            this.trackKbdBrightness.TickFrequency = 10;
            this.groupKbdEffect.Controls.Add(this.labelKbdBr);
            this.groupKbdEffect.Controls.Add(this.trackKbdBrightness);

            // Speed
            this.labelKbdSp.Text          = "Speed:";
            this.labelKbdSp.Location      = new Point(8, 80);
            this.labelKbdSp.Size          = new Size(70, 18);
            this.trackKbdSpeed.Location   = new Point(80, 76);
            this.trackKbdSpeed.Size       = new Size(180, 24);
            this.trackKbdSpeed.Minimum    = 0;
            this.trackKbdSpeed.Maximum    = 10;
            this.trackKbdSpeed.Value      = 5;
            this.trackKbdSpeed.TickFrequency = 1;
            this.groupKbdEffect.Controls.Add(this.labelKbdSp);
            this.groupKbdEffect.Controls.Add(this.trackKbdSpeed);

            // Effect colours
            this.labelEffColors.Text      = "Colors:";
            this.labelEffColors.Location  = new Point(8, 108);
            this.labelEffColors.Size      = new Size(50, 18);
            this.panelEffColors.Location  = new Point(62, 104);
            this.panelEffColors.Size      = new Size(230, 30);
            AddColorSwatch(this.panelEffColors, this.panelEffColor1, 0,   Color.FromArgb(255,0,80));
            AddColorSwatch(this.panelEffColors, this.panelEffColor2, 58,  Color.FromArgb(255,120,0));
            AddColorSwatch(this.panelEffColors, this.panelEffColor3, 116, Color.FromArgb(0,200,255));
            AddColorSwatch(this.panelEffColors, this.panelEffColor4, 174, Color.FromArgb(100,0,255));
            this.groupKbdEffect.Controls.Add(this.labelEffColors);
            this.groupKbdEffect.Controls.Add(this.panelEffColors);

            // Apply button
            this.btnKbdApplyEffect.Text     = "Apply Effect";
            this.btnKbdApplyEffect.Location = new Point(370, 22);
            this.btnKbdApplyEffect.Size     = new Size(112, 36);
            this.btnKbdApplyEffect.Font     = new Font("Segoe UI", 9F, FontStyle.Bold);
            this.groupKbdEffect.Controls.Add(this.btnKbdApplyEffect);

            // Assemble keyboard tab
            this.tabKeyboard.Controls.Add(this.labelKbdStatus);
            this.tabKeyboard.Controls.Add(this.checkKbdBacklight);
            this.tabKeyboard.Controls.Add(this.panelZones);
            this.tabKeyboard.Controls.Add(this.groupKbdEffect);

            // ════════════════════════════════════════════════════════════
            // TAB: Light Bar
            // ════════════════════════════════════════════════════════════
            this.tabLightBar.Text    = "💡 Light Bar";
            this.tabLightBar.Padding = new Padding(8);

            this.labelLbStatus.Text   = "Detecting…";
            this.labelLbStatus.Dock   = DockStyle.Top;
            this.labelLbStatus.Height = 22;
            this.labelLbStatus.Font   = new Font("Segoe UI", 9F, FontStyle.Bold);

            // Zone picker panel (built dynamically in code)
            this.groupLbZones.Text     = "Light Bar Zones  (click to change color)";
            this.groupLbZones.Location = new Point(8, 26);
            this.groupLbZones.Size     = new Size(492, 72);
            this.panelLbZones.Location = new Point(8, 20);
            this.panelLbZones.Size     = new Size(476, 44);
            this.groupLbZones.Controls.Add(this.panelLbZones);

            // Effect group
            this.groupLbEffect.Text     = "Lightbar Effect";
            this.groupLbEffect.Location = new Point(8, 106);
            this.groupLbEffect.Size     = new Size(492, 190);

            this.labelLbEffType.Text     = "Effect:";
            this.labelLbEffType.Location = new Point(8, 24);
            this.labelLbEffType.Size     = new Size(50, 22);
            this.comboLbEffect.Location  = new Point(62, 22);
            this.comboLbEffect.Size      = new Size(200, 24);
            this.comboLbEffect.DropDownStyle = ComboBoxStyle.DropDownList;
            this.groupLbEffect.Controls.Add(this.labelLbEffType);
            this.groupLbEffect.Controls.Add(this.comboLbEffect);

            this.labelLbBr.Text          = "Brightness:";
            this.labelLbBr.Location      = new Point(8, 52);
            this.labelLbBr.Size          = new Size(70, 18);
            this.trackLbBrightness.Location = new Point(80, 48);
            this.trackLbBrightness.Size  = new Size(180, 24);
            this.trackLbBrightness.Minimum = 0;
            this.trackLbBrightness.Maximum = 100;
            this.trackLbBrightness.Value   = 100;
            this.trackLbBrightness.TickFrequency = 10;
            this.groupLbEffect.Controls.Add(this.labelLbBr);
            this.groupLbEffect.Controls.Add(this.trackLbBrightness);

            this.labelLbSp.Text          = "Speed:";
            this.labelLbSp.Location      = new Point(8, 80);
            this.labelLbSp.Size          = new Size(70, 18);
            this.trackLbSpeed.Location   = new Point(80, 76);
            this.trackLbSpeed.Size       = new Size(180, 24);
            this.trackLbSpeed.Minimum    = 0;
            this.trackLbSpeed.Maximum    = 10;
            this.trackLbSpeed.Value      = 5;
            this.trackLbSpeed.TickFrequency = 1;
            this.groupLbEffect.Controls.Add(this.labelLbSp);
            this.groupLbEffect.Controls.Add(this.trackLbSpeed);

            this.labelLbEffColors.Text     = "Colors:";
            this.labelLbEffColors.Location = new Point(8, 108);
            this.labelLbEffColors.Size     = new Size(50, 18);
            this.panelLbEffColors.Location = new Point(62, 104);
            this.panelLbEffColors.Size     = new Size(230, 30);
            AddColorSwatch(this.panelLbEffColors, this.panelLbEffColor1, 0,   Color.FromArgb(0,180,255));
            AddColorSwatch(this.panelLbEffColors, this.panelLbEffColor2, 58,  Color.FromArgb(0,255,120));
            AddColorSwatch(this.panelLbEffColors, this.panelLbEffColor3, 116, Color.FromArgb(180,0,255));
            AddColorSwatch(this.panelLbEffColors, this.panelLbEffColor4, 174, Color.FromArgb(255,60,0));
            this.groupLbEffect.Controls.Add(this.labelLbEffColors);
            this.groupLbEffect.Controls.Add(this.panelLbEffColors);

            this.btnLbApply.Text     = "Apply";
            this.btnLbApply.Location = new Point(370, 22);
            this.btnLbApply.Size     = new Size(112, 36);
            this.btnLbApply.Font     = new Font("Segoe UI", 9F, FontStyle.Bold);
            this.groupLbEffect.Controls.Add(this.btnLbApply);

            this.tabLightBar.Controls.Add(this.labelLbStatus);
            this.tabLightBar.Controls.Add(this.groupLbZones);
            this.tabLightBar.Controls.Add(this.groupLbEffect);

            this.ResumeLayout(false);
        }

        // ── Layout helpers ────────────────────────────────────────────────

        private static void ConfigureZonePanel(Panel p, Label lbl, string text,
            int top, int left, int w, int h, Color c)
        {
            p.Location    = new Point(left, top);
            p.Size        = new Size(w, h);
            p.BackColor   = c;
            p.BorderStyle = BorderStyle.FixedSingle;
            p.Cursor      = Cursors.Hand;
            lbl.Text      = text;
            lbl.Dock      = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.ForeColor = Color.White;
            p.Controls.Add(lbl);
        }

        private static void AddColorSwatch(Panel container, Panel swatch,
            int left, Color c)
        {
            swatch.Location    = new Point(left, 2);
            swatch.Size        = new Size(52, 26);
            swatch.BackColor   = c;
            swatch.BorderStyle = BorderStyle.FixedSingle;
            swatch.Cursor      = Cursors.Hand;
            container.Controls.Add(swatch);
        }

        #endregion

        // ── Control declarations ──────────────────────────────────────────
        private TabControl tabControl;
        private TabPage tabKeyboard;
        private TabPage tabLightBar;

        private Label labelKbdStatus;
        private CheckBox checkKbdBacklight;
        private Panel panelZones;
        private GroupBox groupKbdZones;
        private Panel panelZone0, panelZone1, panelZone2, panelZone3;
        private Label labelZone0, labelZone1, labelZone2, labelZone3;
        private Button btnKbdApplyZones;

        private GroupBox groupKbdEffect;
        private Label labelEffectType;
        private ComboBox comboKbdEffect;
        private Label labelKbdBr;
        private TrackBar trackKbdBrightness;
        private Label labelKbdSp;
        private TrackBar trackKbdSpeed;
        private Panel panelEffColors;
        private Panel panelEffColor1, panelEffColor2, panelEffColor3, panelEffColor4;
        private Label labelEffColors;
        private Button btnKbdApplyEffect;

        private Label labelLbStatus;
        private GroupBox groupLbZones;
        private Panel panelLbZones;
        private GroupBox groupLbEffect;
        private Label labelLbEffType;
        private ComboBox comboLbEffect;
        private Label labelLbBr;
        private TrackBar trackLbBrightness;
        private Label labelLbSp;
        private TrackBar trackLbSpeed;
        private Panel panelLbEffColors;
        private Panel panelLbEffColor1, panelLbEffColor2, panelLbEffColor3, panelLbEffColor4;
        private Label labelLbEffColors;
        private Button btnLbApply;
    }
}
