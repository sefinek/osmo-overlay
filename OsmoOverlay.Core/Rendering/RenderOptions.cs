using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Core;

public sealed record RenderOptions(
	IReadOnlyList<string> InputPaths,
	string OutputPath,
	int? FrameLimit = null,
	string? Encoder = null,
	IReadOnlyList<TelemetryFrame>? TelemetryFrames = null,
	bool Overwrite = true,
	IReadOnlyList<OverlayElement>? Layout = null,
	bool? ShowWatermark = null,
	bool? SmoothGpsMotion = null,
	string? CameraModel = null,
	bool GreenScreen = false,
	// Seconds on the combined timeline of all inputs, rounded to the nearest frame; null = from the
	// start / to the end. CutOuts are removed from inside that range (see RenderPlan.Resolve), and
	// FrameLimit still caps the frame count on top of both.
	double? RangeStartSeconds = null,
	double? RangeEndSeconds = null,
	IReadOnlyList<TimeRange>? CutOuts = null,
	// Where the flat picture of a 360 recording looks - the default (leveled, straight ahead) when null; ignored for a flat video.
	ReframeView? Reframe = null,
	// A track from another device put onto the telemetry the render extracts (ignored with TelemetryFrames, which the caller already has the track in).
	ExternalTrackSource? ExternalTrack = null,
	// A render without the sound track; null = Settings' choice.
	bool? NoAudio = null,
	// The sound's level over the recording (VolumeLine); null or empty = the recording's own.
	IReadOnlyList<VolumePoint>? Volume = null,
	// Music over the sound, on the output's timeline (MusicTrack); null or empty = none.
	IReadOnlyList<MusicClip>? Music = null)
{
	public static string DefaultOutputPath(IReadOnlyList<string> inputPaths, string? outputFolder = null)
	{
		string inputPath = inputPaths[0];
		string dir = !string.IsNullOrWhiteSpace(outputFolder) && Directory.Exists(outputFolder) ? outputFolder : Path.GetDirectoryName(inputPath) ?? ".";
		string name = Path.GetFileNameWithoutExtension(inputPath);
		return Path.Combine(dir, $"{name}_overlay.mp4");
	}

	/// <summary>
	///     Derives the green-screen sibling of an already-chosen normal output path (same directory,
	///     "_greenscreen" instead of whatever suffix the normal path used) rather than starting fresh
	///     from the input paths, so it still respects a location the user picked via "..." for the
	///     normal render instead of silently writing somewhere else.
	/// </summary>
	public static string GreenScreenOutputPath(string outputPath)
	{
		string dir = Path.GetDirectoryName(outputPath) ?? ".";
		string name = Path.GetFileNameWithoutExtension(outputPath);
		string ext = Path.GetExtension(outputPath);
		if (name.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
			name = name[..^"_overlay".Length];
		return Path.Combine(dir, $"{name}_greenscreen{ext}");
	}

	/// <summary>
	///     The output path of a render of just a part of the recording: the normal one with the part's times in the name
	///     ("clip_01m30s-02m10s.mp4"), so it can't overwrite the render of the whole recording.
	/// </summary>
	public static string RangeOutputPath(string outputPath, double startSeconds, double endSeconds)
	{
		string dir = Path.GetDirectoryName(outputPath) ?? ".";
		string name = Path.GetFileNameWithoutExtension(outputPath);
		string ext = Path.GetExtension(outputPath);
		if (name.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
			name = name[..^"_overlay".Length];
		return Path.Combine(dir, $"{name}_{FileTime(startSeconds)}-{FileTime(endSeconds)}{ext}");
	}

	private static string FileTime(double seconds)
	{
		var time = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds)));
		return time.TotalHours >= 1 ? $"{(int)time.TotalHours}h{time.Minutes:00}m{time.Seconds:00}s" : $"{time.Minutes:00}m{time.Seconds:00}s";
	}
}
