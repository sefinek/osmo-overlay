using OsmoOverlay.Core.Overlay;
using SkiaSharp;

namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     The aircraft seen from above, shared by the track's map (TrackBackdrop) and the Orientation3D widget so there is one model:
///     turned to its heading (yaw, clockwise from north, the map's north being up), with a soft shadow and optionally the camera's view
///     ahead of it. A tilt foreshortens it as it would be seen (a bank narrows it, a pitch shortens it) and the raised side's rotors show
///     brighter. Each rotor turns as far as its pose says (RotorSpin), the diagonal pairs the opposite ways round, and smears behind its
///     blades like an exposure of a thirtieth of a second - the faster, the longer, the fainter the blades and the fuller the disc.
///     Front rotors are orange, rear ones light, so the nose reads at a glance. Not thread-safe: it keeps its paints.
/// </summary>
internal sealed class DroneModel : IDisposable
{
	// A rotor's blades are drawn as far back as a camera's exposure would smear them, in this many copies.
	private const double Exposure = 1 / 30.0;
	private const int Ghosts = 6;

	private static readonly SKColor FrontRotor = new(0xFF, 0x6A, 0x3D, 210);
	private static readonly SKColor RearRotor = new(0xE8, 0xE8, 0xEC, 190);
	private static readonly SKColor Hub = new(0x22, 0x22, 0x26);
	private static readonly SKColor Blade = new(0x18, 0x18, 0x1C);

