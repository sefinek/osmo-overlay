using System.Text.Json;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

/// <summary>
///     The test recording's telemetry (SyntheticRecording), one sample per video frame like DJI's, in SyntheticCamera's axes
///     (AccelX/Y/Z is the felt gravity: x right, y down, z forward). The same on every computer (a fixed seed). A ride along
///     field roads (Route), up to 150 km/h, with what real ones throw at the processing: no fix and no GPS time at
///     the start ((0, 0) like a DJI before its first fix), a fix wandering while standing still, the receiver's 10 Hz steps,
///     potholes (G jolts), a GPS loss at full speed with the position held, a multipath jump with a speed spike - then a
///     40 s stop of the camera while the ride goes on, and the third file braking hard into a sharp corner leaned through, a
///     dip in the ground, a stretch without the receiver's speed and a white balance jump.
/// </summary>
internal static class SyntheticTelemetry
{
	public const string CameraModel = "OsmoOverlay test recording";

	// When the ride is: a June morning (UTC).
	private static readonly DateTime StartUtc = new(2026, 6, 14, 8, 0, 0, DateTimeKind.Utc);
	private const double MetersPerDegreeLatitude = 111_320;
	private const double G = 9.80665;
	// The receiver's fix rate: its position holds between updates (TelemetryProcessor interpolates).
	private const double FixHz = 10;
	// The camera is off this long between the second and third files - the ride goes on meanwhile.
	private const double StopSeconds = 40;

	// The field roads, in order; the ride covers the first corner. Corners are rounded off to CornerRadius.
	private static readonly (double Latitude, double Longitude)[] Waypoints =
	[
		(53.250543893934946, 16.439558373751506),
		(53.248489686279584, 16.411663402469195),
		(53.23775484727832, 16.43586765447415),
		(53.23225797839721, 16.389347425443344),
		(53.20784746758602, 16.409002650685185)
	];

	private const double CornerRadius = 25;
	private const double PathStep = 0.5;

	// Speeds in m/s: through the first file's bends, the second's top speed and what it brakes to, the third's into and
	// through the corner and out of it.
	private const double BendSpeed = 50 / 3.6;
	private const double TopSpeed = 150 / 3.6;
	private const double BrakedSpeed = 90 / 3.6;
	private const double ApproachSpeed = 120 / 3.6;
	private const double CornerSpeed = 45 / 3.6;
	private const double ExitSpeed = 70 / 3.6;

	// The third file (seconds into it): at the approach speed, then braking into the corner, through it, and out.
	private const double BrakeFrom = 0.5;
	private const double BrakeTo = 3.5;

	private static readonly Path Route = Path.Through(Waypoints, CornerRadius, PathStep, Bends);

