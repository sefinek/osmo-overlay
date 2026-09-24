using System.Collections.Concurrent;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The trail's own color while slow (held for the bottom SlowHold of the scale), then amber -> orange -> red,
///     for a route colored by speed (TrailOverlayElement.TrailColorBySpeed). Precomputed per trail color into
///     Buckets colors interpolated in Oklab, so the steps stay even in brightness instead of going muddy the way
///     a plain sRGB blend of green and red does.
/// </summary>
internal static class SpeedColorScale
{
	public const int Buckets = 32;
	private const double SlowHold = 0.2;

	private static readonly (double At, SKColor Color)[] WarmStops =
	[
		(0.50, new SKColor(0xFF, 0xC5, 0x3D)),
		(0.75, new SKColor(0xFF, 0x7A, 0x2F)),
		(1.00, new SKColor(0xF0, 0x28, 0x3C))
	];

	// Keyed by trail color - a handful at most (one per distinct TrailColor in the layout), shared by the render and the preview.
	private static readonly ConcurrentDictionary<SKColor, SKColor[]> ColorsByBase = new();

	public static SKColor[] Colors(SKColor slowColor)
	{
		return ColorsByBase.GetOrAdd(slowColor, BuildColors);
	}

	/// <summary>Bucket index for a speed as a fraction of the scale's top (clamped to 0..1).</summary>
	public static int Bucket(double fraction)
	{
		if (double.IsNaN(fraction)) return 0;
		return (int)Math.Round(Math.Clamp(fraction, 0, 1) * (Buckets - 1));
	}

	private static SKColor[] BuildColors(SKColor slowColor)
	{
		(double At, SKColor Color)[] stops = [(0, slowColor), (SlowHold, slowColor), .. WarmStops];
		var colors = new SKColor[Buckets];
		for (var b = 0; b < Buckets; b++)
		{
			var t = b / (double)(Buckets - 1);
			var i = 1;
			while (i < stops.Length - 1 && t > stops[i].At) i++;
			var (fromAt, from) = stops[i - 1];
			var (toAt, to) = stops[i];
			colors[b] = Mix(from, to, (t - fromAt) / (toAt - fromAt));
		}

		return colors;
	}

	private static SKColor Mix(SKColor a, SKColor b, double t)
	{
		if (a == b) return a;
		var (l1, a1, b1) = ToOklab(a);
		var (l2, a2, b2) = ToOklab(b);
		return FromOklab(l1 + (l2 - l1) * t, a1 + (a2 - a1) * t, b1 + (b2 - b1) * t)
			.WithAlpha((byte)Math.Round(a.Alpha + (b.Alpha - a.Alpha) * t));
	}

	private static (double L, double A, double B) ToOklab(SKColor c)
	{
		double r = ToLinear(c.Red), g = ToLinear(c.Green), b = ToLinear(c.Blue);
		var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
		var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
		var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
		return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
			1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
			0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
	}

	private static SKColor FromOklab(double lightness, double a, double b)
	{
		var l = Math.Pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
		var m = Math.Pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
		var s = Math.Pow(lightness - 0.0894841775 * a - 1.2914855480 * b, 3);
		return new SKColor(
			ToSrgb(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
			ToSrgb(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
			ToSrgb(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));
	}

	private static double ToLinear(byte channel)
	{
		var c = channel / 255.0;
		return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
	}

	private static byte ToSrgb(double linear)
	{
		linear = Math.Clamp(linear, 0, 1);
		var c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
		return (byte)Math.Round(c * 255);
	}
}