	private readonly SKPaint _droneOutline = new() { Color = new SKColor(0, 0, 0, 200), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _droneArm = new() { Color = new SKColor(0x30, 0x30, 0x34), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _droneFill = new() { IsAntialias = true };
	private readonly SKPaint _droneRim = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke };
	private readonly SKPaint _rotorFill = new() { IsAntialias = true };
	private readonly SKPaint _rotorRing = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
	private readonly SKPaint _blade = new() { IsAntialias = true };
	private readonly SKPaint _bladeLine = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _shadowPaint = new() { Color = new SKColor(0, 0, 0, 120), IsAntialias = true };
	private readonly SKPaint _beamPaint = new() { IsAntialias = true };
	private readonly SKPath _nose = NoseOf(1);
	private readonly SKPath _beam = BeamOf();
	private readonly SKShader _beamShader = SKShader.CreateLinearGradient(new SKPoint(0, -0.3f), new SKPoint(0, -3f),
		[new SKColor(255, 255, 255, 70), new SKColor(255, 255, 255, 0)], SKShaderTileMode.Clamp);
	private float _shadowSize;

	public DroneModel()
	{
		_beamPaint.Shader = _beamShader;
	}

	private static SKPath BeamOf()
	{
		using var builder = new SKPathBuilder();
		builder.MoveTo(0, -0.3f);
		builder.LineTo(-0.85f, -3f);
		builder.LineTo(0.85f, -3f);
		builder.Close();
		return builder.Detach();
	}

	private static SKPath NoseOf(float size)
	{
		using var builder = new SKPathBuilder();
		builder.MoveTo(0, -size * 0.3f);
		builder.LineTo(size * 0.16f, size * 0.08f);
		builder.LineTo(-size * 0.16f, size * 0.08f);
		builder.Close();
		return builder.Detach();
	}

	/// <param name="size">The body's length; the rotors reach about 1.3 times that from the center.</param>
	/// <param name="beam">The camera's view ahead of the nose.</param>
	public void Draw(SKCanvas canvas, SKPoint center, float size, DroneAttitude attitude, ReadOnlySpan<RotorSpin.RotorPose> rotors, bool beam)
	{
		float reach = size * 0.82f, rotorRadius = size * 0.46f;
		_droneOutline.StrokeWidth = size / 4.5f;
		_droneArm.StrokeWidth = size / 9f;
		_droneRim.StrokeWidth = Math.Max(1.5f, size / 24);
		_rotorRing.StrokeWidth = Math.Max(1.5f, size / 16);
		if (_shadowSize != size)
		{
			_shadowSize = size;
			_shadowPaint.MaskFilter?.Dispose();
			_shadowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.16f);
		}

		canvas.Save();
		canvas.Translate(center);
		canvas.RotateDegrees((float)attitude.YawDegrees);

		if (beam)
		{
			canvas.Save();
			canvas.Scale(size);
			canvas.DrawPath(_beam, _beamPaint);
			canvas.Restore();
		}

		// What the tilt does to the view from above, within what the eye reads as a lean rather than a different shape.
		float narrowing = (float)Math.Cos(AngleMath.DegToRad(Math.Clamp(attitude.RollDegrees, -55, 55)));
		float shortening = (float)Math.Cos(AngleMath.DegToRad(Math.Clamp(attitude.PitchDegrees, -55, 55)));
		canvas.Scale(narrowing, shortening);

		// The light is fixed on the screen, so the shadow is turned back by the heading.
		double yaw = AngleMath.DegToRad(attitude.YawDegrees);
		float shadowX = size * 0.12f, shadowY = size * 0.16f;
		canvas.Save();
		canvas.Translate((float)(shadowX * Math.Cos(yaw) + shadowY * Math.Sin(yaw)) / narrowing, (float)(-shadowX * Math.Sin(yaw) + shadowY * Math.Cos(yaw)) / shortening);
		foreach ((int x, int y) in RotorSpin.Corners) canvas.DrawCircle(x * reach, -y * reach, rotorRadius * 0.95f, _shadowPaint);
		canvas.DrawRoundRect(-size * 0.27f, -size * 0.38f, size * 0.54f, size * 0.76f, size * 0.2f, size * 0.2f, _shadowPaint);
		canvas.Restore();

		foreach ((int x, int y) in RotorSpin.Corners)
		{
			var end = new SKPoint(x * reach, -y * reach);
			canvas.DrawLine(0, 0, end.X, end.Y, _droneOutline);
			canvas.DrawLine(0, 0, end.X, end.Y, _droneArm);
		}

		float bladeLength = rotorRadius * 0.92f, bladeWidth = size * 0.1f;
		for (int i = 0; i < RotorSpin.Count; i++)
		{
			(int x, int y) = RotorSpin.Corners[i];
			SKColor color = y > 0 ? FrontRotor : RearRotor;
			double raised = Math.Clamp((attitude.PitchDegrees * y - attitude.RollDegrees * x) * 4, -70, 70);
			float cx = x * reach, cy = -y * reach;
			int direction = x * y > 0 ? 1 : -1;
			double pace = Math.Clamp(rotors[i].RevolutionsPerSecond / RotorSpin.TopRevolutionsPerSecond, 0, 1.3);

			_rotorFill.Color = color.WithAlpha((byte)(24 + 70 * Math.Min(pace, 1)));
			canvas.DrawCircle(cx, cy, rotorRadius, _rotorFill);
			_rotorRing.Color = color.WithAlpha((byte)Math.Clamp(190 + raised, 90, 255));
			canvas.DrawCircle(cx, cy, rotorRadius, _rotorRing);

			canvas.Save();
			canvas.Translate(cx, cy);
			double angle = rotors[i].Revolutions * 360 * direction;
			double smear = rotors[i].RevolutionsPerSecond * 360 * Exposure;
			int ghosts = smear > 3 ? Ghosts : 1;
			for (int g = ghosts - 1; g >= 0; g--)
			{
				double behind = ghosts == 1 ? 0 : smear * g / (ghosts - 1);
				double fade = ghosts == 1 ? 1 : 1 - 0.8 * g / (ghosts - 1);
				_blade.Color = Blade.WithAlpha((byte)((235 - 110 * Math.Min(pace, 1)) * fade));
				canvas.Save();
				canvas.RotateDegrees((float)(angle - behind * direction));
				canvas.DrawRoundRect(-bladeLength, -bladeWidth / 2, bladeLength * 2, bladeWidth, bladeWidth / 2, bladeWidth / 2, _blade);
				canvas.Restore();
			}

			canvas.Restore();
			_droneFill.Color = Hub;
			canvas.DrawCircle(cx, cy, rotorRadius * 0.2f, _droneFill);
		}

		_droneFill.Color = Hub;
		var body = SKRect.Create(-size * 0.27f, -size * 0.38f, size * 0.54f, size * 0.76f);
		canvas.DrawRoundRect(body, size * 0.2f, size * 0.2f, _droneFill);
		canvas.DrawRoundRect(body, size * 0.2f, size * 0.2f, _droneRim);
		_droneFill.Color = SKColors.White;
		canvas.Save();
		canvas.Scale(size);
		canvas.DrawPath(_nose, _droneFill);
		canvas.Restore();
		canvas.Restore();
	}

