using System.Text.Json.Nodes;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Cameras.Insta360;

/// <summary>
///     Insta360: telemetry in a trailer after the MP4's last box (Insta360TrailerParser - ffprobe doesn't see it), no GPS
///     read yet. A 360 recording is two square lens streams (.insv, the second one the front lens) or both lenses side by
///     side twice as wide as high (.lrv, the left one the front lens). The accelerometer's axes are known in the lens
///     space (Gravity - found on an X4), so roll and pitch are the camera body's against its front lens, whatever
///     view is framed.
/// </summary>
public sealed class Insta360Format : ICameraFormat
{
	public string Id => "insta360";
	public string DisplayName => "Insta360";

	public string SupportNotice =>
		"OsmoOverlay is made mainly for the DJI Osmo Action 6. Insta360 recordings haven't been tested much, and their GPS " +
		"data isn't supported at all. Some widgets may be unavailable or read wrong.";

	public CameraRecording? Detect(string path, JsonArray streams)
	{
		return Insta360TrailerParser.HasTrailer(path) ? new CameraRecording(this, Lenses: DetectLenses(streams)) : null;
	}

	/// <summary>Two equal square video streams, or one twice as wide as high - anything else (a single-lens camera) is flat.</summary>
	internal static DualFisheye? DetectLenses(JsonArray streams)
	{
		List<(int Width, int Height)> videos =
		[
			.. streams.OfType<JsonNode>()
				.Where(s => s["codec_type"]?.GetValue<string>() == "video" && s["disposition"]?["attached_pic"]?.GetValue<int>() != 1)
				.Select(s => (s["width"]?.GetValue<int>() ?? 0, s["height"]?.GetValue<int>() ?? 0))
		];

		return videos switch
		{
			[var a, var b, ..] when a.Width == a.Height && a == b => new DualFisheye(FisheyeLayout.TwoStreams, a.Width),
			[var a] when a.Width == a.Height * 2 => new DualFisheye(FisheyeLayout.SideBySide, a.Height),
			_ => null
		};
	}

	/// <summary>
	///     One frame per captured frame up to the file's own length. No exiftool fallback: it caps the IMU at 20000
	///     samples (20 s) unless forced, and reads the same records this parser does.
	/// </summary>
	public TelemetryExtractionResult ExtractTelemetry(string path, SourceInfo source)
	{
		var name = Path.GetFileName(path);
		Insta360Trailer trailer = Insta360TrailerParser.Read(path) ?? throw new InvalidDataException($"{name} has no Insta360 telemetry.");
		List<TelemetryFrame> frames = Insta360TrailerParser.ToFrames(trailer, source.DurationSeconds);
		if (frames.Count == 0) throw new InvalidDataException($"{name}'s Insta360 telemetry has no per-frame timing.");

		AppLogger.Info($"Insta360 telemetry in {name}: {frames.Count} frames, {trailer.Imu.Count} IMU samples");
		return new TelemetryExtractionResult(frames, trailer.Model ?? DisplayName);
	}

	/// <summary>
	///     Down in v360's lens space is (AccelZ, -AccelX, AccelY): of all 48 signed axis orders, only this one kept the
	///     horizon level at 2, 5, 12 and 18 s of a real X4 .insv with the camera tilted differently each time, and the same
	///     held on its .lrv. Another Insta360 model may put its sensor differently.
	/// </summary>
	public Direction? Gravity(TelemetryFrame frame)
	{
		return new Direction(frame.AccelZ, -frame.AccelX, frame.AccelY);
	}

	public string DescribeTelemetry(IReadOnlyList<TelemetryFrame> frames)
	{
		return "Telemetry source: Insta360 trailer (accelerometer averaged per frame, exposure)";
	}
}
