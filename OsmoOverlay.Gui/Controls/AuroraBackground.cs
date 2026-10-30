using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace OsmoOverlay.Gui;

public sealed class AuroraBackground : Control
{
	private readonly Stopwatch _clock = new();
	private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
	private IBrush? _ambient;
	private IBrush? _glow;
	private IBrush? _highlight;
	private Bitmap? _cloud;
	private Bitmap? _mist;

	public AuroraBackground()
	{
		IsHitTestVisible = false;
		_timer.Tick += (_, _) => InvalidateVisual();
	}

	public bool Running
	{
		get => _timer.IsEnabled;
		set
		{
			if (value == _timer.IsEnabled) return;

			if (value)
			{
				Color accent = ((ISolidColorBrush)Palette.Accent).Color;
				Color light = ((ISolidColorBrush)Palette.Blend(0.25, Palette.Accent, Palette.TextPrimary)).Color;
				_ambient = CreateGlow(accent, 35);
				_glow = CreateGlow(accent, 95);
				_highlight = CreateGlow(light, 160);
				_cloud ??= CreateCloud(accent, 7);
				_mist ??= CreateCloud(light, 29);
				_clock.Start();
				_timer.Start();
				InvalidateVisual();
			}
			else
			{
				_timer.Stop();
				_clock.Stop();
			}
		}
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		Running = false;
	}

	private static RadialGradientBrush CreateGlow(Color color, byte opacity) => new()
	{
		Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
		GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
		RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
		RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
		GradientStops =
		[
			new GradientStop(Color.FromArgb(opacity, color.R, color.G, color.B), 0),
			new GradientStop(Color.FromArgb((byte)(opacity * 0.78), color.R, color.G, color.B), 0.25),
			new GradientStop(Color.FromArgb((byte)(opacity * 0.32), color.R, color.G, color.B), 0.55),
			new GradientStop(Color.FromArgb((byte)(opacity * 0.06), color.R, color.G, color.B), 0.8),
			new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)
		]
	};

	private static Bitmap CreateCloud(Color color, int seed)
	{
		const int size = 256;
		using var image = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
		for (int y = 0; y < size; y++)
		for (int x = 0; x < size; x++)
		{
			double u = (x + 0.5) / size * 2 - 1, v = (y + 0.5) / size * 2 - 1;
			double warp = Noise(u * 2 + 9, v * 2 + 9, seed) * 2;
			double density = 0;
			for (int octave = 0; octave < 4; octave++)
			{
				double frequency = 3 * (1 << octave);
				density += Noise(u * frequency + warp + 20, v * frequency + warp + 20, seed + octave) / (1 << octave);
			}
			double envelope = Math.Pow(Math.Max(0, 1 - u * u - v * v), 2);
			double alpha = Math.Clamp((density - 0.55) * 1.2, 0, 1) * envelope;
			image.SetPixel(x, y, new SKColor(color.R, color.G, color.B, (byte)(alpha * 180)));
		}
		using SKImage snapshot = SKImage.FromBitmap(image);
		using SKData data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
		using Stream stream = data.AsStream();
		return new Bitmap(stream);
	}

	private static double Noise(double x, double y, int seed)
	{
		int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
		double fx = x - ix, fy = y - iy;
		fx = fx * fx * (3 - 2 * fx);
		fy = fy * fy * (3 - 2 * fy);
		double a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed);
		double c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
		return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
	}

	private static double Hash(int x, int y, int seed)
	{
		uint hash = unchecked((uint)(x * 374761393 + y * 668265263 + seed * 1442695041));
		hash = unchecked((hash ^ (hash >> 13)) * 1274126177);
		return (hash ^ (hash >> 16)) / (double)uint.MaxValue;
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		double width = Bounds.Width, height = Bounds.Height;
		if (width < 1 || height < 1) return;

		context.DrawRectangle(Palette.SurfaceSunken, null, new Rect(0, 0, width, height));
		if (_ambient is null || _glow is null || _highlight is null || _cloud is null || _mist is null) return;

		double time = _clock.Elapsed.TotalSeconds;
		double radius = Math.Min(width * 0.26, height * 0.40);
		var center = new Point(width / 2, height / 2);
		DrawGlow(context, _ambient, center, width * 0.6, height * 0.75, 0);
		DrawGlow(context, _ambient, center, radius * 1.6, radius * 1.15, 0);
		for (int i = 0; i < 6; i++)
		{
			double phase = i * Math.Tau / 6;
			double flow = time * 0.28 + phase;
			double breath = 1 + 0.18 * Math.Sin(time * 0.55 + phase);
			var position = new Point(center.X + radius * (0.34 * Math.Cos(phase) + 0.18 * Math.Sin(flow)),
				center.Y + radius * (0.28 * Math.Sin(phase) + 0.14 * Math.Cos(flow * 1.3)));
			using (context.PushTransform(Matrix.CreateRotation(phase + 0.15 * Math.Sin(flow)) * Matrix.CreateTranslation(position.X, position.Y)))
			using (context.PushOpacity(0.55 + 0.2 * Math.Sin(flow + phase)))
				context.DrawImage(i % 2 == 0 ? _cloud : _mist, new Rect(-radius * breath, -radius * 0.78 / breath, radius * 2 * breath, radius * 1.56 / breath));
		}
		for (int i = 0; i < 40; i++)
		{
			double phase = Hash(i, 0, 3) * Math.Tau;
			double spread = 0.35 + Hash(i, 1, 3) * 1.7;
			double drift = time * (0.015 + Hash(i, 2, 3) * 0.012);
			var position = new Point(center.X + Math.Cos(phase + drift) * radius * spread * 1.5,
				center.Y + Math.Sin(phase + drift) * radius * spread * 0.85);
			double brightness = Math.Pow(0.5 + 0.5 * Math.Sin(time * 0.8 + phase * 3), 3);
			using (context.PushOpacity(0.12 + brightness * 0.6))
			{
				DrawGlow(context, _glow, position, 4 + brightness * 5, 4 + brightness * 5, 0);
				context.DrawEllipse(_highlight, null, position, 0.6 + brightness * 0.5, 0.6 + brightness * 0.5);
			}
		}
	}

	private static void DrawGlow(DrawingContext context, IBrush brush, Point center, double radiusX, double radiusY, double angle)
	{
		using (context.PushTransform(Matrix.CreateRotation(angle) * Matrix.CreateTranslation(center.X, center.Y)))
			context.DrawRectangle(brush, null, new Rect(-radiusX, -radiusY, radiusX * 2, radiusY * 2));
	}
}
