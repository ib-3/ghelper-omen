using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GHelper.UI;

public class RGlowPanel : Panel
{
	private Color _glowColor = Color.FromArgb(0, 168, 255);

	private float _glowIntensity = 1f;

	private bool _selected;

	private string _labelText = "";

	private int _cornerRadius = 6;

	public Color GlowColor
	{
		get
		{
			return _glowColor;
		}
		set
		{
			_glowColor = value;
			Invalidate();
		}
	}

	public float GlowIntensity
	{
		get
		{
			return _glowIntensity;
		}
		set
		{
			_glowIntensity = Math.Clamp(value, 0f, 1f);
			Invalidate();
		}
	}

	public bool Selected
	{
		get
		{
			return _selected;
		}
		set
		{
			_selected = value;
			Invalidate();
		}
	}

	public string LabelText
	{
		get
		{
			return _labelText;
		}
		set
		{
			_labelText = value;
			Invalidate();
		}
	}

	public RGlowPanel()
	{
		DoubleBuffered = true;
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		Cursor = Cursors.Hand;
		BackColor = Color.Transparent;
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		Graphics graphics = e.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		int num = 8;
		Rectangle rect = new Rectangle(num, num, base.Width - num * 2, base.Height - num * 2);
		int num2 = (int)((float)(int)GlowColor.R * _glowIntensity);
		int num3 = (int)((float)(int)GlowColor.G * _glowIntensity);
		int num4 = (int)((float)(int)GlowColor.B * _glowIntensity);
		Color color = Color.FromArgb(num2, num3, num4);
		if (_glowIntensity > 0.05f)
		{
			for (int num5 = 3; num5 >= 1; num5--)
			{
				int num6 = num5 * 3;
				int value = (int)(40f * _glowIntensity / (float)num5);
				value = Math.Clamp(value, 0, 255);
				Rectangle rect2 = Rectangle.Inflate(rect, num6, num6);
				using SolidBrush brush = new SolidBrush(Color.FromArgb(value, num2, num3, num4));
				using GraphicsPath path = CreateRoundedRect(rect2, _cornerRadius + num6);
				graphics.FillPath(brush, path);
			}
		}
		using (GraphicsPath path2 = CreateRoundedRect(rect, _cornerRadius))
		{
			using SolidBrush brush2 = new SolidBrush((_glowIntensity > 0.05f) ? color : Color.FromArgb(28, 28, 28));
			graphics.FillPath(brush2, path2);
			if (_selected)
			{
				using Pen pen = new Pen(Color.White, 2f);
				graphics.DrawPath(pen, path2);
			}
			else
			{
				using Pen pen2 = new Pen(Color.FromArgb(60, 60, 60), 1f);
				graphics.DrawPath(pen2, path2);
			}
		}
		if (string.IsNullOrEmpty(_labelText))
		{
			return;
		}
		Color color2 = (((num2 * 299 + num3 * 587 + num4 * 114) / 1000 < 128 || _glowIntensity < 0.3f) ? Color.White : Color.FromArgb(20, 20, 20));
		using Font font = new Font("Segoe UI", 8f, FontStyle.Regular);
		SizeF sizeF = graphics.MeasureString(_labelText, font);
		float x = (float)rect.X + ((float)rect.Width - sizeF.Width) / 2f;
		float y = (float)rect.Y + ((float)rect.Height - sizeF.Height) / 2f;
		using SolidBrush brush3 = new SolidBrush(color2);
		graphics.DrawString(_labelText, font, brush3, x, y);
	}

	private static GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
	{
		GraphicsPath graphicsPath = new GraphicsPath();
		int num = radius * 2;
		graphicsPath.AddArc(rect.X, rect.Y, num, num, 180f, 90f);
		graphicsPath.AddArc(rect.Right - num, rect.Y, num, num, 270f, 90f);
		graphicsPath.AddArc(rect.Right - num, rect.Bottom - num, num, num, 0f, 90f);
		graphicsPath.AddArc(rect.X, rect.Bottom - num, num, num, 90f, 90f);
		graphicsPath.CloseFigure();
		return graphicsPath;
	}
}
