using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>The round gauge widgets: SpeedGauge, PitchGauge, SunWidget (sun position + G-force readout) and GMeter.</summary>
public sealed partial class OverlayRenderer
{
	private const double KmhToMph = 0.621371;

	// GMeter plots dynamic acceleration (cornering/braking-style forces), not the camera's raw
	// orientation - a raw reading sits permanently off-center depending on how the camera happens to
	// be mounted, since gravity itself shows up on whichever axis is "down" for that mount.
	// GMeterBaselineSeconds is a slow-moving average acting as each axis's own "at rest" zero point, so
	// the dot reads relative to it instead of an absolute, mount-dependent reading - mirrors
	// GetMapZoomFactor's smoothing/jump-reset shape. GMeterSmoothingSeconds is a second, much faster
	// EMA applied to the signal itself (same time constant as TelemetryProcessor's Pitch/GForce
	// smoothing) - raw per-sample accelerometer readings are noisy (vibration, bumps), and unlike
	// those two gauges this one never went through TelemetryProcessor, so without this the dot/reading
	// would jitter with every sample instead of tracking actual cornering/braking swings.
	// Plots AccelY (lateral, screen-horizontal) and AccelX (longitudinal, screen-vertical) - the same
	// two axes PitchGauge's own comment already verified (AccelX = forward/backward tilt, AccelY =
	// left/right lean), now cross-checked a second time against a dedicated tilt-test recording
	// (right/left/floor/ceiling tilts at known timestamps): right tilt showed AccelY swing hugely
	// negative with AccelX flat, left tilt the mirror positive swing, floor/ceiling tilts showed the
	// opposite pattern on AccelX with AccelY flat. AccelZ was NOT used here (an earlier version plotted
	// it) - it moves under both pitch and roll alike (the "remaining" component of the fixed ~1G
	// vector), so pairing it with AccelX made a pure left/right tilt visibly move the dot on what was
	// meant to be the up/down axis. This still only reads REORIENTATION (tilting the camera itself),
	// not necessarily translational G-force - a single accelerometer without a gyroscope cannot tell
	// "the sensor rotated" apart from "the sensor felt a real force", so a deliberate/incidental tilt
	// still shows up here same as a real cornering/braking G would.
	private const double GMeterBaselineSeconds = 8.0;
	private const double GMeterSmoothingSeconds = 0.3;

	// User-configurable per widget (OverlayElement.GMeterFullScaleG) - how many G's the dot needs to
	// reach the ring's edge. Unlike Map's zoom factor, there's no "correct" value to derive from the
	// data itself: a cyclist and a track-day rider experience wildly different real G ranges, so a
	// fixed 1.0 either pins a motorsport recording at full deflection constantly or leaves a gentle
	// ride's dot barely twitching near the center.
	public const double GMeterFullScaleGDefault = 1.0;
	public const double GMeterFullScaleGMin = 0.1;
	public const double GMeterFullScaleGMax = 5.0;

