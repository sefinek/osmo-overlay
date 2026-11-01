using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Drones.Dji;

/// <summary>
///     The camera of a DJI drone (Mavic 2 and the like): its MP4 holds the picture and nothing else - no telemetry stream, no
///     DJI tag - so the file is told by its name (DJI_0001.MP4) and the telemetry is the flight log (DjiFlightLogFormat), added as a
///     track. Until it is, the recording has frames with no GPS fix and the overlay shows only what needs none. A file of
///     the DJI naming that carries a djmd stream belongs to the Osmo Action format, registered before this one.
/// </summary>
public sealed partial class DjiDroneFormat : ICameraFormat
{
	public string Id => "dji-drone";
	public string DisplayName => CoreStrings.DjiDrone_Name;
	public string Brand => "DJI";
	public bool IsDrone => true;
	public string? PreferredPresetId => OverlayPresetStore.DronePresetId;

	public CameraRecording? Detect(string path, JsonArray streams)
	{
		if (!FileName().IsMatch(Path.GetFileName(path))) return null;

		bool hasData = streams.Any(s => s?["codec_type"]?.GetValue<string>() == "data");
		int videos = streams.Count(s => s?["codec_type"]?.GetValue<string>() == "video" && s["disposition"]?["attached_pic"]?.GetValue<int>() != 1);
		return videos == 1 && !hasData ? new CameraRecording(this) : null;
	}

	/// <summary>One empty sample per video frame - nothing is known about the flight until a log is added.</summary>
	public TelemetryExtractionResult ExtractTelemetry(string path, SourceInfo source)
	{
		double fps = source.Video.Fps > 0 ? source.Video.Fps : 30;
		int count = Math.Max((int)Math.Round(source.DurationSeconds * fps), 1);
		var frames = new List<TelemetryFrame>(count);
		for (int i = 0; i < count; i++) frames.Add(new TelemetryFrame(i, i / fps, 0, 0, 0, null, 0, 0, 0, HasGpsFix: false));
		return new TelemetryExtractionResult(frames, null);
	}

	public Direction? Gravity(TelemetryFrame frame)
	{
		return null;
	}

	public string DescribeTelemetry(IReadOnlyList<TelemetryFrame> frames)
	{
		return CoreStrings.DjiDrone_NeedsLog;
	}

	public string? SupportNotice(string? cameraModel)
	{
		return CoreStrings.DjiDrone_NeedsLog;
	}

	[GeneratedRegex(@"^DJI_\d{4,}\.(mp4|mov)$", RegexOptions.IgnoreCase)]
	private static partial Regex FileName();
}
