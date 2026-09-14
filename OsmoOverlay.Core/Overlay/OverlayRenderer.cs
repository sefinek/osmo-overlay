using System.Globalization;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

public sealed class OverlayRenderer : IDisposable
{
	private const double TrailMinStepMeters = 3.0;

	private static readonly SKColor White = SKColors.White;
	private static readonly SKColor Accent = new(70, 190, 255);
	private static readonly SKColor TrailColor = new(70, 220, 110);
	private static readonly SKColor SunColor = new(255, 175, 45);
	private static readonly SKColor Shadow = new(0, 0, 0, 225);
	private static readonly SKColor PanelFill = new(0, 0, 0, 80);
	private readonly SKFont _dateFont;
	private readonly int _height;

	private readonly SKTypeface _hudTypeface;
	private readonly SKFont _labelFont;
	private readonly double _maxGaugeSpeedKmh;
	private readonly SKFont _smallFont;
	private readonly SKFont _speedFont;
	private readonly SKFont _speedUnitFont;
	private readonly double _startAltitude;

	private readonly List<(double East, double North)> _trail = [];
	private readonly SKFont _unitFont;
	private readonly SKFont _valueFont;

	private readonly int _width;
	private (double East, double North)? _lastTrailPoint;

	public OverlayRenderer(int width, int height, double startAltitude, IReadOnlyList<OverlayElement> layout,
		double observedMaxSpeedKmh = 0)
	{
		_width = width;
		_height = height;
		_startAltitude = startAltitude;
		Layout = layout;
		_maxGaugeSpeedKmh = ComputeGaugeMaxSpeed(observedMaxSpeedKmh);

		_hudTypeface = CreateHudTypeface();

		_dateFont = new SKFont(_hudTypeface, 46);
		_labelFont = new SKFont(_hudTypeface, 30);
		_valueFont = new SKFont(_hudTypeface, 100);
		_unitFont = new SKFont(_hudTypeface, 46);
		_smallFont = new SKFont(_hudTypeface, 36);
		_speedFont = new SKFont(_hudTypeface, 140);
		_speedUnitFont = new SKFont(_hudTypeface, 38);
	}

	/// <summary>Mutable so the GUI editor can reposition/toggle elements without rebuilding fonts.</summary>
	public IReadOnlyList<OverlayElement> Layout { get; set; }

	public void Dispose()
	{
		_hudTypeface.Dispose();
		_dateFont.Dispose();
		_labelFont.Dispose();
		_valueFont.Dispose();
		_unitFont.Dispose();
		_smallFont.Dispose();
		_speedFont.Dispose();
		_speedUnitFont.Dispose();
	}

	/// <summary>
	///     Rounds the recording's actual max speed up to the next 10 km/h so the gauge scale matches
	///     this ride instead of a fixed 60 km/h that's meaningless for a walk or absurdly low for a car.
	///     A 20 km/h floor keeps the needle from pinning near full-scale on a near-stationary clip.
	/// </summary>
	private static double ComputeGaugeMaxSpeed(double observedMaxSpeedKmh)
	{
		var rounded = Math.Ceiling(Math.Max(observedMaxSpeedKmh, 1) / 10.0) * 10.0;
		return Math.Max(rounded, 20.0);
	}

	private static SKTypeface CreateHudTypeface()
	{
		var style = new SKFontStyle(SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
		string[] candidates = OperatingSystem.IsWindows()
			? ["Segoe UI"]
			: OperatingSystem.IsMacOS()
				? ["Helvetica Neue", "Arial"]
				: ["Noto Sans", "DejaVu Sans", "Liberation Sans", "Arial"];

		foreach (var family in candidates)
		{
			SKTypeface? typeface = SKFontManager.Default.MatchFamily(family, style);
			if (typeface is not null && typeface.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
				return typeface;
			typeface?.Dispose();
		}

		return SKTypeface.FromFamilyName(null, style);
	}

	public byte[] Render(DerivedFrame frame, int? outputWidth = null, int? outputHeight = null)
	{
		UpdateTrail(frame);

		var outW = outputWidth ?? _width;
		var outH = outputHeight ?? _height;

		using var bitmap = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Unpremul));
		using var canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.Transparent);
		canvas.Scale(outW / (float)_width, outH / (float)_height);

