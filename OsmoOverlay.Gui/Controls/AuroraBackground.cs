using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace OsmoOverlay.Gui;

/// <summary>
///     A slowly moving blue aurora on the dark ground, gathered around the "app is working" text: a few soft curtains that drift and breathe, and a
///     few twinkling stars. Each curtain is a handful of wide translucent shapes stacked from its bright lower edge upward, so it fades
///     smoothly with no seams and costs a few dozen fills a frame (no per-ray layers). Colors come from the palette, so a theme recolors it.
///     It only ticks while Running.
/// </summary>
public sealed class AuroraBackground : Control
{
	private const int Points = 72;
	private const int Layers = 14;
	private const int Stars = 46;
	private const int StarShades = 5;
	// Each curtain is strongest around the middle, where the text is, and thins out to nothing toward the sides: how far (Spread, a share of the
	// width) and how much it sways about the middle.
	private static readonly (double Base, double Height, double Speed, double Phase, double Strength, double Spread)[] Curtains =
	[
		(0.60, 0.55, 0.22, 0.0, 1.0, 0.20),
		(0.66, 0.40, 0.31, 2.1, 0.8, 0.27),
		(0.54, 0.46, 0.17, 4.3, 0.65, 0.16)
	];

	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
	private readonly Point[] _bottom = new Point[Points + 1];
	private readonly double[] _length = new double[Points + 1];
	private IBrush[]? _curtainBrushes;
	private IBrush? _edgeBrush;
	private IBrush[]? _starBrushes;
	private RadialGradientBrush? _haze;

	public AuroraBackground()
	{
		IsHitTestVisible = false;
		_timer.Tick += (_, _) => InvalidateVisual();
	}

	/// <summary>Whether it moves - the window turns it on while it shows it, so a hidden one costs no ticks.</summary>
	public bool Running
	{
		get => _timer.IsEnabled;
		set
		{
			if (value == _timer.IsEnabled) return;

			if (value)
			{
				BuildBrushes();
				_timer.Start();
			}
			else
			{
				_timer.Stop();
			}
		}
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		_timer.Stop();
	}

	private void BuildBrushes()
	{
		Color accent = ((ISolidColorBrush)Palette.Accent).Color;
		Color light = Color.FromRgb((byte)Math.Min(255, accent.R + 80), (byte)Math.Min(255, accent.G + 80), (byte)Math.Min(255, accent.B + 40));
		// Each stacked shape adds a little: the lowest part lies under all of them, the top of the curtain under one.
		_curtainBrushes = [.. Curtains.Select((c, i) => (IBrush)new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(255 * 0.05 * c.Strength), i == 1 ? light.R : accent.R, i == 1 ? light.G : accent.G, i == 1 ? light.B : accent.B)))];
		_edgeBrush = new ImmutableSolidColorBrush(Color.FromArgb(40, light.R, light.G, light.B));
		Color star = ((ISolidColorBrush)Palette.TextPrimary).Color;
		_starBrushes = [.. Enumerable.Range(0, StarShades).Select(i => (IBrush)new ImmutableSolidColorBrush(Color.FromArgb((byte)(25 + 150 * i / (StarShades - 1)), star.R, star.G, star.B)))];
		_haze = new RadialGradientBrush
		{
			Center = new RelativePoint(0.5, 0.55, RelativeUnit.Relative),
			GradientOrigin = new RelativePoint(0.5, 0.55, RelativeUnit.Relative),
			RadiusX = RelativeScalar.Parse("42%"),
			RadiusY = RelativeScalar.Parse("75%"),
			GradientStops = [new GradientStop(Color.FromArgb(95, accent.R, accent.G, accent.B), 0), new GradientStop(Color.FromArgb(34, accent.R, accent.G, accent.B), 0.55), new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)]
		};
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		if (_curtainBrushes is null || _edgeBrush is null || _starBrushes is null || _haze is null || Bounds.Width < 1 || Bounds.Height < 1) return;

		double w = Bounds.Width, h = Bounds.Height, t = _clock.Elapsed.TotalSeconds;
		context.DrawRectangle(Palette.SurfaceSunken, null, new Rect(0, 0, w, h));
		context.DrawRectangle(_haze, null, new Rect(0, 0, w, h));

		for (int i = 0; i < Stars; i++)
		{
			double twinkle = 0.5 + 0.5 * Math.Sin(t * (0.6 + Hash(i + 90)) + i);
			int shade = Math.Clamp((int)(twinkle * StarShades), 0, StarShades - 1);
			context.DrawEllipse(_starBrushes[shade], null, new Point(Hash(i * 2 + 1) * w, Hash(i * 2 + 2) * h * 0.7), 1.1, 1.1);
		}

		for (int c = 0; c < Curtains.Length; c++)
		{
			(double baseline, double height, double speed, double phase, _, double spread) = Curtains[c];
			double middle = 0.5 + 0.04 * Math.Sin(t * speed + phase);
			for (int i = 0; i <= Points; i++)
			{
				double u = i / (double)Points;
				double fade = Math.Exp(-Math.Pow((u - middle) / spread, 2));
				_bottom[i] = new Point(u * w, h * (baseline + 0.07 * Math.Sin(u * 5.2 + t * speed + phase) + 0.03 * Math.Sin(u * 13 - t * speed * 1.7)));
				_length[i] = fade * h * height * (0.55 + 0.45 * Math.Sin(u * 7.5 + t * speed * 1.3 + phase * 1.7));
			}

			for (int layer = 0; layer < Layers; layer++)
			{
				double reach = (layer + 1) / (double)Layers;
				// A new geometry each time: the context keeps what it is given until the frame is drawn, so one reused would change them all.
				var geometry = new StreamGeometry();
				using (StreamGeometryContext shape = geometry.Open())
				{
					shape.BeginFigure(new Point(_bottom[0].X, _bottom[0].Y - _length[0] * reach), true);
					for (int i = 1; i <= Points; i++) shape.LineTo(new Point(_bottom[i].X, _bottom[i].Y - _length[i] * reach));
					for (int i = Points; i >= 0; i--) shape.LineTo(_bottom[i]);
					shape.EndFigure(true);
				}

				context.DrawGeometry(_curtainBrushes[c], null, geometry);
			}

			DrawEdge(context);
		}
	}

	/// <summary>The bright lower edge of a curtain: a thin soft band along its bottom.</summary>
	private void DrawEdge(DrawingContext context)
	{
		var geometry = new StreamGeometry();
		using (StreamGeometryContext shape = geometry.Open())
		{
			shape.BeginFigure(new Point(_bottom[0].X, _bottom[0].Y - _length[0] * 0.12), true);
			for (int i = 1; i <= Points; i++) shape.LineTo(new Point(_bottom[i].X, _bottom[i].Y - _length[i] * 0.12));
			for (int i = Points; i >= 0; i--) shape.LineTo(new Point(_bottom[i].X, _bottom[i].Y + 6 * Math.Min(1, _length[i] / 20)));
			shape.EndFigure(true);
		}

		context.DrawGeometry(_edgeBrush, null, geometry);
	}

	/// <summary>A fixed pseudo-random number in [0, 1) for an index - the same stars on every frame.</summary>
	private static double Hash(int n)
	{
		double s = Math.Sin(n * 12.9898) * 43758.5453;
		return s - Math.Floor(s);
	}
}
