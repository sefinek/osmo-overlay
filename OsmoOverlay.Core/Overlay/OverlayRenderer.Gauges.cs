using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>The round gauge widgets: SpeedGauge, RollGauge, PitchGauge, SunWidget (sun position + G-force readout) and GMeter, plus TripProgressBar.</summary>
public sealed partial class OverlayRenderer
{
	private const double KmhToMph = 0.621371;

	// GMeter plots dynamic acceleration (cornering/braking-style forces) relative to the camera's own baseline, not its raw
	// orientation - see GMeterDeltas. Which accelerometer axes are sideways and forward is the camera format's
	// (ICameraFormat.Gravity, DerivedFrame.LateralAccelG/LongitudinalAccelG). This still only reads REORIENTATION (tilting
	// the camera itself), not necessarily translational G-force - a single accelerometer without a gyroscope cannot tell
	// "the sensor rotated" apart from "the sensor felt a real force".

	// User-configurable per widget (OverlayElement.GMeterFullScaleG) - how many G's the dot needs to
	// reach the ring's edge. Unlike Map's zoom factor, there's no "correct" value to derive from the
	// data itself: a cyclist and a track-day rider experience wildly different real G ranges, so a
	// fixed 1.0 either pins a motorsport recording at full deflection constantly or leaves a gentle
	// ride's dot barely twitching near the center.
	public const double GMeterFullScaleGDefault = 1.0;
	public const double GMeterFullScaleGMin = 0.1;
	public const double GMeterFullScaleGMax = 5.0;

	// User-configurable per widget (OverlayElement.TripArrivedToleranceMeters/TripArrivedLabel) - see
	// DrawTripProgressBar's remarks for why a tolerance is needed at all. 1.5m default: comfortably
	// above typical consumer GPS jitter, negligible next to any real trip distance.
	public const double TripArrivedToleranceMetersDefault = 1.5;
	public const string TripArrivedLabelDefault = "FINISH";


	private static SKPaint CreateGaugeBandPaint(SKColor color)
	{
		return new SKPaint
		{
			Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 20, StrokeCap = SKStrokeCap.Butt
		};
	}

	/// <summary>
	///     Rounds the recording's actual max speed up to the next 10 km/h so the gauge scale matches
	///     this ride instead of a fixed 60 km/h that's meaningless for a walk or absurdly low for a car.
	///     A 20 km/h floor keeps the needle from pinning near full-scale on a near-stationary clip.
	/// </summary>
	private static double ComputeGaugeMaxSpeed(double observedSpeed)
	{
		double rounded = Math.Ceiling(Math.Max(observedSpeed, 1) / 10.0) * 10.0;
		return Math.Max(rounded, 20.0);
	}

	/// <summary>
	///     Same "round up to a nice number" rule as ComputeGaugeMaxSpeed, but computed per unit system
	///     at draw time instead of once in km/h - converting an already-rounded km/h max into mph would
	///     produce an ugly non-round number (e.g. 60 km/h -&gt; 37.28 mph).
	/// </summary>
	private double GaugeMaxSpeed(UnitSystem units)
	{
		double observed = units == UnitSystem.Imperial ? _observedMaxSpeedKmh * KmhToMph : _observedMaxSpeedKmh;
		return ComputeGaugeMaxSpeed(observed);
	}

	private void DrawSunWidget(SKCanvas canvas, DerivedFrame frame, SunWidgetElement element)
	{
		const float cx = 0;
		const float cy = 0;

		DrawPanelShadow(canvas, cx, cy, OverlayElementBounds.SunRadius);

		canvas.DrawCircle(cx, cy, OverlayElementBounds.SunRadius, _panelFillPaint);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.SunRadius, _ringStroke3White140);

		double relativeAzimuthRad = AngleMath.DegToRad(frame.Sun.AzimuthDegrees - frame.HeadingDegrees);
		double elevationClamped = Math.Clamp(frame.Sun.ElevationDegrees, -20, 90);
		double radiusFactor = 1.0 - (elevationClamped + 20) / 110.0;

		float dotX = cx + (float)(Math.Sin(relativeAzimuthRad) * OverlayElementBounds.SunRadius * 0.8 * radiusFactor);
		float dotY = cy - (float)(Math.Cos(relativeAzimuthRad) * OverlayElementBounds.SunRadius * 0.8 * radiusFactor);

		float sunDotRadius = frame.Sun.ElevationDegrees > 0 ? 16 : 10;
		canvas.DrawCircle(dotX, dotY, sunDotRadius, _sunFillPaint);
		canvas.DrawCircle(dotX, dotY, sunDotRadius, _blackStroke2);

