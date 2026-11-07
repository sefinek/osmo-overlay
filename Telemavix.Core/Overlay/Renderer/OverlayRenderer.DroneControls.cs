using SkiaSharp;
using Telemavix.Core.Telemetry;
using Telemavix.Core.Telemetry.External;

namespace Telemavix.Core.Overlay;

/// <summary>
///     What a drone's flight log says of the aircraft itself: the remote's sticks (RcSticksElement), the attitude against the horizon
///     (AttitudeIndicatorElement) and the aircraft drawn at its attitude and heading (Orientation3DElement).
/// </summary>
public sealed partial class OverlayRenderer
{
	private static readonly SKColor HorizonGroundColor = SKColor.Parse("#1873B8");

	// A stick's dot runs through these colors as the stick moves from the center to the end of its travel: the attitude indicator's blue at rest,
	// straight to red at the end (blended in Oklab, so it passes violet on its own, never green: that would read as another state).
	private static readonly (double Travel, SKColor Color)[] StickGradient =
	[
		(0, SKColor.Parse("#1873B8")),
		(1, SKColor.Parse("#FF1F1F"))
	];

	// Degrees of pitch the attitude indicator shows from its center to its edge.
	private const double HorizonPitchSpan = 40;

	// The elevation the Orientation3D widget looks at the aircraft from.
	private const double OrientationViewElevationDegrees = 32;

	// The aircraft of the Orientation3D widget is the track map's (DroneModel), in perspective; its rotors' turn is a function of the frame set, kept until it changes.
	private readonly DroneModel _droneModel = new();
	private double[,]? _rotorPhases;
	private double _rotorReferenceKmh;

	private readonly SKPaint _droneControlPaint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

	internal static SKColor StickColor(double x, double y)
	{
		double travel = Math.Clamp(Math.Max(Math.Abs(x), Math.Abs(y)), 0, 1);
		for (int i = 1; i < StickGradient.Length; i++)
		{
			(double end, SKColor to) = StickGradient[i];
			if (travel > end) continue;

			(double start, SKColor from) = StickGradient[i - 1];
			return SpeedColorScale.Mix(from, to, (travel - start) / (end - start));
		}

		return StickGradient[^1].Color;
	}

	private void DrawRcSticks(SKCanvas canvas, DerivedFrame frame, RcSticksElement element)
	{
		DroneSticks sticks = frame.Raw.DroneSticks ?? new DroneSticks(0, 0, 0, 0);
		const float size = OverlayElementBounds.StickPadSize;
		DrawStickPad(canvas, element, 0, sticks.Rudder, sticks.Throttle);
		DrawStickPad(canvas, element, size + OverlayElementBounds.StickPadGap, sticks.Aileron, sticks.Elevator);
	}

	/// <summary>One square at `left`, with the stick's position: x to the right and y up, each -1 to 1.</summary>
	private void DrawStickPad(SKCanvas canvas, RcSticksElement element, float left, double x, double y)
	{
		const float size = OverlayElementBounds.StickPadSize, corner = 22, dot = 17;
		var pad = new SKRect(left, 0, left + size, size);
		canvas.DrawRoundRect(pad, corner, corner, PanelFillPaint(element));

		_droneControlPaint.Style = SKPaintStyle.Stroke;
		_droneControlPaint.StrokeWidth = 3;
		_droneControlPaint.Color = SKColors.White.WithAlpha(110);
		canvas.DrawLine(pad.MidX, pad.Top + 8, pad.MidX, pad.Bottom - 8, _droneControlPaint);
		canvas.DrawLine(pad.Left + 8, pad.MidY, pad.Right - 8, pad.MidY, _droneControlPaint);
		_droneControlPaint.StrokeWidth = 5;
		_droneControlPaint.Color = SKColors.White.WithAlpha(190);
		canvas.DrawRoundRect(pad, corner, corner, _droneControlPaint);

		float reach = size / 2 - dot - 8;
		float cx = pad.MidX + (float)Math.Clamp(x, -1, 1) * reach, cy = pad.MidY - (float)Math.Clamp(y, -1, 1) * reach;
		_droneControlPaint.StrokeWidth = 4;
		_droneControlPaint.Color = OutlineColorOf(element);
		canvas.DrawCircle(cx, cy, dot, _droneControlPaint);
		_droneControlPaint.Style = SKPaintStyle.Fill;
		_droneControlPaint.Color = StickColor(x, y);
		canvas.DrawCircle(cx, cy, dot, _droneControlPaint);
	}

