using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GHelper.UI;

public class KeyboardVisualizer : Control
{
	private new static readonly float[][] Layout = new float[5][]
	{
		new float[14]
		{
			1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
			1f, 1f, 1f, 2f
		},
		new float[14]
		{
			1.5f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
			1f, 1f, 1f, 1.5f
		},
		new float[13]
		{
			1.75f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
			1f, 1f, 2.25f
		},
		new float[12]
		{
			2.25f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
			1f, 2.75f
		},
		new float[8] { 1.25f, 1.25f, 1.25f, 6.25f, 1.25f, 1.25f, 1.25f, 1.25f }
	};

	private Color[] _zoneColors = new Color[4]
	{
		Color.FromArgb(0, 168, 255),
		Color.FromArgb(0, 168, 255),
		Color.FromArgb(0, 168, 255),
		Color.FromArgb(0, 168, 255)
	};

	private float _brightness = 1f;

	private System.Windows.Forms.Timer _animTimer;
	private float _animTime = 0f;

	public bool IsAnimated { get; set; } = false;
	public int AnimationType { get; set; } = 0;
	public int AnimationDirection { get; set; } = 0;
	public int AnimationSpeed { get; set; } = 0;

	public Color[] ZoneColors
	{
		get
		{
			return _zoneColors;
		}
		set
		{
			_zoneColors = value ?? new Color[1] { Color.FromArgb(0, 168, 255) };
			Invalidate();
		}
	}

	public float Brightness
	{
		get
		{
			return _brightness;
		}
		set
		{
			_brightness = Math.Clamp(value, 0f, 1f);
			Invalidate();
		}
	}

