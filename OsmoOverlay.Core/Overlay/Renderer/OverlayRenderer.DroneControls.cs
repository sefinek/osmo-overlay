using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;
using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     What a drone's flight log says of the aircraft itself: the remote's sticks (RcSticksElement), the attitude against the horizon
///     (AttitudeIndicatorElement) and the aircraft drawn at its attitude and heading (Orientation3DElement).
/// </summary>
public sealed partial class OverlayRenderer
{
	private static readonly SKColor StickCenterColor = SKColor.Parse("#F2D31B");
	private static readonly SKColor StickMidColor = SKColor.Parse("#FF9A2E");
	private static readonly SKColor StickEndColor = SKColor.Parse("#FF1F1F");
	private static readonly SKColor HorizonGroundColor = SKColor.Parse("#1873B8");

	// A dot is yellow while the stick is within this share of its travel from the center, red from the second, orange between.
	private const double StickCenterShare = 0.2, StickEndShare = 0.92;

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
		double travel = Math.Max(Math.Abs(x), Math.Abs(y));
		return travel < StickCenterShare ? StickCenterColor : travel < StickEndShare ? StickMidColor : StickEndColor;
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
		_droneModel.DrawPerspective(canvas, new SKPoint(0, 12), radius * 0.88f, attitude, rotors, OrientationViewElevationDegrees);
	}
}