		foreach (OverlayElement element in Layout)
		{
			if (!element.Visible) continue;

			switch (element.Type)
			{
				case OverlayElementType.StatsBlock:
					DrawStatsBlock(canvas, frame, element.X, element.Y);
					break;
				case OverlayElementType.Compass:
					DrawCompass(canvas, frame, element.X, element.Y);
					break;
				case OverlayElementType.SunWidget:
					DrawSunWidget(canvas, frame, element.X, element.Y);
					break;
				case OverlayElementType.PitchGauge:
					DrawPitchGauge(canvas, element.X, element.Y, frame.PitchDegrees);
					break;
				case OverlayElementType.SpeedGauge:
					DrawSpeedGauge(canvas, element.X, element.Y, frame.SpeedKmh);
					break;
			}
		}

		return bitmap.Bytes;
	}

	private void UpdateTrail(DerivedFrame frame)
	{
		(double LocalEastMeters, double LocalNorthMeters) point = (frame.LocalEastMeters, frame.LocalNorthMeters);
		if (_lastTrailPoint is not { } last || Distance(last, point) >= TrailMinStepMeters)
		{
			_trail.Add(point);
			_lastTrailPoint = point;
		}
	}

	private static double Distance((double East, double North) a, (double East, double North) b)
	{
		double dx = a.East - b.East, dy = a.North - b.North;
		return Math.Sqrt(dx * dx + dy * dy);
	}

	private void DrawStatsBlock(SKCanvas canvas, DerivedFrame frame, float x, float y)
	{
		var dateText = frame.Raw.GpsTimestamp is { } utc
			? utc.ToLocalFromUtc().ToString("yyyy/MM/dd  HH:mm:ss", CultureInfo.InvariantCulture)
			: "--";
		DrawOutlined(canvas, dateText, x, y, _dateFont, White);

		y += 110;
		DrawStat(canvas, x, ref y, "ELEVATION", F(frame.Raw.AltitudeMeters - _startAltitude, "0"), "M");
		DrawStat(canvas, x, ref y, "GRADIENT", F(frame.GradientPercent, "0"), "%");
		var (distanceValue, distanceUnit) = FormatDistance(frame.CumulativeDistanceMeters);
		DrawStat(canvas, x, ref y, "TOTAL DISTANCE", distanceValue, distanceUnit);
	}

	private static (string Value, string Unit) FormatDistance(double meters)
	{
		return meters >= 1000
			? (F(meters / 1000.0, "0.00"), "KM")
			: (F(meters, "0.00"), "M");
	}

	private void DrawStat(SKCanvas canvas, float x, ref float y, string label, string value, string unit)
	{
		DrawOutlined(canvas, label, x, y, _labelFont, White);
		y += 90;
		DrawOutlined(canvas, value, x, y, _valueFont, Accent);
		var valueWidth = _valueFont.MeasureText(value);
		DrawOutlined(canvas, unit, x + valueWidth + 12, y, _unitFont, White);
		y += 150;
	}

	private void DrawCompass(SKCanvas canvas, DerivedFrame frame, float cx, float cy)
	{
		using var fill = new SKPaint { Color = PanelFill, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var ring = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 160), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3
		};
		canvas.DrawCircle(cx, cy, OverlayElementBounds.CompassRadius, fill);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.CompassRadius, ring);

		DrawTrail(canvas, cx, cy, frame);
		DrawHeadingArrow(canvas, cx, cy, frame.HeadingDegrees);

		DrawOutlined(canvas, "N", cx, cy - OverlayElementBounds.CompassRadius + 46, _labelFont, White,
			SKTextAlign.Center);

		var headingText =
			$"{F(AngleMath.NormalizeDegrees(frame.HeadingDegrees), "0")}°{CardinalDirection(frame.HeadingDegrees)}";
		DrawOutlined(canvas, headingText, cx, cy + OverlayElementBounds.CompassRadius + 46, _labelFont, White,
			SKTextAlign.Center);
	}

	private void DrawTrail(SKCanvas canvas, float cx, float cy, DerivedFrame frame)
	{
		if (_trail.Count < 2) return;

		(double East, double North) currentPos = (frame.LocalEastMeters, frame.LocalNorthMeters);
		var maxDist = 5.0;
		foreach ((double East, double North) p in _trail)
		{
			var dist = Distance(p, currentPos);
			if (dist > maxDist) maxDist = dist;
		}

		var scale = OverlayElementBounds.CompassRadius * 0.82 / maxDist;

		var builder = new SKPathBuilder();
		var started = false;
		foreach ((double East, double North) p in _trail)
		{
			var px = cx + (float)((p.East - frame.LocalEastMeters) * scale);
			var py = cy - (float)((p.North - frame.LocalNorthMeters) * scale);
			if (!started)
			{
				builder.MoveTo(px, py);
				started = true;
			}
			else
			{
				builder.LineTo(px, py);
			}
		}

		using SKPath path = builder.Detach();
		using var trailPaint = new SKPaint
		{
			Color = TrailColor, IsAntialias = true, Style = SKPaintStyle.Stroke,
			StrokeWidth = 6, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
		};
		canvas.DrawPath(path, trailPaint);
	}

	private void DrawHeadingArrow(SKCanvas canvas, float cx, float cy, double headingDegrees)
	{
		var rad = AngleMath.DegToRad(headingDegrees);
		float len = 34;
		var tipX = cx + (float)(Math.Sin(rad) * len);
		var tipY = cy - (float)(Math.Cos(rad) * len);
		var leftX = cx + (float)(Math.Sin(rad + 2.5) * len * 0.55);
		var leftY = cy - (float)(Math.Cos(rad + 2.5) * len * 0.55);
		var rightX = cx + (float)(Math.Sin(rad - 2.5) * len * 0.55);
		var rightY = cy - (float)(Math.Cos(rad - 2.5) * len * 0.55);

		var builder = new SKPathBuilder();
		builder.MoveTo(tipX, tipY);
		builder.LineTo(leftX, leftY);
		builder.LineTo(cx, cy);
		builder.LineTo(rightX, rightY);
		builder.Close();

		using SKPath path = builder.Detach();
		using var fill = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var outline = new SKPaint
			{ Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
		canvas.DrawPath(path, fill);
		canvas.DrawPath(path, outline);
	}

	private void DrawSunWidget(SKCanvas canvas, DerivedFrame frame, float cx, float cy)
	{
		using var fill = new SKPaint { Color = PanelFill, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var ring = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 140), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3
		};
		canvas.DrawCircle(cx, cy, OverlayElementBounds.SunRadius, fill);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.SunRadius, ring);

		var relativeAzimuthRad = AngleMath.DegToRad(frame.Sun.AzimuthDegrees - frame.HeadingDegrees);
		var elevationClamped = Math.Clamp(frame.Sun.ElevationDegrees, -20, 90);
		var radiusFactor = 1.0 - (elevationClamped + 20) / 110.0;

		var dotX = cx + (float)(Math.Sin(relativeAzimuthRad) * OverlayElementBounds.SunRadius * 0.8 * radiusFactor);
		var dotY = cy - (float)(Math.Cos(relativeAzimuthRad) * OverlayElementBounds.SunRadius * 0.8 * radiusFactor);

		using var sunPaint = new SKPaint { Color = SunColor, IsAntialias = true, Style = SKPaintStyle.Fill };
		using var sunOutline = new SKPaint
			{ Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
		float sunDotRadius = frame.Sun.ElevationDegrees > 0 ? 16 : 10;
		canvas.DrawCircle(dotX, dotY, sunDotRadius, sunPaint);
		canvas.DrawCircle(dotX, dotY, sunDotRadius, sunOutline);

		var gText = $"{F(frame.SmoothedGForce, "0.0")}G";
		DrawOutlined(canvas, gText, cx, cy + OverlayElementBounds.SunRadius + 56, _labelFont, White,
			SKTextAlign.Center);
	}

	private void DrawPitchGauge(SKCanvas canvas, float cx, float cy, double pitchDegrees)
	{
		using var arcPaint = new SKPaint
		{
			Color = new SKColor(255, 255, 255, 150), IsAntialias = true,
			Style = SKPaintStyle.Stroke, StrokeWidth = 5, StrokeCap = SKStrokeCap.Round
		};
		var rect = new SKRect(cx - OverlayElementBounds.PitchRadius, cy - OverlayElementBounds.PitchRadius,
			cx + OverlayElementBounds.PitchRadius, cy + OverlayElementBounds.PitchRadius);
		canvas.DrawArc(rect, 200, 140, false, arcPaint);

		var clamped = Math.Clamp(pitchDegrees, -45, 45);
		var angleDeg = 270 + clamped;
		var angleRad = AngleMath.DegToRad(angleDeg);
		var dotX = cx + (float)(Math.Cos(angleRad) * OverlayElementBounds.PitchRadius);
		var dotY = cy + (float)(Math.Sin(angleRad) * OverlayElementBounds.PitchRadius);

		using var dotPaint = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Fill };
		canvas.DrawCircle(dotX, dotY, 12, dotPaint);

		DrawOutlined(canvas, $"{F(pitchDegrees, "0")}°", cx, cy + 16, _labelFont, White, SKTextAlign.Center);
	}

	private void DrawSpeedGauge(SKCanvas canvas, float cx, float cy, double speedKmh)
	{
		var radius = OverlayElementBounds.SpeedRadius;
		var rect = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
		const float startAngle = 135f;
		const float sweep = 270f;

		DrawGaugeBand(canvas, rect, startAngle, sweep * 0.45f, new SKColor(70, 200, 90));
		DrawGaugeBand(canvas, rect, startAngle + sweep * 0.45f, sweep * 0.25f, new SKColor(230, 200, 60));
		DrawGaugeBand(canvas, rect, startAngle + sweep * 0.70f, sweep * 0.18f, new SKColor(235, 140, 50));
		DrawGaugeBand(canvas, rect, startAngle + sweep * 0.88f, sweep * 0.12f, new SKColor(220, 60, 60));

		var clamped = Math.Clamp(speedKmh, 0, _maxGaugeSpeedKmh);
		var needleAngleDeg = startAngle + sweep * (clamped / _maxGaugeSpeedKmh);
		var needleRad = AngleMath.DegToRad(needleAngleDeg);
		var needleX = cx + (float)(Math.Cos(needleRad) * (radius - 34));
		var needleY = cy + (float)(Math.Sin(needleRad) * (radius - 34));

		using var needlePaint = new SKPaint
		{
			Color = White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6,
			StrokeCap = SKStrokeCap.Round
		};
		canvas.DrawLine(cx, cy, needleX, needleY, needlePaint);

		var speedText = F(speedKmh, "0");
		var textWidth = _speedFont.MeasureText(speedText);
		DrawOutlined(canvas, speedText, cx - textWidth / 2, cy + 20, _speedFont, White);
		DrawOutlined(canvas, "KM/H", cx, cy + radius - 30, _speedUnitFont, White, SKTextAlign.Center);
	}

	private static void DrawGaugeBand(SKCanvas canvas, SKRect rect, float startAngle, float sweep, SKColor color)
	{
		using var paint = new SKPaint
		{
			Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke,
			StrokeWidth = 20, StrokeCap = SKStrokeCap.Butt
		};
		canvas.DrawArc(rect, startAngle, sweep, false, paint);
	}

	private void DrawOutlined(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color,
		SKTextAlign align = SKTextAlign.Left)
	{
		var dropOffset = font.Size * 0.045f;
		using var dropShadowPaint = new SKPaint
		{
			Color = new SKColor(0, 0, 0, 130), IsAntialias = true, Style = SKPaintStyle.Fill,
			MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, font.Size * 0.04f)
		};
		canvas.DrawText(text, x + dropOffset, y + dropOffset, align, font, dropShadowPaint);

		using var strokePaint = new SKPaint
			{ Color = Shadow, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = font.Size * 0.045f };
		using var fillPaint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
		canvas.DrawText(text, x, y, align, font, strokePaint);
		canvas.DrawText(text, x, y, align, font, fillPaint);
	}

	private static string F(double value, string format)
	{
		return value.ToString(format, CultureInfo.InvariantCulture);
	}

	private static string CardinalDirection(double heading)
	{
		string[] names = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
		var index = (int)Math.Round(AngleMath.NormalizeDegrees(heading) / 45.0) % 8;
		return names[index];
	}
}