	private void DrawAttitudeIndicator(SKCanvas canvas, DerivedFrame frame, AttitudeIndicatorElement element)
	{
		const float radius = OverlayElementBounds.DroneDialRadius;
		DroneAttitude attitude = frame.Raw.DroneAttitude ?? new DroneAttitude(0, 0, 0);
		canvas.DrawCircle(0, 0, radius, PanelFillPaint(element));

		// Nose up moves the horizon below the center, a right bank turns it counter-clockwise on the dial.
		float offset = (float)(Math.Clamp(attitude.PitchDegrees, -90, 90) / HorizonPitchSpan * radius);
		canvas.Save();
		using (var builder = new SKPathBuilder())
		{
			builder.AddCircle(0, 0, radius - 1);
			using SKPath clip = builder.Detach();
			canvas.ClipPath(clip, SKClipOperation.Intersect, true);
		}

		canvas.RotateDegrees((float)-Math.Clamp(attitude.RollDegrees, -180, 180));
		_droneControlPaint.Style = SKPaintStyle.Fill;
		_droneControlPaint.Color = HorizonGroundColor;
		canvas.DrawRect(-radius * 3, offset, radius * 6, radius * 6, _droneControlPaint);
		_droneControlPaint.Style = SKPaintStyle.Stroke;
		_droneControlPaint.StrokeWidth = 3;
		_droneControlPaint.Color = SKColors.White.WithAlpha(230);
		canvas.DrawLine(-radius * 3, offset, radius * 3, offset, _droneControlPaint);
		canvas.Restore();

		_droneControlPaint.Color = SKColors.White.WithAlpha(150);
		canvas.DrawLine(-radius, 0, -radius * 0.45f, 0, _droneControlPaint);
		canvas.DrawLine(radius * 0.45f, 0, radius, 0, _droneControlPaint);
		canvas.DrawLine(0, -radius, 0, -radius * 0.45f, _droneControlPaint);
		canvas.DrawLine(0, radius * 0.45f, 0, radius, _droneControlPaint);
		canvas.DrawCircle(0, 0, radius * 0.3f, _droneControlPaint);
		_droneControlPaint.StrokeWidth = 5;
		_droneControlPaint.Color = SKColors.White.WithAlpha(190);
		canvas.DrawCircle(0, 0, radius, _droneControlPaint);
	}

	private void DrawOrientation3D(SKCanvas canvas, DerivedFrame frame, Orientation3DElement element)
	{
		const float radius = OverlayElementBounds.DroneDialRadius;
		DroneAttitude attitude = frame.Raw.DroneAttitude ?? new DroneAttitude(0, 0, 0);
		canvas.DrawCircle(0, 0, radius, PanelFillPaint(element));

		_droneControlPaint.Style = SKPaintStyle.Stroke;
		_droneControlPaint.StrokeWidth = 5;
		_droneControlPaint.Color = SKColors.White.WithAlpha(190);
		canvas.DrawCircle(0, 0, radius, _droneControlPaint);

		SKFont font = TextFont(element, OverlayElementBounds.LabelFontSize);
		DrawOutlined(canvas, "N", 0, -radius + 38, font, TextColorOf(element), SKTextAlign.Center, outlineColor: OutlineColorOf(element), outlineWidthScale: element.OutlineWidth);

		if (_rotorPhases is null)
		{
			_rotorReferenceKmh = TrackCamera.ReferenceKmh(_allFrames);
			_rotorPhases = RotorSpin.Phases(_allFrames, _rotorReferenceKmh);
		}

		Span<RotorSpin.RotorPose> rotors = stackalloc RotorSpin.RotorPose[RotorSpin.Count];
		if (element.SpinRotors) RotorSpin.At(_allFrames, _rotorPhases, frame.Raw.SampleTimeSeconds, _rotorReferenceKmh, rotors);
		_droneModel.DrawPerspective(canvas, new SKPoint(0, 14), radius * 0.74f, attitude, rotors, OrientationViewElevationDegrees);
	}
}
