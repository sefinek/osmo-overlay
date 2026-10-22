using System.Text.Json.Nodes;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Cameras.GoPro;

/// <summary>
///     GoPro: an MP4 track tagged gpmd holds GPMF telemetry (GpmfReader) - the accelerometer, the camera's own gravity vector,
///     exposure and, on the models that have a receiver, GPS. A HERO12 Black has none: its recordings have motion, tilt and
///     exposure but no position, so speed, route and map need another source. Read natively from the file's sample table
///     (GoProTrack), no exiftool: it caps the IMU's samples. The same layout is in the .LRV proxies. 360 recordings (GoPro MAX,
///     two video streams) aren't read.
/// </summary>
public sealed class GoProFormat : ICameraFormat
{
	// The models the layout was checked on. Others of the family share GPMF, but their streams may differ.
	private static readonly string[] TestedModels = ["HERO12 Black"];

	public string Id => "gopro";
	public string DisplayName => "GoPro";

	public string? SupportNotice(string? cameraModel)
	{
		return cameraModel is not null && TestedModels.Contains(cameraModel, StringComparer.OrdinalIgnoreCase)
			? null
			: string.Format(CoreStrings.GoPro_NotTested, cameraModel ?? CoreStrings.GoPro_ThisCamera);
	}

	public CameraRecording? Detect(string path, JsonArray streams)
	{
		JsonNode? gpmd = streams.FirstOrDefault(s => s?["codec_type"]?.GetValue<string>() == "data" && s["codec_tag_string"]?.GetValue<string>() == GoProTrack.SampleFormat);
		if (gpmd is null) return null;

		int videos = streams.Count(s => s?["codec_type"]?.GetValue<string>() == "video" && s["disposition"]?["attached_pic"]?.GetValue<int>() != 1);
		return videos == 1 ? new CameraRecording(this, gpmd["index"]?.GetValue<int>()) : null;
	}

	public TelemetryExtractionResult ExtractTelemetry(string path, SourceInfo source)
	{
		string name = Path.GetFileName(path);
		List<GpmfPacket> packets = GoProTrack.Read(path);
		TelemetryExtractionResult result = GoProTelemetryParser.Parse(packets, source.Video.Fps, source.Video.FrameCount);
		AppLogger.Info($"GoPro telemetry in {name}: {result.Frames.Count} frames from {packets.Count} GPMF packets, " +
		               $"{result.Frames.Count(f => f.HasGpsFix)} with a GPS fix");
		return result;
	}

	/// <summary>
	///     The accelerometer's axes are the camera's (AccelX to the left, AccelY backward, AccelZ up - what the sensor feels,
	///     GoProTelemetryParser.ImuAxes), and what it feels is gravity's opposite, so gravity in x right, y down, z forward is
	///     (AccelX, AccelZ, AccelY). Found on a HERO12 recording with the camera pointed at the floor and the sky (z forward
	///     confirmed by the picture, y down by it lying level, both by the camera's own GRAV agreeing: 0.85-0.95 correlation
	///     with the accelerometer through the clip). Its x is the left axis by handedness of the sensor's frame and by the
	///     sign of the small x readings - the clip has no roll test, so a lean to the right being positive rests on that.
	/// </summary>
	public Direction? Gravity(TelemetryFrame frame)
	{
		return new Direction(frame.AccelX, frame.AccelZ, frame.AccelY);
	}

	public Direction? OrientationGravity(TelemetryFrame frame)
	{
		return frame.Orientation is { } orientation ? GoProTilt.Gravity(orientation) : null;
	}

	public string DescribeTelemetry(IReadOnlyList<TelemetryFrame> frames)
	{
		return frames.Any(f => f.HasGpsFix) ? CoreStrings.GoPro_SourceWithGps : CoreStrings.GoPro_SourceNoGps;
	}
}