		string gText = $"{F(frame.SmoothedGForce, "0.0")}G";
		DrawOutlined(canvas, gText, cx, cy + OverlayElementBounds.SunRadius + OverlayElementBounds.LabelBelowRadiusOffset,
			TextFont(element, OverlayElementBounds.LabelFontSize), TextColorOf(element), SKTextAlign.Center,
			outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);
	}

	private void DrawRollGauge(SKCanvas canvas, RollGaugeElement element, double rollDegrees)
	{
		const float radius = OverlayElementBounds.TiltRadius;

		DrawTiltPanel(canvas);

		// Roll is +-90 at most, so doubling it maps the full physical range onto the full ring - level sits at
		// the top, and either direction sweeps round to meet at the bottom for a full 90 degree lean.
		double angleRad = AngleMath.DegToRad(270 + Math.Clamp(rollDegrees, -90, 90) * 2);
		canvas.DrawCircle((float)(Math.Cos(angleRad) * radius), (float)(Math.Sin(angleRad) * radius), 12, _dotFillAccent);

		DrawOutlined(canvas, $"{F(rollDegrees, "0")}°", 0, 16, TextFont(element, OverlayElementBounds.LabelFontSize),
			TextColorOf(element), SKTextAlign.Center, outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);
	}

	/// <summary>
	///     Seen from the side, facing right: level at 3 o'clock (the tick), nose up moves the dot up the right half of the
	///     ring, nose down moves it down - +-90 covers the half. The line from the dot inwards reads as the camera's axis.
	/// </summary>
	private void DrawPitchGauge(SKCanvas canvas, PitchGaugeElement element, double pitchDegrees)
	{
		const float radius = OverlayElementBounds.TiltRadius;

		DrawTiltPanel(canvas);
		canvas.DrawLine(radius - 18, 0, radius, 0, _ringStroke3White160);

		double angleRad = AngleMath.DegToRad(-Math.Clamp(pitchDegrees, -90, 90));
		(float cos, float sin) = ((float)Math.Cos(angleRad), (float)Math.Sin(angleRad));
		canvas.DrawLine(cos * radius * 0.5f, sin * radius * 0.5f, cos * radius, sin * radius, _accentStroke4Round);
		canvas.DrawCircle(cos * radius, sin * radius, 12, _dotFillAccent);

		DrawOutlined(canvas, $"{F(pitchDegrees, "0")}°", 0, 16, TextFont(element, OverlayElementBounds.LabelFontSize),
			TextColorOf(element), SKTextAlign.Center, outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);
	}

	private void DrawTiltPanel(SKCanvas canvas)
	{
		DrawPanelShadow(canvas, 0, 0, OverlayElementBounds.TiltRadius);
		canvas.DrawCircle(0, 0, OverlayElementBounds.TiltRadius, _panelFillPaint);
		canvas.DrawCircle(0, 0, OverlayElementBounds.TiltRadius, _ringStroke5White150);
	}

	private void DrawGMeter(SKCanvas canvas, DerivedFrame frame, GMeterElement element)
	{
		const float cx = 0;
		const float cy = 0;
		const float radius = OverlayElementBounds.GMeterRadius;

		DrawPanelShadow(canvas, cx, cy, radius);
		canvas.DrawCircle(cx, cy, radius, _panelFillPaint);
		canvas.DrawCircle(cx, cy, radius, _ringStroke5White150);
		canvas.DrawCircle(cx, cy, radius * 0.5f, _thinStroke2White70);

		canvas.DrawLine(cx - radius, cy, cx + radius, cy, _thinStroke2White70);
		canvas.DrawLine(cx, cy - radius, cx, cy + radius, _thinStroke2White70);

		double fullScaleG = Math.Clamp(element.GMeterFullScaleG, GMeterFullScaleGMin, GMeterFullScaleGMax);
		(double lateral, double longitudinal) = GMeterDelta(frame);
		float dotX = cx + (float)Math.Clamp(lateral / fullScaleG, -1, 1) * radius;
		float dotY = cy - (float)Math.Clamp(longitudinal / fullScaleG, -1, 1) * radius;

		canvas.DrawCircle(dotX, dotY, 12, _dotOutlineBlackFill);
		canvas.DrawCircle(dotX, dotY, 9, _dotFillAccent);

		double magnitude = Math.Sqrt(lateral * lateral + longitudinal * longitudinal);
		DrawOutlined(canvas, $"{F(magnitude, "0.00")}G", cx, cy + radius + OverlayElementBounds.LabelBelowRadiusOffset,
			TextFont(element, OverlayElementBounds.LabelFontSize), TextColorOf(element), SKTextAlign.Center,
			outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);
	}

	private (double Lateral, double Longitudinal) GMeterDelta(DerivedFrame frame)
	{
		int index = TelemetryProcessor.FindIndex(_allFrames, frame.Raw.SampleTimeSeconds);
		return index < _gMeterDeltas.Length ? _gMeterDeltas[index] : (0, 0);
	}

	private const float RingArcStartAngle = 92f;
	private const float RingArcSweep = 298f;
	private const float RingArcRadius = 180f;
	private const float RingOuterRadius = 220f;

	private static SKPath CreateRingNeedle()
	{
		using var builder = new SKPathBuilder();
		builder.MoveTo(RingArcRadius - 15, 0);
		builder.LineTo(RingOuterRadius + 30, -22);
		builder.LineTo(RingOuterRadius + 30, 22);
		builder.Close();
		return builder.Detach();
	}

	private void DrawRingSpeedGauge(SKCanvas canvas, SpeedGaugeElement element, double speedKmh)
	{
		bool imperial = element.Units == UnitSystem.Imperial;
		double displaySpeed = imperial ? speedKmh * KmhToMph : speedKmh;
		double fraction = Math.Clamp(displaySpeed / GaugeMaxSpeed(element.Units), 0, 1);

		// The outer ring spans the same angles as the colored arc.
		var outerRect = new SKRect(-RingOuterRadius, -RingOuterRadius, RingOuterRadius, RingOuterRadius);
		_speedRingOuterPaint.StrokeWidth = 22;
		_speedRingOuterPaint.Color = new SKColor(255, 255, 255, 70);
		canvas.DrawArc(outerRect, RingArcStartAngle, RingArcSweep, false, _speedRingOuterPaint);
		var edgeRect = SKRect.Inflate(outerRect, 11, 11);
		var innerEdgeRect = SKRect.Inflate(outerRect, -11, -11);
		canvas.DrawArc(edgeRect, RingArcStartAngle, RingArcSweep, false, _ringStroke3White160);
		canvas.DrawArc(innerEdgeRect, RingArcStartAngle, RingArcSweep, false, _ringStroke3White160);

		var arcRect = new SKRect(-RingArcRadius, -RingArcRadius, RingArcRadius, RingArcRadius);
		canvas.DrawArc(arcRect, RingArcStartAngle, RingArcSweep, false, _speedRingArcPaint);

		canvas.Save();
		canvas.RotateDegrees((float)(RingArcStartAngle + RingArcSweep * fraction));
		canvas.DrawPath(_speedRingNeedle, _whiteFill);
		canvas.Restore();

		SKFont speedFont = TextFont(element, 133);
		SKFont speedUnitFont = TextFont(element, 44);
		SKColor textColor = TextColorOf(element);
		SKColor outlineColor = OutlineColorOf(element);

		DrawOutlined(canvas, F(displaySpeed, "0"), 0, 106, speedFont, textColor, SKTextAlign.Center,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, imperial ? "MPH" : "KM/H", 32, 156, speedUnitFont, textColor, SKTextAlign.Left,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
	}

	private void DrawSpeedGauge(SKCanvas canvas, SpeedGaugeElement element, double speedKmh)
	{
		if (element.Theme == SpeedGaugeTheme.Ring)
		{
			DrawRingSpeedGauge(canvas, element, speedKmh);
			return;
		}

		float cx = 0;
		float cy = 0;

		bool imperial = element.Units == UnitSystem.Imperial;
		double displaySpeed = imperial ? speedKmh * KmhToMph : speedKmh;
		double maxDisplaySpeed = GaugeMaxSpeed(element.Units);

		float radius = OverlayElementBounds.SpeedRadius;
		var rect = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
		const float startAngle = 135f;
		const float sweep = 270f;

		DrawPanelShadow(canvas, cx, cy, radius - 4);

		canvas.DrawCircle(cx, cy, radius - 4, _panelFillPaint);
		canvas.DrawCircle(cx, cy, radius - 4, _ringStroke3White140);

		canvas.DrawArc(rect, startAngle, sweep * 0.45f, false, _speedBandGreen);
		canvas.DrawArc(rect, startAngle + sweep * 0.45f, sweep * 0.25f, false, _speedBandYellow);
		canvas.DrawArc(rect, startAngle + sweep * 0.70f, sweep * 0.18f, false, _speedBandOrange);
		canvas.DrawArc(rect, startAngle + sweep * 0.88f, sweep * 0.12f, false, _speedBandRed);

		double clamped = Math.Clamp(displaySpeed, 0, maxDisplaySpeed);
		double needleAngleDeg = startAngle + sweep * (clamped / maxDisplaySpeed);
		double needleRad = AngleMath.DegToRad(needleAngleDeg);
		float needleX = cx + (float)(Math.Cos(needleRad) * (radius - 34));
		float needleY = cy + (float)(Math.Sin(needleRad) * (radius - 34));

		canvas.DrawLine(cx, cy, needleX, needleY, _whiteStroke6Round);

		canvas.DrawCircle(cx, cy, 9, _dotOutlineBlackFill);
		canvas.DrawCircle(cx, cy, 6, _dotFillAccent);

		SKFont speedFont = TextFont(element, OverlayElementBounds.SpeedFontSize);
		SKFont speedUnitFont = TextFont(element, OverlayElementBounds.SpeedUnitFontSize);
		SKColor textColor = TextColorOf(element);
		SKColor outlineColor = OutlineColorOf(element);

		string speedText = F(displaySpeed, "0");
		float textWidth = speedFont.MeasureText(speedText);
		DrawOutlined(canvas, speedText, cx - textWidth / 2, cy + radius - 90, speedFont, textColor,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, imperial ? "MPH" : "KM/H", cx, cy + radius - 30, speedUnitFont, textColor, SKTextAlign.Center,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
	}

	/// <summary>
	///     A track + moving dot, same "shape encodes a live value" idea as the round gauges above, just
	///     laid out horizontally. Progress is cumulative distance so far over the route's total distance
	///     (_totalDistanceMeters, the last frame's CumulativeDistanceMeters, cached once in the
	///     constructor) - 0 at the start of the recording, 1 on its last frame.
	/// </summary>
	private void DrawTripProgressBar(SKCanvas canvas, DerivedFrame frame, TripProgressBarElement element)
	{
		const float halfWidth = OverlayElementBounds.ProgressBarWidth / 2f;
		const float trackHeight = 14f;

		double progress = _totalDistanceMeters > 0
			? Math.Clamp(frame.CumulativeDistanceMeters / _totalDistanceMeters, 0.0, 1.0)
			: 0.0;

		var trackRect = new SKRect(-halfWidth, -trackHeight / 2, halfWidth, trackHeight / 2);
		canvas.DrawRoundRect(trackRect, trackHeight / 2, trackHeight / 2, _panelFillPaint);
		canvas.DrawRoundRect(trackRect, trackHeight / 2, trackHeight / 2, _thinStroke2White70);

		float dotX = -halfWidth + (float)(OverlayElementBounds.ProgressBarWidth * progress);
		if (progress > 0)
		{
			var fillRect = new SKRect(-halfWidth, -trackHeight / 2, dotX, trackHeight / 2);
			canvas.DrawRoundRect(fillRect, trackHeight / 2, trackHeight / 2, _dotFillAccent);
		}

		canvas.DrawCircle(dotX, 0, 16, _dotOutlineBlackFill);
		canvas.DrawCircle(dotX, 0, 12, _dotFillAccent);

		double remainingMeters = Math.Max(_totalDistanceMeters - frame.CumulativeDistanceMeters, 0);

		// A stationary GPS receiver's position jitter means remainingMeters almost never settles on
		// exactly 0 (see element.TripArrivedToleranceMeters) - within that margin, treat the trip as
		// arrived and show a clean "100%"/TripArrivedLabel instead of the two numbers rounding
		// independently into a contradiction like "100%, 0.4 M LEFT".
		bool arrived = remainingMeters <= element.TripArrivedToleranceMeters;
		double displayPercent = arrived ? 100.0 : Math.Min(progress * 100, 99);
		DrawOutlined(canvas, $"{F(displayPercent, "0")}%", -halfWidth, -26, _smallFont, White);

		if (arrived)
		{
			DrawOutlined(canvas, element.TripArrivedLabel, halfWidth, -26, _smallFont, White, SKTextAlign.Right);
		}
		else
		{
			(string remainingValue, string remainingUnit) = FormatDistance(remainingMeters, element.Units);
			DrawOutlined(canvas, $"{remainingValue} {remainingUnit} LEFT", halfWidth, -26, _smallFont, White, SKTextAlign.Right);
		}
	}
}