	/// <summary>Each file's telemetry and the moment its first frame was recorded, seconds after the ride started.</summary>
	public static IReadOnlyList<TelemetryFrame> ForFile(int file, int frames, double fps)
	{
		double fileStart = FileStartSeconds(file);
		double fileEnd = fileStart + frames / fps;
		var noise = new Random(1000 + file);
		List<TelemetryFrame> result = new(frames);
		(double East, double North) wander = (0, 0);
		TelemetryFrame? lastFix = null;
		for (int i = 0; i < frames; i++)
		{
			double t = i / fps;
			double ride = fileStart + t;
			// The receiver reports on its own clock: every frame between two fixes carries the older one.
			double fixRide = fileStart + Math.Floor(t * FixHz) / FixHz;
			wander = (wander.East * 0.97 + (noise.NextDouble() - 0.5) * 0.6, wander.North * 0.97 + (noise.NextDouble() - 0.5) * 0.6);
			Path.Point at = Route.At(Distance(fixRide));
			(double east, double north) = (at.East, at.North);
			double altitude = Altitude(at.Distance) + Math.Sin(fixRide * 1.7) * 0.8;
			double? speedMs = Speed(fixRide);
			bool hasFix = true;

			if (ride < 1.5)
				hasFix = false;
			else if (ride < 3)
			{
				east += wander.East * 4;
				north += wander.North * 4;
				speedMs = 0.2 + Math.Abs(wander.East) * 0.5;
			}
			else if (ride is >= 15 and < 17)
				hasFix = false;
			else if (ride is >= 17.2 and < 17.7)
			{
				// Bounced off something beside the road: 60 m to the right of it for half a second, and one wild speed.
				(double sin, double cos) = Math.SinCos(at.HeadingDegrees * Math.PI / 180);
				east += cos * 60;
				north -= sin * 60;
				if (ride < 17.3) speedMs = 75;
			}
			else if (file == 2 && t >= 8.5)
				speedMs = null;

			(double forwardG, double lateralG) = Motion(ride, fileStart, fileEnd);
			double pitch = Math.Atan(Gradient(at.Distance));
			// A rider leans into a corner almost as far as the cornering force asks, so the camera, leaning with the bike, feels
			// only a fifth of it sideways - as measured on real rides (CameraTilt); the lean itself it doesn't feel.
			double accelX = -0.2 * lateralG + Vibration(noise);
			double accelY = Math.Cos(pitch) + Vibration(noise) + Jolt(ride, out double sideways);
			double accelZ = -Math.Sin(pitch) - forwardG + Vibration(noise);
			accelX += sideways;

			DateTime? gpsTime = ride < 1.5 ? null : StartUtc.AddSeconds(Math.Floor(fixRide));
			TelemetryFrame frame;
			if (hasFix)
			{
				frame = new TelemetryFrame(i, t, Latitude(north), Longitude(east), altitude, gpsTime, accelX, accelY, accelZ, speedMs, Iso(ride), Shutter(ride),
					ColorTemperature(file));
				lastFix = frame;
			}
			else
			{
				// No fix: the last one held (a DJI's GpsForwardFill), or (0, 0) before the first.
				frame = new TelemetryFrame(i, t, lastFix?.Latitude ?? 0, lastFix?.Longitude ?? 0, lastFix?.AltitudeMeters ?? 0, gpsTime,
					accelX, accelY, accelZ, null, Iso(ride), Shutter(ride), ColorTemperature(file), HasGpsFix: false);
			}

			result.Add(frame);
		}

		return result;
	}

	/// <summary>Seconds after the ride started that a file's first frame was recorded: the first two back to back, the third after the stop.</summary>
	public static double FileStartSeconds(int file)
	{
		return file switch
		{
			0 => 0,
			1 => SyntheticRecording.FileSeconds(0),
			_ => SecondFileEnd + StopSeconds
		};
	}

	/// <summary>The container's creation_time for a file - the camera's clock, as its own recordings carry.</summary>
	public static DateTime CreationTimeUtc(int file)
	{
		return StartUtc.AddSeconds(Math.Round(FileStartSeconds(file)));
	}

	public static string SidecarPath(string videoPath)
	{
		return videoPath + ".telemetry.json";
	}

	public static void Write(string videoPath, IReadOnlyList<TelemetryFrame> frames)
	{
		using FileStream stream = File.Create(SidecarPath(videoPath));
		JsonSerializer.Serialize(stream, frames);
	}

	public static List<TelemetryFrame> Read(string videoPath)
	{
		using FileStream stream = File.OpenRead(SidecarPath(videoPath));
		return JsonSerializer.Deserialize<List<TelemetryFrame>>(stream) ?? [];
	}

	private static double SecondFileEnd => SyntheticRecording.FileSeconds(0) + SyntheticRecording.FileSeconds(1);

	private static double Latitude(double north)
	{
		return Waypoints[0].Latitude + north / MetersPerDegreeLatitude;
	}

	private static double Longitude(double east)
	{
		return Waypoints[0].Longitude + east / (MetersPerDegreeLatitude * Math.Cos(Waypoints[0].Latitude * Math.PI / 180));
	}

