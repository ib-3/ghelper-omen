using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using GHelper.Helpers;

namespace GHelper.UI
{
    public class ColorPickerForm : RForm
    {
        public Color SelectedColor { get; private set; }

        private PictureBox pbColorMap;
        private PictureBox pbHue;
        private Panel pnlPreview;
        private RTextBox txtHex;
        private RNumericUpDown numR, numG, numB;
        private RButton btnOk, btnCancel;


        private float currentHue = 0f;
        private float currentSat = 1f;
        private float currentVal = 1f;

        private bool isUpdatingUI = false;

        public ColorPickerForm(Color initialColor)
        {
            SelectedColor = initialColor;
            InitUI();
            InitTheme(true);
            
            UpdateFromColor(initialColor);
        }

        private void InitUI()
        {
            this.Text = "Color Picker";
            this.Size = new Size(340, 480);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            // Custom Tabs instead of TabControl to remove white border
            RButton btnAdvanced = new RButton { Text = "Advanced", Location = new Point(10, 10), Size = new Size(150, 30), Activated = true };
            RButton btnQuick = new RButton { Text = "Quick Colours", Location = new Point(160, 10), Size = new Size(150, 30), Activated = false };
            this.Controls.Add(btnAdvanced);
            this.Controls.Add(btnQuick);

            Panel tpAdvanced = new Panel { Location = new Point(10, 45), Size = new Size(300, 185) };
            Panel tpQuick = new Panel { Location = new Point(10, 45), Size = new Size(300, 185), Visible = false };
            this.Controls.Add(tpAdvanced);
            this.Controls.Add(tpQuick);

            btnAdvanced.Click += (s, e) => {
                btnAdvanced.Activated = true;
                btnQuick.Activated = false;
                tpAdvanced.Visible = true;
                tpQuick.Visible = false;
            };

            btnQuick.Click += (s, e) => {
                btnAdvanced.Activated = false;
                btnQuick.Activated = true;
                tpAdvanced.Visible = false;
                tpQuick.Visible = true;
            };

            // Color Map (Saturation vs Value)
            pbColorMap = new PictureBox
            {
                Location = new Point(10, 10),
                Size = new Size(200, 170),
                Cursor = Cursors.Cross
            };
            pbColorMap.Paint += PbColorMap_Paint;
            pbColorMap.MouseMove += PbColorMap_MouseMove;
            pbColorMap.MouseDown += PbColorMap_MouseMove;
            tpAdvanced.Controls.Add(pbColorMap);

            // Hue Slider
            pbHue = new PictureBox
            {
                Location = new Point(220, 10),
                Size = new Size(30, 170),
                Cursor = Cursors.Hand
            };
            pbHue.Paint += PbHue_Paint;
            pbHue.MouseMove += PbHue_MouseMove;
            pbHue.MouseDown += PbHue_MouseMove;
            tpAdvanced.Controls.Add(pbHue);

            // Quick Colors Grid
            Color[] quickColors = {
                Color.Red, Color.Orange, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue,
                Color.Magenta, Color.Purple, Color.Pink, Color.Teal, Color.Olive, Color.Maroon,
                Color.Navy, Color.DeepSkyBlue, Color.SpringGreen, Color.Gold, Color.Coral, Color.HotPink,
                Color.White, Color.LightGray, Color.Gray, Color.DarkGray, Color.Black, Color.Brown
            };
            
            for (int i = 0; i < quickColors.Length; i++)
            {
                Color c = quickColors[i];
                int col = i % 6;
                int row = i / 6;
                Panel presetPanel = new Panel
                {
                    Location = new Point(25 + col * 40, 15 + row * 40),
                    Size = new Size(30, 30),
                    BackColor = c,
                    Cursor = Cursors.Hand,
                    BorderStyle = BorderStyle.FixedSingle
                };
                presetPanel.Click += (s, e) => UpdateFromColor(c);
                tpQuick.Controls.Add(presetPanel);
            }

            // Preview
            pnlPreview = new Panel
            {
                Location = new Point(20, 240),
                Size = new Size(60, 60),
                BorderStyle = BorderStyle.FixedSingle
            };
            this.Controls.Add(pnlPreview);

            // Hex Input
            Label lblHex = new Label { Text = "HEX:", Location = new Point(100, 240), Size = new Size(40, 20) };
            this.Controls.Add(lblHex);

            txtHex = new RTextBox
            {
                Location = new Point(150, 237),
                Size = new Size(110, 25),
            };
            txtHex.TextChanged += TxtHex_TextChanged;
            this.Controls.Add(txtHex);

            // RGB Inputs
            int rgbY = 275;
            Label lblR = new Label { Text = "R:", Location = new Point(100, rgbY), Size = new Size(20, 20) };
            numR = new RNumericUpDown { Location = new Point(120, rgbY - 3), Size = new Size(50, 25), Minimum = 0, Maximum = 255 };
            numR.ValueChanged += NumericRGB_ValueChanged;
            this.Controls.Add(lblR);
            this.Controls.Add(numR);

            Label lblG = new Label { Text = "G:", Location = new Point(175, rgbY), Size = new Size(20, 20) };
            numG = new RNumericUpDown { Location = new Point(195, rgbY - 3), Size = new Size(50, 25), Minimum = 0, Maximum = 255 };
            numG.ValueChanged += NumericRGB_ValueChanged;
            this.Controls.Add(lblG);
            this.Controls.Add(numG);

            Label lblB = new Label { Text = "B:", Location = new Point(250, rgbY), Size = new Size(20, 20) };
            numB = new RNumericUpDown { Location = new Point(270, rgbY - 3), Size = new Size(50, 25), Minimum = 0, Maximum = 255 };
            numB.ValueChanged += NumericRGB_ValueChanged;
            this.Controls.Add(lblB);
            this.Controls.Add(numB);

            // Buttons
            btnOk = new RButton { Text = "OK", Location = new Point(40, 320), Size = new Size(100, 35) };
            btnOk.Click += (s, e) => { this.DialogResult = DialogResult.OK; this.Close(); };
            this.Controls.Add(btnOk);

            btnCancel = new RButton { Text = "Cancel", Location = new Point(160, 320), Size = new Size(100, 35) };
            btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };
            this.Controls.Add(btnCancel);
            