	private readonly ResettableEma _gMeterBaselineLateralEma = new();
	private readonly ResettableEma _gMeterBaselineLongitudinalEma = new();
	private readonly ResettableEma _gMeterSmoothedLateralEma = new();
	private readonly ResettableEma _gMeterSmoothedLongitudinalEma = new();

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
		var rounded = Math.Ceiling(Math.Max(observedSpeed, 1) / 10.0) * 10.0;
		return Math.Max(rounded, 20.0);
	}

	/// <summary>
	///     Same "round up to a nice number" rule as ComputeGaugeMaxSpeed, but computed per unit system
	///     at draw time instead of once in km/h - converting an already-rounded km/h max into mph would
	///     produce an ugly non-round number (e.g. 60 km/h -&gt; 37.28 mph).
	/// </summary>
	private double GaugeMaxSpeed(UnitSystem units)
	{
		var observed = units == UnitSystem.Imperial ? _observedMaxSpeedKmh * KmhToMph : _observedMaxSpeedKmh;
		return ComputeGaugeMaxSpeed(observed);
	}

	private void DrawSunWidget(SKCanvas canvas, DerivedFrame frame, float cx, float cy)
	{
		canvas.Save();
		canvas.Translate(cx, cy);
		canvas.Scale(_scale, _scale);
		cx = 0;
		cy = 0;

		DrawPanelShadow(canvas, cx, cy, OverlayElementBounds.SunRadius);

		canvas.DrawCircle(cx, cy, OverlayElementBounds.SunRadius, _panelFillPaint);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.SunRadius, _ringStroke3White140);

		var relativeAzimuthRad = AngleMath.DegToRad(frame.Sun.AzimuthDegrees - frame.HeadingDegrees);
		var elevationClamped = Math.Clamp(frame.Sun.ElevationDegrees, -20, 90);
		var radiusFactor = 1.0 - (elevationClamped + 20) / 110.0;

		var dotX = cx + (float)(Math.Sin(relativeAzimuthRad) * OverlayElementBounds.SunRadius * 0.8 * radiusFactor);
		var dotY = cy - (float)(Math.Cos(relativeAzimuthRad) * OverlayElementBounds.SunRadius * 0.8 * radiusFactor);

		float sunDotRadius = frame.Sun.ElevationDegrees > 0 ? 16 : 10;
		canvas.DrawCircle(dotX, dotY, sunDotRadius, _sunFillPaint);
		canvas.DrawCircle(dotX, dotY, sunDotRadius, _blackStroke2);

		var gText = $"{F(frame.SmoothedGForce, "0.0")}G";
		DrawOutlined(canvas, gText, cx, cy + OverlayElementBounds.SunRadius + 56, _labelFont, White,
			SKTextAlign.Center);

		canvas.Restore();
	}

	private void DrawPitchGauge(SKCanvas canvas, float cx, float cy, double pitchDegrees)
	{
		canvas.Save();
		canvas.Translate(cx, cy);
		canvas.Scale(_scale, _scale);
		cx = 0;
		cy = 0;

		DrawPanelShadow(canvas, cx, cy, OverlayElementBounds.PitchRadius);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.PitchRadius, _panelFillPaint);
		canvas.DrawCircle(cx, cy, OverlayElementBounds.PitchRadius, _ringStroke5White150);

		// AccelY-derived roll saturates at +-90 (see TelemetryProcessor), so doubling it here maps the
		// full physical range onto the full 360 degree ring - a level camera sits at top, and either
		// tilt direction sweeps all the way around to meet at the bottom for a full 90 degree roll.
		var clamped = Math.Clamp(pitchDegrees, -90, 90);
		var angleDeg = 270 + clamped * 2;
		var angleRad = AngleMath.DegToRad(angleDeg);
		var dotX = cx + (float)(Math.Cos(angleRad) * OverlayElementBounds.PitchRadius);
		var dotY = cy + (float)(Math.Sin(angleRad) * OverlayElementBounds.PitchRadius);

		canvas.DrawCircle(dotX, dotY, 12, _dotFillAccent);

		DrawOutlined(canvas, $"{F(pitchDegrees, "0")}°", cx, cy + 16, _labelFont, White, SKTextAlign.Center);

		canvas.Restore();
	}

	private void DrawGMeter(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		const float cx = 0;
		const float cy = 0;
		const float radius = OverlayElementBounds.GMeterRadius;

		DrawPanelShadow(canvas, cx, cy, radius);
		canvas.DrawCircle(cx, cy, radius, _panelFillPaint);
		canvas.DrawCircle(cx, cy, radius, _ringStroke5White150);
		canvas.DrawCircle(cx, cy, radius * 0.5f, _thinStroke2White70);

		canvas.DrawLine(cx - radius, cy, cx + radius, cy, _thinStroke2White70);
		canvas.DrawLine(cx, cy - radius, cx, cy + radius, _thinStroke2White70);

		var fullScaleG = Math.Clamp(element.GMeterFullScaleG, GMeterFullScaleGMin, GMeterFullScaleGMax);
		var (lateral, longitudinal) = SmoothGMeterDelta(frame.Raw);
		var dotX = cx + (float)Math.Clamp(lateral / fullScaleG, -1, 1) * radius;
		var dotY = cy - (float)Math.Clamp(longitudinal / fullScaleG, -1, 1) * radius;

		canvas.DrawCircle(dotX, dotY, 12, _dotOutlineBlackFill);
		canvas.DrawCircle(dotX, dotY, 9, _dotFillAccent);

		var magnitude = Math.Sqrt(lateral * lateral + longitudinal * longitudinal);
		DrawOutlined(canvas, $"{F(magnitude, "0.00")}G", cx, cy + radius + 56, _labelFont, White, SKTextAlign.Center);

		canvas.Restore();
	}

	/// <summary>
	///     Dynamic acceleration relative to the camera's own slow-moving baseline (see GMeterBaselineSeconds
	///     above `DrawGMeter`), with the signal itself run through a second, much faster EMA
	///     (GMeterSmoothingSeconds) to tame per-sample noise - two EMAs at different time constants
	///     racing toward the same raw reading, not a "smoothed minus itself" no-op. Both are driven by the
	///     same ResettableEma GetMapZoomFactor uses, and since all four instances below are always updated
	///     together with the same `seconds`, their independent jump-reset checks stay in lockstep - a
	///     backwards/large time gap resets all four straight to the raw reading, same as if they shared
	///     one clock.
	/// </summary>
	private (double Lateral, double Longitudinal) SmoothGMeterDelta(TelemetryFrame raw)
	{
		var seconds = raw.SampleTimeSeconds;
		var baselineLateral = _gMeterBaselineLateralEma.Update(seconds, raw.AccelY, GMeterBaselineSeconds);
		var baselineLongitudinal = _gMeterBaselineLongitudinalEma.Update(seconds, raw.AccelX, GMeterBaselineSeconds);
		var smoothedLateral = _gMeterSmoothedLateralEma.Update(seconds, raw.AccelY, GMeterSmoothingSeconds);
		var smoothedLongitudinal = _gMeterSmoothedLongitudinalEma.Update(seconds, raw.AccelX, GMeterSmoothingSeconds);

		// Negated: right tilt showed AccelY swing negative in the verification recording (see the
		// comment above DrawGMeter), but "tilt right" should move the dot right (positive), same
		// left-is-negative/right-is-positive convention as screen X.
		return (-(smoothedLateral - baselineLateral), smoothedLongitudinal - baselineLongitudinal);
	}

	private void DrawSpeedGauge(SKCanvas canvas, OverlayElement element, double speedKmh)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);
		float cx = 0;
		float cy = 0;

		var imperial = element.Units == UnitSystem.Imperial;
		var displaySpeed = imperial ? speedKmh * KmhToMph : speedKmh;
		var maxDisplaySpeed = GaugeMaxSpeed(element.Units);

		var radius = OverlayElementBounds.SpeedRadius;
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

		var clamped = Math.Clamp(displaySpeed, 0, maxDisplaySpeed);
		var needleAngleDeg = startAngle + sweep * (clamped / maxDisplaySpeed);
		var needleRad = AngleMath.DegToRad(needleAngleDeg);
		var needleX = cx + (float)(Math.Cos(needleRad) * (radius - 34));
		var needleY = cy + (float)(Math.Sin(needleRad) * (radius - 34));

		canvas.DrawLine(cx, cy, needleX, needleY, _whiteStroke6Round);

		canvas.DrawCircle(cx, cy, 9, _dotOutlineBlackFill);
		canvas.DrawCircle(cx, cy, 6, _dotFillAccent);

		var speedText = F(displaySpeed, "0");
		var textWidth = _speedFont.MeasureText(speedText);
		DrawOutlined(canvas, speedText, cx - textWidth / 2, cy + radius - 90, _speedFont, White);
		DrawOutlined(canvas, imperial ? "MPH" : "KM/H", cx, cy + radius - 30, _speedUnitFont, White, SKTextAlign.Center);

		canvas.Restore();
	}

	/// <summary>
	///     A track + moving dot, same "shape encodes a live value" idea as the round gauges above, just
	///     laid out horizontally. Progress is cumulative distance so far over the route's total distance
	///     (_totalDistanceMeters, the last frame's CumulativeDistanceMeters, cached once in the
	///     constructor) - 0 at the start of the recording, 1 on its last frame.
	/// </summary>
	private void DrawTripProgressBar(SKCanvas canvas, DerivedFrame frame, OverlayElement element)
	{
		canvas.Save();
		canvas.Translate(element.X, element.Y);
		canvas.Scale(_scale, _scale);

		const float halfWidth = OverlayElementBounds.ProgressBarWidth / 2f;
		const float trackHeight = 14f;

		var progress = _totalDistanceMeters > 0
			? Math.Clamp(frame.CumulativeDistanceMeters / _totalDistanceMeters, 0.0, 1.0)
			: 0.0;

		var trackRect = new SKRect(-halfWidth, -trackHeight / 2, halfWidth, trackHeight / 2);
		canvas.DrawRoundRect(trackRect, trackHeight / 2, trackHeight / 2, _panelFillPaint);
		canvas.DrawRoundRect(trackRect, trackHeight / 2, trackHeight / 2, _thinStroke2White70);

		var dotX = -halfWidth + (float)(OverlayElementBounds.ProgressBarWidth * progress);
		if (progress > 0)
		{
			var fillRect = new SKRect(-halfWidth, -trackHeight / 2, dotX, trackHeight / 2);
			canvas.DrawRoundRect(fillRect, trackHeight / 2, trackHeight / 2, _dotFillAccent);
		}

		canvas.DrawCircle(dotX, 0, 16, _dotOutlineBlackFill);
		canvas.DrawCircle(dotX, 0, 12, _dotFillAccent);

		var (remainingValue, remainingUnit) =
			FormatDistance(Math.Max(_totalDistanceMeters - frame.CumulativeDistanceMeters, 0), element.Units);
		DrawOutlined(canvas, $"{F(progress * 100, "0")}%", -halfWidth, -26, _smallFont, White);
		DrawOutlined(canvas, $"{remainingValue} {remainingUnit} LEFT", halfWidth, -26, _smallFont, White, SKTextAlign.Right);

		canvas.Restore();
	}
}