	/// <summary>
	///     m/s at a moment of the ride: standing until 3 s, 50 km/h by 6 s through the bends; flat out to 150 km/h within 5 s,
	///     held through a sweeping bend, braking hard (0.85 g) to 90 at the second file's end; the third file at 120 km/h,
	///     braking hard into the corner at 45, then out of it to 70.
	/// </summary>
	private static double Speed(double ride)
	{
		double second = FileStartSeconds(1), third = FileStartSeconds(2);
		if (ride < 3) return 0;
		if (ride < 6) return BendSpeed * Smooth((ride - 3) / 3);
		if (ride < second) return BendSpeed + 0.8 * Math.Sin((ride - 6) * 1.6);
		if (ride < SecondFileEnd - 2)
			return BendSpeed + (TopSpeed - BendSpeed) * Smooth((ride - second) / 5) - 0.5 * (1 - Math.Cos((ride - second) * 1.3));
		if (ride < SecondFileEnd)
			return Speed(SecondFileEnd - 2 - 1e-9) + (BrakedSpeed - Speed(SecondFileEnd - 2 - 1e-9)) * Smooth((ride - (SecondFileEnd - 2)) / 2);
		if (ride < third) return ApproachSpeed;

		double t = ride - third;
		if (t < BrakeFrom) return ApproachSpeed;
		if (t < BrakeTo) return ApproachSpeed + (CornerSpeed - ApproachSpeed) * Smooth((t - BrakeFrom) / (BrakeTo - BrakeFrom));
		double cornerEnd = BrakeTo + Route.CornerLength(0) / CornerSpeed;
		return t < cornerEnd ? CornerSpeed : CornerSpeed + (ExitSpeed - CornerSpeed) * Smooth((t - cornerEnd) / 1.5);
	}

	private const double DistanceStep = 0.005;
	private static readonly Lazy<(double[] First, double[] Third)> Distances = new(IntegrateDistances);

	/// <summary>
	///     Meters along the route: the speed integrated from the start, and for the third file from where it reaches the first
	///     corner's arc at BrakeTo - so the camera, off for the stop, misses the ride in between.
	/// </summary>
	private static double Distance(double ride)
	{
		double third = FileStartSeconds(2);
		(double[] table, double from) = ride >= third ? (Distances.Value.Third, third) : (Distances.Value.First, 0);
		double index = Math.Clamp((ride - from) / DistanceStep, 0, table.Length - 1.001);
		int i = (int)index;
		return table[i] + (table[i + 1] - table[i]) * (index - i);
	}

	/// <summary>Distance every DistanceStep seconds: through the first two files from 0, through the third from the corner's start back and forth.</summary>
	private static (double[] First, double[] Third) IntegrateDistances()
	{
		double third = FileStartSeconds(2);
		double[] first = Integrate(0, third);
		double[] last = Integrate(third, third + SyntheticRecording.FileSeconds(2) + 1);
		double anchor = last[(int)Math.Round(BrakeTo / DistanceStep)];
		return (first, [.. last.Select(d => d - anchor + Route.CornerStart(0))]);

		static double[] Integrate(double from, double to)
		{
			double[] table = new double[(int)Math.Ceiling((to - from) / DistanceStep) + 2];
			for (int i = 1; i < table.Length; i++)
				table[i] = table[i - 1] + Speed(from + (i - 0.5) * DistanceStep) * DistanceStep;
			return table;
		}
	}

	/// <summary>Rolling fields - slopes up to about 5% - and a dip at the corner: 8% down and up again.</summary>
	private static double Altitude(double distance)
	{
		double dip = distance - (Route.CornerStart(0) + Route.CornerLength(0) / 2);
		return 128 + 5 * Math.Sin(distance / 110) - 6 * Math.Exp(-dip * dip / (2 * 45 * 45));
	}