	public KeyboardVisualizer()
	{
		DoubleBuffered = true;
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		base.Height = 150;

		_animTimer = new System.Windows.Forms.Timer();
		_animTimer.Interval = 33;
		_animTimer.Tick += (s, e) =>
		{
			if (IsAnimated && _zoneColors.Length > 0 && Visible)
			{
				_animTime += 1f + (AnimationSpeed * 0.05f);
				Invalidate();
			}
		};
		_animTimer.Start();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_animTimer?.Stop();
			_animTimer?.Dispose();
		}
		base.Dispose(disposing);
	}

	private Color BlendColor(Color c1, Color c2, float blend)
	{
		blend = Math.Clamp(blend, 0f, 1f);
		int r = (int)(c1.R + (c2.R - c1.R) * blend);
		int g = (int)(c1.G + (c2.G - c1.G) * blend);
		int b = (int)(c1.B + (c2.B - c1.B) * blend);
		return Color.FromArgb(r, g, b);
	}

	private Color GetAnimatedColor(int keyX, int keyY, float normalizedX, float normalizedY)
	{
		if (_zoneColors == null || _zoneColors.Length == 0) return Color.Black;

		switch (AnimationType)
		{
			case 2: // ColorCycle
				int idx = (int)((_animTime / 30f) % _zoneColors.Length);
				int nextIdx = (idx + 1) % _zoneColors.Length;
				float blend = (_animTime / 30f) - (float)Math.Floor(_animTime / 30f);
				return BlendColor(_zoneColors[idx], _zoneColors[nextIdx], blend);

			case 3: // Wave
				float waveOffset = 0f;
				if (AnimationDirection == 2) waveOffset = normalizedX * 100f;
				else if (AnimationDirection == 3) waveOffset = (1f - normalizedX) * 100f;
				else if (AnimationDirection == 0) waveOffset = Math.Abs(0.5f - normalizedX) * 200f;
				else if (AnimationDirection == 1) waveOffset = (0.5f - Math.Abs(0.5f - normalizedX)) * 200f;
				int wIdx = (int)(((_animTime + waveOffset) / 20f) % _zoneColors.Length);
				int wNextIdx = (wIdx + 1) % _zoneColors.Length;
				float wBlend = ((_animTime + waveOffset) / 20f) - (float)Math.Floor((_animTime + waveOffset) / 20f);
				return BlendColor(_zoneColors[wIdx], _zoneColors[wNextIdx], wBlend);

			case 1: // Breathing
				float bPulse = (float)Math.Sin(_animTime / 20f) * 0.5f + 0.5f;
				Color bBase = _zoneColors[0];
				return Color.FromArgb((int)(bBase.R * bPulse), (int)(bBase.G * bPulse), (int)(bBase.B * bPulse));

			case 18: // Swipe
				float sOffset = 0f;
				if (AnimationDirection == 2) sOffset = normalizedX * 100f;
				else if (AnimationDirection == 3) sOffset = (1f - normalizedX) * 100f;
				else if (AnimationDirection == 0) sOffset = Math.Abs(0.5f - normalizedX) * 200f;
				else if (AnimationDirection == 1) sOffset = (0.5f - Math.Abs(0.5f - normalizedX)) * 200f;
				int sIdx = (int)(((_animTime + sOffset) / 40f) % _zoneColors.Length);
				return _zoneColors[sIdx];

			case 10: // Starlight
			case 14: // Raindrop
			case 16: // Confetti
				int noise = (keyX * 31 + keyY * 17 + (int)_animTime) % 100;
				if (noise > 95) return _zoneColors[noise % _zoneColors.Length];
				return Color.FromArgb(20, 20, 20);

			case 17: // Sun
				float dist = (float)Math.Sqrt(Math.Pow(normalizedX - 0.5f, 2) + Math.Pow(normalizedY - 0.5f, 2));
				float sunPulse = (float)Math.Sin(_animTime / 15f - dist * 10f) * 0.5f + 0.5f;
				return Color.FromArgb((int)(255 * sunPulse), (int)(200 * sunPulse), 0);

			case 11: // Ghosting
			case 12: // Ripple
				float rPulse = (float)Math.Sin(_animTime / 10f - normalizedX * 10f - normalizedY * 10f) * 0.5f + 0.5f;
				Color rBase = _zoneColors[0];
				return Color.FromArgb((int)(rBase.R * rPulse), (int)(rBase.G * rPulse), (int)(rBase.B * rPulse));

			default:
				int defIdx = ((_zoneColors.Length != 1) ? Math.Clamp((int)(normalizedX * (float)_zoneColors.Length), 0, _zoneColors.Length - 1) : 0);
				return _zoneColors[defIdx];
		}
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		Graphics graphics = e.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		graphics.Clear(Color.Transparent);
		float num = 15f;
		int num2 = 2;
		float num3 = (float)base.Width - 20f;
		float num4 = (num3 - (float)(num2 * 13)) / num;
		float num5 = Math.Max(16f, ((float)base.Height - 20f - (float)(num2 * 4)) / 5f);
		float num6 = 10f;
		float num7 = 10f;
		Color color = Color.FromArgb(28, 28, 28);
		using SolidBrush brush = new SolidBrush(color);
		for (int i = 0; i < Layout.Length; i++)
		{
			float num8 = num6;
			float y = num7 + (float)i * (num5 + (float)num2);
			float num9 = 0f;
			for (int j = 0; j < Layout[i].Length; j++)
			{
				num9 += Layout[i][j];
			}
			float num10 = num9 * num4 + (float)((Layout[i].Length - 1) * num2);
			float num11 = (num3 - num10) / 2f;
			num8 += num11;
			for (int k = 0; k < Layout[i].Length; k++)
			{
				float num12 = Layout[i][k] * num4 + ((Layout[i][k] > 1f) ? ((Layout[i][k] - 1f) * (float)num2 * 0.1f) : 0f);
				RectangleF value = new RectangleF(num8, y, num12, num5);
				Rectangle rect = Rectangle.Round(value);
				float num13 = num8 + num12 / 2f;
				float num14 = (num13 - num6) / num3;
				float normY = (y + num5 / 2f) / (float)base.Height;

				Color color2;
				if (IsAnimated)
				{
					color2 = GetAnimatedColor(k, i, num14, normY);
				}
				else
				{
					int num15 = ((_zoneColors.Length != 1) ? Math.Clamp((int)(num14 * (float)_zoneColors.Length), 0, _zoneColors.Length - 1) : 0);
					color2 = _zoneColors[num15];
				}
				int red = (int)((float)(int)color2.R * _brightness);
				int green = (int)((float)(int)color2.G * _brightness);
				int blue = (int)((float)(int)color2.B * _brightness);
				if (_brightness > 0.05f)
				{
					Rectangle rect2 = Rectangle.Inflate(rect, 1, 1);
					using GraphicsPath path = CreateRoundedRect(rect2, 3);
					using Pen pen = new Pen(Color.FromArgb(120, red, green, blue), 2f);
					graphics.DrawPath(pen, path);
				}
				using (GraphicsPath path2 = CreateRoundedRect(rect, 3))
				{
					graphics.FillPath(brush, path2);
				}
				num8 += num12 + (float)num2;
			}
		}
	}

	private static GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
	{
		GraphicsPath graphicsPath = new GraphicsPath();
		int num = radius * 2;
		if (num > rect.Width)
		{
			num = rect.Width;
		}
		if (num > rect.Height)
		{
			num = rect.Height;
		}
		graphicsPath.AddArc(rect.X, rect.Y, num, num, 180f, 90f);
		graphicsPath.AddArc(rect.Right - num, rect.Y, num, num, 270f, 90f);
		graphicsPath.AddArc(rect.Right - num, rect.Bottom - num, num, num, 0f, 90f);
		graphicsPath.AddArc(rect.X, rect.Bottom - num, num, num, 90f, 90f);
		graphicsPath.CloseFigure();
		return graphicsPath;
	}
}