	/// <summary>
	///     The same aircraft in perspective, for the Orientation3D widget: seen from `elevationDegrees` above the horizon with north up,
	///     lying as its roll, pitch and yaw say, so the tilt shows as real depth instead of a foreshortened plan. The parts (the arms, the
	///     rotors, the body) are drawn far to near. `scale` is how many pixels the aircraft's unit is - an arm is 0.62 long, a rotor 0.34 wide.
	/// </summary>
	public void DrawPerspective(SKCanvas canvas, SKPoint center, float scale, DroneAttitude attitude, ReadOnlySpan<RotorSpin.RotorPose> rotors, double elevationDegrees)
	{
		const double arm = 0.62, rotorRadius = 0.34, level = 0.06, halfWidth = 0.2, halfLength = 0.29, cornerRadius = 0.09;
		double elevation = AngleMath.DegToRad(elevationDegrees);
		(double sinE, double cosE) = (Math.Sin(elevation), Math.Cos(elevation));
		(double sinR, double cosR) = Math.SinCos(AngleMath.DegToRad(attitude.RollDegrees));
		(double sinP, double cosP) = Math.SinCos(AngleMath.DegToRad(attitude.PitchDegrees));
		(double sinY, double cosY) = Math.SinCos(AngleMath.DegToRad(attitude.YawDegrees));

		(double X, double Y, double Z) Place(double x, double y, double z)
		{
			double rx = x * cosR + z * sinR, rz = -x * sinR + z * cosR;
			double py = y * cosP - rz * sinP, pz = y * sinP + rz * cosP;
			return (rx * cosY + py * sinY, -rx * sinY + py * cosY, pz);
		}

		SKPoint Project(double x, double y, double z)
		{
			(double wx, double wy, double wz) = Place(x, y, z);
			return new SKPoint(center.X + (float)(wx * scale), center.Y - (float)((wz * cosE + wy * sinE) * scale));
		}

		double Nearness(double x, double y, double z)
		{
			(_, double wy, double wz) = Place(x, y, z);
			return -wy * cosE + wz * sinE;
		}

		SKPath Polygon(IEnumerable<SKPoint> points)
		{
			using var builder = new SKPathBuilder();
			bool first = true;
			foreach (SKPoint point in points)
			{
				if (first) builder.MoveTo(point);
				else builder.LineTo(point);
				first = false;
			}

			builder.Close();
			return builder.Detach();
		}

		float line = scale / 11f;
		_droneOutline.StrokeWidth = line * 1.6f;
		_droneArm.StrokeWidth = line;
		_droneRim.StrokeWidth = Math.Max(1.5f, scale / 38);
		_rotorRing.StrokeWidth = Math.Max(1.5f, scale / 26);

		foreach ((int x, int y) in RotorSpin.Corners)
		{
			SKPoint end = Project(x * arm, y * arm, level), middle = Project(0, 0, level);
			canvas.DrawLine(middle, end, _droneOutline);
			canvas.DrawLine(middle, end, _droneArm);
		}

		var parts = new List<(double Nearness, Action Draw)>();
		for (int i = 0; i < RotorSpin.Count; i++)
		{
			(int x, int y) = RotorSpin.Corners[i];
			RotorSpin.RotorPose pose = rotors[i];
			double cx = x * arm, cy = y * arm;
			parts.Add((Nearness(cx, cy, level), () =>
			{
				SKColor color = y > 0 ? FrontRotor : RearRotor;
				double raised = Math.Clamp((attitude.PitchDegrees * y - attitude.RollDegrees * x) * 4, -70, 70);
				double pace = Math.Clamp(pose.RevolutionsPerSecond / RotorSpin.TopRevolutionsPerSecond, 0, 1.3);
				using SKPath disc = Polygon(Enumerable.Range(0, 28).Select(k =>
					Project(cx + rotorRadius * Math.Cos(k * 2 * Math.PI / 28), cy + rotorRadius * Math.Sin(k * 2 * Math.PI / 28), level)));
				_rotorFill.Color = color.WithAlpha((byte)(24 + 70 * Math.Min(pace, 1)));
				canvas.DrawPath(disc, _rotorFill);

				int direction = x * y > 0 ? 1 : -1;
				double angle = pose.Revolutions * 360 * direction, smear = pose.RevolutionsPerSecond * 360 * Exposure;
				int ghosts = smear > 3 ? Ghosts : 1;
				_bladeLine.StrokeWidth = Math.Max(2f, scale / 26);
				for (int g = ghosts - 1; g >= 0; g--)
				{
					double behind = ghosts == 1 ? 0 : smear * g / (ghosts - 1), fade = ghosts == 1 ? 1 : 1 - 0.8 * g / (ghosts - 1);
					double a = AngleMath.DegToRad(angle - behind * direction);
					_bladeLine.Color = Blade.WithAlpha((byte)((235 - 110 * Math.Min(pace, 1)) * fade));
					double bx = rotorRadius * 0.92 * Math.Cos(a), by = rotorRadius * 0.92 * Math.Sin(a);
					canvas.DrawLine(Project(cx - bx, cy - by, level), Project(cx + bx, cy + by, level), _bladeLine);
				}

				_rotorRing.Color = color.WithAlpha((byte)Math.Clamp(190 + raised, 90, 255));
				canvas.DrawPath(disc, _rotorRing);
				_droneFill.Color = Hub;
				canvas.DrawCircle(Project(cx, cy, level), Math.Max(2f, scale / 22), _droneFill);
			}));
		}

		parts.Add((Nearness(0, 0, level) + 0.01, () =>
		{
			var outline = new List<SKPoint>();
			foreach ((int sx, int sy, double start) in new[] { (1, 1, 0.0), (-1, 1, 90.0), (-1, -1, 180.0), (1, -1, 270.0) })
			{
				for (int k = 0; k <= 6; k++)
				{
					double a = AngleMath.DegToRad(start + k * 15);
					outline.Add(Project(sx * (halfWidth - cornerRadius) + cornerRadius * Math.Cos(a), sy * (halfLength - cornerRadius) + cornerRadius * Math.Sin(a), level + 0.02));
				}
			}

			using SKPath body = Polygon(outline);
			_droneFill.Color = Hub;
			canvas.DrawPath(body, _droneFill);
			canvas.DrawPath(body, _droneRim);
			using SKPath nose = Polygon([Project(0, 0.23, level + 0.03), Project(0.12, -0.06, level + 0.03), Project(-0.12, -0.06, level + 0.03)]);
			_droneFill.Color = SKColors.White;
			canvas.DrawPath(nose, _droneFill);
		}));

		foreach ((double _, Action draw) in parts.OrderBy(part => part.Nearness)) draw();
	}

	public void Dispose()
	{
		_nose.Dispose();
		_beam.Dispose();
		_beamShader.Dispose();
		_shadowPaint.MaskFilter?.Dispose();
		foreach (SKPaint paint in new[] { _droneOutline, _droneArm, _droneFill, _droneRim, _rotorFill, _rotorRing, _blade, _bladeLine, _shadowPaint, _beamPaint })
			paint.Dispose();
	}
}
