using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     Roll, pitch and the G-meter's accelerations from the camera's own orientation (ICameraFormat.OrientationGravity)
///     instead of the accelerometer alone. The accelerometer feels gravity and the camera's own acceleration together,
///     so turning it by hand reads as a force; the orientation says where gravity is, and what the accelerometer feels
///     beyond it is the camera's acceleration. The orientation needs no GPS, so nothing is taken out of the tilt with it.
///     Applied after TelemetryProcessor.Process, and to a cached summary's frames, which are derived from the accelerometer.
/// </summary>
public static class OrientationTilt
{
	/// <summary>
	///     What Apply will really use for these frames: the requested source, unless the camera reports no orientation (or
	///     doesn't know its axes), where it is the accelerometer.
	/// </summary>
	public static OrientationSource Effective(IReadOnlyList<TelemetryFrame> frames, ICameraFormat? camera, OrientationSource requested)
	{
		return requested == OrientationSource.Quaternion && camera is not null && frames.Count > 0 && camera.Gravity(frames[0]) is not null &&
		       frames.Any(f => camera.OrientationGravity(f) is not null)
			? OrientationSource.Quaternion
			: OrientationSource.Accelerometer;
	}

	/// <summary>
	///     The frames with roll, pitch and the G-meter's accelerations from the orientation - unchanged where Effective
	///     says the accelerometer. A frame without an orientation keeps the accelerometer's tilt (without GPS compensation)
	///     and no dynamic acceleration.
	/// </summary>
	public static IReadOnlyList<DerivedFrame> Apply(IReadOnlyList<DerivedFrame> frames, ICameraFormat? camera, OrientationSource source)
	{
		if (source != OrientationSource.Quaternion || camera is null || frames.Count == 0) return frames;
		if (Effective([.. frames.Select(f => f.Raw)], camera, source) != OrientationSource.Quaternion) return frames;

		var raw = new List<TelemetryFrame>(frames.Count);
		var measured = new Direction[frames.Count];
		var oriented = new Direction[frames.Count];
		for (int i = 0; i < frames.Count; i++)
		{
			TelemetryFrame frame = frames[i].Raw;
			raw.Add(frame);
			measured[i] = camera.Gravity(frame) ?? default;
			Direction? orientation = camera.OrientationGravity(frame);
			oriented[i] = orientation ?? measured[i];
		}

		double[] none = new double[frames.Count];
		(double[] roll, double[] pitch) = CameraTilt.Compute(raw, oriented, none, none);

		var result = new List<DerivedFrame>(frames.Count);
		for (int i = 0; i < frames.Count; i++)
		{
			result.Add(frames[i] with
			{
				RollDegrees = roll[i],
				PitchDegrees = pitch[i],
				LateralAccelG = measured[i].X - oriented[i].X,
				LongitudinalAccelG = -(measured[i].Z - oriented[i].Z)
			});
		}

		return result;
	}
}
