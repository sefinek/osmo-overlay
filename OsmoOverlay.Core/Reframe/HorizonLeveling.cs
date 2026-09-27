using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Reframe;

/// <summary>
///     Keeps a 360 recording's picture upright: per telemetry frame, the rotation (Rotation.Leveling) that brings the
///     camera's measured "down" back to the picture's down. An accelerometer reads gravity plus the camera's own
///     acceleration, so it's averaged over a second around each frame - the tilt it follows, not the bumps. Where the
///     accelerometer's axes point in the lens space is the camera format's (ICameraFormat.Gravity).
/// </summary>
public sealed class HorizonLeveling
{
	internal const double WindowSeconds = 1.0;

	private readonly double[] _times;
	private readonly Rotation[] _rotations;

	private HorizonLeveling(double[] times, Rotation[] rotations)
	{
		_times = times;
		_rotations = rotations;
	}

	/// <summary>Null when the camera can't say where down is, or there are no frames.</summary>
	public static HorizonLeveling? For(IReadOnlyList<TelemetryFrame> frames, ICameraFormat camera)
	{
		if (frames.Count == 0 || camera.Gravity(frames[0]) is null) return null;

		// Prefix sums of down per frame, so every frame's window average is one subtraction.
		var sums = new Direction[frames.Count + 1];
		for (int i = 0; i < frames.Count; i++)
		{
			Direction d = camera.Gravity(frames[i]) ?? default;
			Direction s = sums[i];
			sums[i + 1] = new Direction(s.X + d.X, s.Y + d.Y, s.Z + d.Z);
		}

		double[] times = new double[frames.Count];
		var rotations = new Rotation[frames.Count];
		int first = 0, last = 0;
		for (int i = 0; i < frames.Count; i++)
		{
			double t = frames[i].SampleTimeSeconds;
			while (frames[first].SampleTimeSeconds < t - WindowSeconds / 2) first++;
			while (last + 1 < frames.Count && frames[last + 1].SampleTimeSeconds <= t + WindowSeconds / 2) last++;

			Direction from = sums[first], to = sums[last + 1];
			times[i] = t;
			rotations[i] = Rotation.Leveling(new Direction(to.X - from.X, to.Y - from.Y, to.Z - from.Z));
		}

		return new HorizonLeveling(times, rotations);
	}

	/// <summary>The leveling for the frame nearest to a moment of the recording.</summary>
	public Rotation At(double recordingSeconds)
	{
		int index = Array.BinarySearch(_times, recordingSeconds);
		if (index >= 0) return _rotations[index];

		index = ~index;
		if (index == 0) return _rotations[0];
		if (index == _times.Length) return _rotations[^1];
		return recordingSeconds - _times[index - 1] <= _times[index] - recordingSeconds ? _rotations[index - 1] : _rotations[index];
	}
}
