using OsmoOverlay.Core.Telemetry;
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
	private static readonly SKColor AircraftFrontColor = SKColor.Parse("#1E9BE6");
	private static readonly SKColor AircraftRearColor = SKColor.Parse("#FF8A1F");

	// A dot is yellow while the stick is within this share of its travel from the center, red from the second, orange between.
	private const double StickCenterShare = 0.2, StickEndShare = 0.92;

	// Degrees of pitch the attitude indicator shows from its center to its edge, and the elevation the 3D view looks from.
	private const double HorizonPitchSpan = 40;
	private const double OrientationViewElevationDegrees = 32;

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

		double elevation = AngleMath.DegToRad(OrientationViewElevationDegrees);
		(double sinE, double cosE) = (Math.Sin(elevation), Math.Cos(elevation));
		float scale = radius * 0.88f;

		(double X, double Y, double Z) Place((double X, double Y, double Z) local)
		{
			double roll = AngleMath.DegToRad(attitude.RollDegrees), pitch = AngleMath.DegToRad(attitude.PitchDegrees), yaw = AngleMath.DegToRad(attitude.YawDegrees);
			double x = local.X * Math.Cos(roll) + local.Z * Math.Sin(roll), z = -local.X * Math.Sin(roll) + local.Z * Math.Cos(roll), y = local.Y;
			double y2 = y * Math.Cos(pitch) - z * Math.Sin(pitch), z2 = y * Math.Sin(pitch) + z * Math.Cos(pitch);
			return (x * Math.Cos(yaw) + y2 * Math.Sin(yaw), -x * Math.Sin(yaw) + y2 * Math.Cos(yaw), z2);
		}

		SKPoint Project((double X, double Y, double Z) world)
		{
			return new SKPoint((float)(world.X * scale), (float)(-(world.Z * cosE + world.Y * sinE) * scale) + 12);
		}

		double Nearness((double X, double Y, double Z) world)
		{
			return -world.Y * cosE + world.Z * sinE;
		}

		const double arm = 0.62, rotor = 0.34;
		var parts = new List<(double Nearness, Action Draw)>();
		foreach ((double sx, double sy) in new[] { (-1.0, 1.0), (1.0, 1.0), (-1.0, -1.0), (1.0, -1.0) })
		{
			(double X, double Y, double Z) tip = Place((sx * arm, sy * arm, 0.06));
			bool front = sy > 0;
			parts.Add((Nearness(tip), () =>
			{
				SKPoint from = Project(Place((0, 0, 0))), to = Project(tip);
				_droneControlPaint.Style = SKPaintStyle.Stroke;
				_droneControlPaint.StrokeWidth = 7;
				_droneControlPaint.Color = new SKColor(70, 78, 88);
				canvas.DrawLine(from, to, _droneControlPaint);

				using var builder = new SKPathBuilder();
				const int steps = 28;
				for (int i = 0; i < steps; i++)
				{
					double a = i * 2 * Math.PI / steps;
					SKPoint p = Project(Place((sx * arm + Math.Cos(a) * rotor, sy * arm + Math.Sin(a) * rotor, 0.06)));
					if (i == 0) builder.MoveTo(p);
					else builder.LineTo(p);
				}

				builder.Close();
				using SKPath disc = builder.Detach();
				_droneControlPaint.Style = SKPaintStyle.Fill;
				_droneControlPaint.Color = new SKColor(200, 210, 220, 90);
				canvas.DrawPath(disc, _droneControlPaint);
				_droneControlPaint.Style = SKPaintStyle.Stroke;
				_droneControlPaint.StrokeWidth = 3;
				_droneControlPaint.Color = front ? AircraftFrontColor.WithAlpha(230) : new SKColor(210, 218, 226, 230);
				canvas.DrawPath(disc, _droneControlPaint);
			}));
		}

		parts.Add((Nearness(Place((0, 0, 0))), () =>
		{
			SKPoint rear = Project(Place((0, -0.42, 0.06))), center = Project(Place((0, 0, 0.06))), front = Project(Place((0, 0.42, 0.06)));
			_droneControlPaint.Style = SKPaintStyle.Stroke;
			_droneControlPaint.StrokeWidth = 24;
			_droneControlPaint.Color = AircraftRearColor;
			canvas.DrawLine(rear, center, _droneControlPaint);
			_droneControlPaint.Color = AircraftFrontColor;
			canvas.DrawLine(center, front, _droneControlPaint);
		}));

		foreach ((double _, Action draw) in parts.OrderBy(p => p.Nearness)) draw();
	}
}
