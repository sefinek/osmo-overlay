namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     Stand-in telemetry for a recording that has none, so the preview can still play it: one empty sample a second,
///     no GPS fix, no accelerometer. Nothing is drawn from it - the preview shows no overlay for such a recording.
/// </summary>
public static class PlainRecordingFrames
{
	public static List<TelemetryFrame> Create(double durationSeconds)
	{
		int count = (int)Math.Ceiling(Math.Max(durationSeconds, 0)) + 1;
		var frames = new List<TelemetryFrame>(count);
		for (int i = 0; i < count; i++)
			frames.Add(new TelemetryFrame(i, Math.Min(i, durationSeconds), 0, 0, 0, null, 0, 0, 0, HasGpsFix: false));
		return frames;
	}
}
