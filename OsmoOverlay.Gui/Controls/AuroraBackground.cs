using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;

namespace OsmoOverlay.Gui;

public sealed class AuroraBackground : Control
{
	private readonly Stopwatch _clock = new();
	private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
	private IBrush? _ambient;
	private IBrush? _highlight;
	private Color _glowColor;
	private readonly IBrush?[] _particleBrushes = new IBrush?[256];
	private Bitmap? _cloud;
	private Bitmap? _mist;
	private int _seed;
	private double _cloudsShownAt;
	private Task<Nebula>? _next;
	private const int CloudCount = 5;
	private const int ParticleCount = 28;
	private const double CloudFadeSeconds = 1.2;

	private sealed record Nebula(int Seed, Color Accent, Color Light, Bitmap Cloud, Bitmap Mist);

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
				_highlight = CreateGlow(light, 160);
				_glowColor = light;
				Array.Clear(_particleBrushes);
				StartNewNebula(accent, light);
				_clock.Start();
				_timer.Start();
				InvalidateVisual();
			}
			else
			{
				_timer.Stop();
				_clock.Stop();
				_seed = 0;
				ReleaseClouds();
			}
		}
	}

	private void ReleaseClouds()
	{
		_cloud?.Dispose();
		_mist?.Dispose();
		(_cloud, _mist) = (null, null);
	}

	/// <summary>
	///     A different nebula every time the animation starts: the seed shapes both cloud textures and where everything drifts. The
	///     next one is built ahead (PrepareNext), so the clouds are there from the first frame; when it isn't ready (or the colors
	///     changed with the theme) they are built off the UI thread and fade in once done. One finished after the animation stopped
	///     (or restarted) is dropped.
	/// </summary>
	private void StartNewNebula(Color accent, Color light)
	{
		ReleaseClouds();
		if (_next is { IsCompletedSuccessfully: true } ready && ready.Result is { } nebula && nebula.Accent == accent && nebula.Light == light)
		{
			_next = null;
			(_seed, _cloud, _mist) = (nebula.Seed, nebula.Cloud, nebula.Mist);
			_cloudsShownAt = double.NegativeInfinity;
		}
		else
		{
			int seed = _seed = Random.Shared.Next(1, int.MaxValue);
			_ = Task.Run(() => (CreateCloud(accent, seed), CreateCloud(light, seed + 1))).ContinueWith(done =>
			{
				Dispatcher.UIThread.Post(() =>
				{
					if (_seed != seed || done.IsFaulted) return;

					(_cloud, _mist) = done.Result;
					_cloudsShownAt = _clock.Elapsed.TotalSeconds;
					InvalidateVisual();
				});
			}, TaskScheduler.Default);
		}

		PrepareNext(accent, light);
	}

	/// <summary>Builds the nebula for the next start in the background, so starting needs no wait.</summary>
	private void PrepareNext(Color accent, Color light)
	{
		DiscardNext();
		int seed = Random.Shared.Next(1, int.MaxValue);
		_next = Task.Run(() => new Nebula(seed, accent, light, CreateCloud(accent, seed), CreateCloud(light, seed + 1)));
	}

	private void DiscardNext()
	{
		Task<Nebula>? next = _next;
		_next = null;
		next?.ContinueWith(done =>
		{
			if (!done.IsCompletedSuccessfully) return;

			done.Result.Cloud.Dispose();
			done.Result.Mist.Dispose();
		}, TaskScheduler.Default);
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		if (_next is not null) return;

		PrepareNext(((ISolidColorBrush)Palette.Accent).Color, ((ISolidColorBrush)Palette.Blend(0.25, Palette.Accent, Palette.TextPrimary)).Color);
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		Running = false;
		DiscardNext();
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
		uint[] pixels = new uint[size * size];
		Parallel.For(0, size, y =>
		{
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
				uint alpha = (uint)(Math.Clamp((density - 0.55) * 1.2, 0, 1) * envelope * 100);
				pixels[y * size + x] = alpha << 24 | Premultiply(color.R, alpha) << 16 | Premultiply(color.G, alpha) << 8 | Premultiply(color.B, alpha);
			}
		});

		GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
		try
		{
			return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), new PixelSize(size, size), new Vector(96, 96), size * 4);
		}
		finally
		{
			handle.Free();
		}
	}

	private static uint Premultiply(byte channel, uint alpha) => (channel * alpha + 127) / 255;

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
		if (_ambient is null || _highlight is null) return;

		double time = _clock.Elapsed.TotalSeconds;
		double radius = Math.Min(width * 0.26, height * 0.40);
		var center = new Point(width / 2, height / 2);
		DrawGlow(context, _ambient, center, width * 0.6, height * 0.75);
		DrawGlow(context, _ambient, center, radius * 1.6, radius * 1.15);
		if (_cloud is { } cloud && _mist is { } mist)
		{
			using var fade = context.PushOpacity(Math.Clamp((time - _cloudsShownAt) / CloudFadeSeconds, 0, 1));
			double turn = Hash(0, 5, _seed) * Math.Tau;
			for (int i = 0; i < CloudCount; i++)
			{
				double phase = turn + i * Math.Tau / CloudCount + (Hash(i, 6, _seed) - 0.5) * 0.8;
				double flow = time * (0.2 + Hash(i, 7, _seed) * 0.16) + phase;
				double breath = 1 + 0.18 * Math.Sin(time * 0.55 + phase);
				var position = new Point(center.X + radius * (0.34 * Math.Cos(phase) + 0.18 * Math.Sin(flow)),
					center.Y + radius * (0.28 * Math.Sin(phase) + 0.14 * Math.Cos(flow * 1.3)));
				using (context.PushTransform(Matrix.CreateRotation(phase + 0.15 * Math.Sin(flow)) * Matrix.CreateTranslation(position.X, position.Y)))
					context.DrawImage(i % 2 == 0 ? cloud : mist, new Rect(-radius * breath, -radius * 0.78 / breath, radius * 2 * breath, radius * 1.56 / breath));
			}
		}

		for (int i = 0; i < ParticleCount; i++)
		{
			// Each star drifts on its own heading and speed across the whole area (positions are shares of it, so a resize keeps them),
			// wrapping round the edges; it fades near them so the wrap isn't seen.
			double heading = Hash(i, 2, _seed) * Math.Tau;
			double speed = 0.003 + Hash(i, 3, _seed) * 0.012;
			double x = Wrap(Hash(i, 0, _seed) + Math.Cos(heading) * speed * time);
			double y = Wrap(Hash(i, 1, _seed) + Math.Sin(heading) * speed * time * width / height);
			double edge = Math.Clamp(Math.Min(Math.Min(x, 1 - x), Math.Min(y, 1 - y)) / 0.06, 0, 1);
			double brightness = Math.Pow(0.5 + 0.5 * Math.Sin(time * (0.5 + Hash(i, 4, _seed)) + Hash(i, 5, _seed) * Math.Tau), 3);
			double size = 0.4 + brightness * 0.3 + speed * 20;
			var position = new Point(x * width, y * height);
			context.DrawEllipse(Particle((byte)((30 + brightness * 60) * edge)), null, position, size * 3, size * 3);
			context.DrawEllipse(Particle((byte)((40 + brightness * 150) * edge)), null, position, size, size);
		}
	}

	private static double Wrap(double value) => value - Math.Floor(value);

	private IBrush Particle(byte alpha) => _particleBrushes[alpha] ??= new ImmutableSolidColorBrush(Color.FromArgb(alpha, _glowColor.R, _glowColor.G, _glowColor.B));

	private static void DrawGlow(DrawingContext context, IBrush brush, Point center, double radiusX, double radiusY)
	{
		context.DrawRectangle(brush, null, new Rect(center.X - radiusX, center.Y - radiusY, radiusX * 2, radiusY * 2));
	}
}
