namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     How far each of the aircraft's four rotors has turned at a time on the track, and how fast it turns then. As on the real
///     thing the rotors differ: the side being raised turns faster (a nose up speeds the front pair, a bank to the right the left
///     pair), a climb speeds all four and a descent slows them, and the faster it flies the faster they go (relative to the
///     track's own pace, like TrackCamera). The rates are an illustration - slow enough to be read at 30 fps - not the motors'
///     true revolutions. The turn is the running sum of the rate over the frames, so it is a function of the time asked for only:
///     the picture of a frame never depends on the frames drawn before, and a change of rate speeds a rotor up instead of
///     making it jump.
/// </summary>
public static class RotorSpin
{
	public const double IdleRevolutionsPerSecond = 2;
	public const double TopRevolutionsPerSecond = 4.5;
	public const int Count = 4;

	// A rotor's share of the rate gained or lost per degree of lift (pitch and roll, in degrees), and the most it can be.
	private const double TiltPerDegree = 0.012;
	private const double MaxTiltShare = 0.4;
	// A climb of FullClimbMs metres a second adds (a descent takes away) this share to all four.
	private const double ClimbShare = 0.25;
	private const double FullClimbMs = 3;

	/// <summary>The rotors' corners seen from above, front first: x to the right, y to the front (the order of RotorPose).</summary>
	public static readonly (int X, int Y)[] Corners = [(-1, 1), (1, 1), (-1, -1), (1, -1)];

	/// <param name="Revolutions">How many times it has turned since the start of the track.</param>
	/// <param name="RevolutionsPerSecond">How fast it turns now.</param>
	public readonly record struct RotorPose(double Revolutions, double RevolutionsPerSecond);

	/// <summary>The share of the track's pace the speed is, 0 to 1.</summary>
	public static double Ratio(double speedKmh, double referenceKmh)
	{
		return double.IsFinite(speedKmh) ? Math.Clamp(speedKmh / Math.Max(referenceKmh, 1), 0, 1) : 0;
	}

	/// <summary>The rate of all four at a share of the pace, before the tilt and the climb.</summary>
	public static double RevolutionsPerSecond(double ratio)
	{
		return IdleRevolutionsPerSecond + (TopRevolutionsPerSecond - IdleRevolutionsPerSecond) * ratio;
	}

	/// <summary>The rate of one rotor (an index into Corners) in a frame.</summary>
	public static double RateOf(DerivedFrame frame, int rotor, double referenceKmh)
	{
		double rate = RevolutionsPerSecond(Ratio(frame.SpeedKmh, referenceKmh));
		if (frame.Raw.DroneAttitude is { } attitude)
		{
			(int x, int y) = Corners[rotor];
			double lift = attitude.PitchDegrees * y - attitude.RollDegrees * x;
			rate *= 1 + Math.Clamp(lift * TiltPerDegree, -MaxTiltShare, MaxTiltShare);
		}

		if (frame.Raw.VerticalSpeedMs is { } climb && double.IsFinite(climb))
			rate *= 1 + ClimbShare * Math.Clamp(climb / FullClimbMs, -1, 1);
		return rate;
	}

	/// <summary>The revolutions made by each rotor at each frame, from the start of the track: [frame, rotor].</summary>
	public static double[,] Phases(IReadOnlyList<DerivedFrame> frames, double referenceKmh)
	{
		double[,] phases = new double[frames.Count, Count];
		for (int i = 1; i < frames.Count; i++)
		{
			double seconds = Math.Max(0, frames[i].Raw.SampleTimeSeconds - frames[i - 1].Raw.SampleTimeSeconds);
			for (int rotor = 0; rotor < Count; rotor++)
				phases[i, rotor] = phases[i - 1, rotor] + RateOf(frames[i - 1], rotor, referenceKmh) * seconds;
		}

		return phases;
	}

	/// <summary>Each rotor's turn at a time (carried between the two frames around it) and the rate there, into `poses`.</summary>
	public static void At(IReadOnlyList<DerivedFrame> frames, double[,] phases, double seconds, double referenceKmh, Span<RotorPose> poses)
	{
		poses.Clear();
		if (frames.Count == 0 || phases.GetLength(0) != frames.Count || poses.Length < Count) return;

		(int index, double fraction, _) = TrackCamera.Locate(frames, seconds);
		int next = Math.Min(index + 1, frames.Count - 1);
		for (int rotor = 0; rotor < Count; rotor++)
		{
			double rate = RateOf(frames[index], rotor, referenceKmh);
			double nextRate = RateOf(frames[next], rotor, referenceKmh);
			poses[rotor] = new RotorPose(phases[index, rotor] + (phases[next, rotor] - phases[index, rotor]) * fraction, rate + (nextRate - rate) * fraction);
		}
	}
}
