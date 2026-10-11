using System.Text.Json.Nodes;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Cameras;

/// <summary>
///     The performance test's recording (SyntheticRecording): a file with SyntheticTelemetry beside it. Not a camera a user
///     has, so it's never listed among the supported ones (CameraFormats.All) - only found by Detect and Find. Its frames'
///     AccelX/Y/Z is the felt gravity itself, in this interface's axes.
/// </summary>
internal sealed class SyntheticCamera : ICameraFormat
{
	public static readonly SyntheticCamera Instance = new();

	private SyntheticCamera()
	{
	}

	public string Id => "synthetic";

	public string DisplayName => CoreStrings.Synthetic_Name;

	public CameraRecording? Detect(string path, JsonArray streams)
	{
		return File.Exists(SyntheticTelemetry.SidecarPath(path)) ? new CameraRecording(this) : null;
	}

	public TelemetryExtractionResult ExtractTelemetry(string path, SourceInfo source)
	{
		return new TelemetryExtractionResult(SyntheticTelemetry.Read(path), SyntheticTelemetry.CameraModel);
	}

	public Direction? Gravity(TelemetryFrame frame)
	{
		return new Direction(frame.AccelX, frame.AccelY, frame.AccelZ);
	}

	public string DescribeTelemetry(IReadOnlyList<TelemetryFrame> frames)
	{
		return CoreStrings.Synthetic_Source;
	}
}
