using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GHelper.UI;

public class SegmentedTab : Control
{
	private string[] _items = Array.Empty<string>();

	private int _selectedIndex;

	private int _hoverIndex = -1;

	public string[] Items
	{
		get
		{
			return _items;
		}
		set
		{
			_items = value ?? Array.Empty<string>();
			_selectedIndex = 0;
			Invalidate();
		}
	}

	public int SelectedIndex
	{
		get
		{
			return _selectedIndex;
		}
		set
		{
			if (_selectedIndex != value && value >= 0 && value < _items.Length)
			{
				_selectedIndex = value;
				this.SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
				Invalidate();
			}
		}
	}

	public event EventHandler? SelectedIndexChanged;

	public SegmentedTab()
	{
		DoubleBuffered = true;
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
		Cursor = Cursors.Hand;
		base.Height = 32;
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		if (_items.Length == 0)
		{
			return;
		}
		Graphics graphics = e.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		Color color = Color.FromArgb(24, 24, 24);
		Color color2 = Color.FromArgb(50, 50, 50);
		Color color3 = Color.FromArgb(55, 55, 55);
		Color color4 = Color.FromArgb(40, 40, 40);
		Color color5 = Color.FromArgb(160, 160, 160);
		Color color6 = Color.FromArgb(240, 240, 240);
		Rectangle rect = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
		using (GraphicsPath path = CreateRoundedRect(rect, 6))
		{
			using SolidBrush brush = new SolidBrush(color);
			graphics.FillPath(brush, path);
			using Pen pen = new Pen(color2);
			graphics.DrawPath(pen, path);
		}
		int num = (base.Width - 4) / _items.Length;
		using Font font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
		for (int i = 0; i < _items.Length; i++)
		{
			Rectangle rect2 = new Rectangle(2 + i * num, 2, num, base.Height - 4);
			if (i == _selectedIndex)
			{
				using GraphicsPath path2 = CreateRoundedRect(rect2, 4);
				using SolidBrush brush2 = new SolidBrush(color3);
				graphics.FillPath(brush2, path2);
			}
			else if (i == _hoverIndex)
			{
				using GraphicsPath path3 = CreateRoundedRect(rect2, 4);
				using SolidBrush brush3 = new SolidBrush(color4);
				graphics.FillPath(brush3, path3);
			}
			Color color7 = ((i == _selectedIndex) ? color6 : color5);
			using SolidBrush brush4 = new SolidBrush(color7);
			SizeF sizeF = graphics.MeasureString(_items[i], font);
			float x = (float)rect2.X + ((float)rect2.Width - sizeF.Width) / 2f;
			float y = (float)rect2.Y + ((float)rect2.Height - sizeF.Height) / 2f;
			graphics.DrawString(_items[i], font, brush4, x, y);
		}
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		int indexAt = GetIndexAt(e.X);
		if (indexAt != _hoverIndex)
		{
			_hoverIndex = indexAt;
			Invalidate();
		}
	}

	protected override void OnMouseLeave(EventArgs e)
	{
		base.OnMouseLeave(e);
		_hoverIndex = -1;
		Invalidate();
	}

	protected override void OnMouseClick(MouseEventArgs e)
	{
		base.OnMouseClick(e);
		int indexAt = GetIndexAt(e.X);
		if (indexAt >= 0 && indexAt < _items.Length)
		{
			SelectedIndex = indexAt;
		}
	}

	private int GetIndexAt(int x)
	{
		if (_items.Length == 0)
		{
			return -1;
		}
		int num = (base.Width - 4) / _items.Length;
		int value = (x - 2) / num;
		return Math.Clamp(value, 0, _items.Length - 1);
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