	/// <summary>
	///     Meters to the right of the straight runs at a distance along them: an S-bend in the first file at about 50 km/h
	///     (curves of ~40 m, leaned through both ways) and a sweeping bend at 150 km/h in the second (~500 m, as much as that
	///     speed allows) - each easing in and out, so the heading never jumps.
	/// </summary>
	private static double Bends(double distance)
	{
		return Bend(distance, 22, 70, 2, true) + Bend(distance, 230, 170, 3, false);

		static double Bend(double distance, double from, double length, double amplitude, bool both)
		{
			double u = (distance - from) / length;
			if (u is <= 0 or >= 1) return 0;
			double ease = Math.Pow(Math.Sin(Math.PI * u), 2);
			return amplitude * ease * (both ? Math.Sin(2 * Math.PI * u) * 2 : 1);
		}
	}

	private static double Gradient(double distance)
	{
		return (Altitude(distance + 1) - Altitude(distance - 1)) / 2;
	}

	/// <summary>The vehicle's own forward and sideways acceleration in g - differenced within the file, never across the stop before the third one.</summary>
	private static (double ForwardG, double LateralG) Motion(double ride, double fileStart, double fileEnd)
	{
		const double dt = 0.05;
		double before = Math.Max(fileStart, ride - dt), after = Math.Min(fileEnd, ride + dt);
		double forward = (Speed(after) - Speed(before)) / (after - before) / G;
		double turn = AngleMath.DegToRad(AngleMath.NormalizeDegrees(Route.At(Distance(after)).HeadingDegrees - Route.At(Distance(before)).HeadingDegrees + 180) - 180);
		return (forward, Speed(ride) * turn / (after - before) / G);
	}

	private static double Vibration(Random noise)
	{
		return (noise.NextDouble() - 0.5) * 0.06;
	}

	/// <summary>Potholes and a kerb: a few frames of a hard knock, up (y) or sideways.</summary>
	private static double Jolt(double ride, out double sideways)
	{
		double third = FileStartSeconds(2);
		sideways = ride is >= 15.2 and < 15.25 ? 1.4 : 0;
		return ride switch
		{
			>= 6 and < 6.05 => 2.8,
			_ when ride - third is >= 2 and < 2.04 => 2.0,
			_ => 0
		};
	}

	private static float Iso(double ride)
	{
		return ride switch
		{
			< 2.5 => 100,
			< 2.6 => 50,
			< 7 => 200,
			_ when ride < FileStartSeconds(2) => 400,
			_ => 1600
		};
	}

	private static double Shutter(double ride)
	{
		return ride < FileStartSeconds(2) ? 1 / 2000.0 : 1 / 240.0;
	}

	private static int ColorTemperature(int file)
	{
		return file < 2 ? 5600 : 3200;
	}

	private static double Smooth(double x)
	{
		x = Math.Clamp(x, 0, 1);
		return x * x * (3 - 2 * x);
	}

	/// <summary>
	///     The route as straight runs joined by arcs of a set radius and moved sideways into bends, sampled every Step meters:
	///     east/north of the first waypoint and the heading at a distance along it. Corner i is the arc at the (i + 1)-th waypoint.
	/// </summary>
	private sealed class Path
	{
		private readonly double[] _east, _north, _distance;
		private readonly List<(int Start, int End)> _corners;

		private Path(double[] east, double[] north, List<(int Start, int End)> corners)
		{
			_east = east;
			_north = north;
			_corners = corners;
			_distance = new double[east.Length];
			for (int i = 1; i < east.Length; i++)
				_distance[i] = _distance[i - 1] + Math.Sqrt(Math.Pow(east[i] - east[i - 1], 2) + Math.Pow(north[i] - north[i - 1], 2));
		}

		public readonly record struct Point(double East, double North, double HeadingDegrees, double Distance);

		public double CornerStart(int corner)
		{
			return _distance[_corners[corner].Start];
		}

		public double CornerLength(int corner)
		{
			return _distance[_corners[corner].End] - _distance[_corners[corner].Start];
		}