            this.Size = new Size(340, 420);
        }

        private void UpdateFromColor(Color color)
        {
            if (isUpdatingUI) return;
            isUpdatingUI = true;

            SelectedColor = color;
            pnlPreview.BackColor = color;

            var hsv = ColorUtils.HSV.ToHSV(color);
            currentHue = (float)hsv.Hue;
            currentSat = (float)hsv.Saturation;
            currentVal = (float)hsv.Value;

            txtHex.Text = ColorTranslator.ToHtml(color);
            numR.Value = color.R;
            numG.Value = color.G;
            numB.Value = color.B;

            pbColorMap.Invalidate();
            pbHue.Invalidate();

            isUpdatingUI = false;
        }

        private void UpdateFromHSV()
        {
            if (isUpdatingUI) return;
            
            var hsv = new ColorUtils.HSV { Hue = currentHue, Saturation = currentSat, Value = currentVal };
            Color c = hsv.ToRGB();
            UpdateFromColor(c);
        }

        private void TxtHex_TextChanged(object sender, EventArgs e)
        {
            if (isUpdatingUI) return;
            try
            {
                string hex = txtHex.Text.StartsWith("#") ? txtHex.Text : "#" + txtHex.Text;
                if (hex.Length == 7)
                {
                    Color c = ColorTranslator.FromHtml(hex);
                    UpdateFromColor(c);
                }
            }
            catch { }
        }

        private void NumericRGB_ValueChanged(object sender, EventArgs e)
        {
            if (isUpdatingUI) return;
            Color c = Color.FromArgb(255, (int)numR.Value, (int)numG.Value, (int)numB.Value);
            UpdateFromColor(c);
        }

        private void PbHue_Paint(object sender, PaintEventArgs e)
        {
            LinearGradientBrush brush = new LinearGradientBrush(pbHue.ClientRectangle, Color.Red, Color.Red, LinearGradientMode.Vertical);
            ColorBlend blend = new ColorBlend();
            blend.Colors = new Color[] { Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta, Color.Red };
            blend.Positions = new float[] { 0f, 1/6f, 2/6f, 3/6f, 4/6f, 5/6f, 1f };
            brush.InterpolationColors = blend;
            e.Graphics.FillRectangle(brush, pbHue.ClientRectangle);

            int y = (int)(currentHue * pbHue.Height);
            e.Graphics.DrawLine(Pens.Black, 0, y, pbHue.Width, y);
            e.Graphics.DrawLine(Pens.White, 0, y+1, pbHue.Width, y+1);
        }

        private void PbHue_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                currentHue = Math.Max(0f, Math.Min(1f, (float)e.Y / pbHue.Height));
                pbHue.Invalidate();
                pbColorMap.Invalidate();
                UpdateFromHSV();
            }
        }

        private void PbColorMap_Paint(object sender, PaintEventArgs e)
        {
            Color pureHue = new ColorUtils.HSV { Hue = currentHue, Saturation = 1, Value = 1 }.ToRGB();
            
            using (PathGradientBrush pgb = new PathGradientBrush(new Point[] {
                new Point(pbColorMap.Width, 0),
                new Point(pbColorMap.Width, pbColorMap.Height),
                new Point(0, pbColorMap.Height),
                new Point(0, 0)
            }))
            {
                pgb.CenterColor = pureHue;
                pgb.CenterPoint = new PointF(pbColorMap.Width, 0);
                pgb.SurroundColors = new Color[] { pureHue, Color.Black, Color.Black, Color.White };
                e.Graphics.FillRectangle(pgb, pbColorMap.ClientRectangle);
            }

            int x = (int)(currentSat * pbColorMap.Width);
            int y = (int)((1 - currentVal) * pbColorMap.Height);

            e.Graphics.DrawEllipse(Pens.Black, x - 4, y - 4, 8, 8);
            e.Graphics.DrawEllipse(Pens.White, x - 3, y - 3, 6, 6);
        }

        private void PbColorMap_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                currentSat = Math.Max(0f, Math.Min(1f, (float)e.X / pbColorMap.Width));
                currentVal = 1f - Math.Max(0f, Math.Min(1f, (float)e.Y / pbColorMap.Height));
                pbColorMap.Invalidate();
                UpdateFromHSV();
            }
        }
    }
}