		/// <summary>The point at `distance` meters along the road as driven (bends included) - held at either end.</summary>
		public Point At(double distance)
		{
			int i = Array.BinarySearch(_distance, distance);
			i = Math.Clamp(i >= 0 ? i : ~i - 1, 0, _distance.Length - 2);
			double span = _distance[i + 1] - _distance[i];
			double share = span > 0 ? Math.Clamp((distance - _distance[i]) / span, 0, 1) : 0;
			double east = _east[i] + (_east[i + 1] - _east[i]) * share;
			double north = _north[i] + (_north[i + 1] - _north[i]) * share;
			double heading = AngleMath.NormalizeDegrees(AngleMath.RadToDeg(Math.Atan2(_east[i + 1] - _east[i], _north[i + 1] - _north[i])));
			return new Point(east, north, heading, distance);
		}

		/// <summary>
		///     Straight runs between the waypoints joined by arcs of `radius`, sampled every `step` meters, then moved sideways by
		///     `offset` (meters to the right at that far along the runs and arcs) - the bends a field road has between them.
		/// </summary>
		public static Path Through((double Latitude, double Longitude)[] waypoints, double radius, double step, Func<double, double> offset)
		{
			double cos = Math.Cos(waypoints[0].Latitude * Math.PI / 180);
			var points = waypoints.Select(w => ((w.Longitude - waypoints[0].Longitude) * MetersPerDegreeLatitude * cos,
				(w.Latitude - waypoints[0].Latitude) * MetersPerDegreeLatitude)).ToList();
			List<(double East, double North)> line = [points[0]];
			List<(int Start, int End)> corners = [];

			void Run((double East, double North) to)
			{
				(double East, double North) from = line[^1];
				double length = Math.Sqrt(Math.Pow(to.East - from.East, 2) + Math.Pow(to.North - from.North, 2));
				for (double d = step; d < length; d += step)
					line.Add((from.East + (to.East - from.East) * d / length, from.North + (to.North - from.North) * d / length));
				line.Add(to);
			}

			for (int i = 1; i < points.Count - 1; i++)
			{
				(double East, double North) a = points[i - 1], b = points[i], c = points[i + 1];
				double inHeading = Math.Atan2(b.East - a.East, b.North - a.North);
				double outHeading = Math.Atan2(c.East - b.East, c.North - b.North);
				double turn = Math.IEEERemainder(outHeading - inHeading, 2 * Math.PI);
				double tangent = radius * Math.Tan(Math.Abs(turn) / 2);
				Run((b.East - Math.Sin(inHeading) * tangent, b.North - Math.Cos(inHeading) * tangent));

				// The arc's center is to the side the road turns to, `radius` off the run.
				double side = Math.Sign(turn);
				(double East, double North) center = (line[^1].East + Math.Cos(inHeading) * radius * side, line[^1].North - Math.Sin(inHeading) * radius * side);
				double arcLength = radius * Math.Abs(turn);
				int start = line.Count - 1;
				for (double d = step; d <= arcLength; d += step)
				{
					double heading = inHeading + turn * d / arcLength;
					line.Add((center.East - Math.Cos(heading) * radius * side, center.North + Math.Sin(heading) * radius * side));
				}

				corners.Add((start, line.Count - 1));
			}

			Run(points[^1]);

			// Every sample `step` apart along the runs and arcs, so the offset's argument is how far along they are.
			double[] east = new double[line.Count], north = new double[line.Count];
			for (int i = 0; i < line.Count; i++)
			{
				(double East, double North) before = line[Math.Max(0, i - 1)], after = line[Math.Min(line.Count - 1, i + 1)];
				double heading = Math.Atan2(after.East - before.East, after.North - before.North);
				double shift = offset(i * step);
				east[i] = line[i].East + Math.Cos(heading) * shift;
				north[i] = line[i].North - Math.Sin(heading) * shift;
			}

			return new Path(east, north, corners);
		}
	}
}
