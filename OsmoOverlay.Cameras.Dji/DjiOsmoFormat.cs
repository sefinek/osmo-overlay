using System.Text.Json.Nodes;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Cameras.Dji;

/// <summary>
///     DJI Osmo Action: an extra MP4 stream tagged djmd holds protobuf-encoded telemetry (GPS position and velocity,
///     accelerometer, ISO, shutter, color temperature, device name). Read natively (DjiMetaTelemetryParser), with
///     exiftool as the fallback when that throws (another model or firmware may lay the stream out differently). Its
///     accelerometer's axes are known (Gravity), and its djmd/dbgi tracks and udta can be carried into a render
///     (Mp4CameraMetadata).
/// </summary>
public sealed class DjiOsmoFormat : ICameraFormat
{
	public string Id => "dji-osmo";
	public string DisplayName => "DJI Osmo Action";
	public bool HasMetadataToCopy => true;

	public CameraRecording? Detect(string path, JsonArray streams)
	{
		JsonNode? djmd = streams.FirstOrDefault(s => s?["codec_type"]?.GetValue<string>() == "data" && s["codec_tag_string"]?.GetValue<string>() == "djmd");
		return djmd is null ? null : new CameraRecording(this, djmd["index"]?.GetValue<int>());
	}

	public TelemetryExtractionResult ExtractTelemetry(string path, SourceInfo source)
	{
		if (source.Camera?.TelemetryStream is { } stream)
			try
			{
				return DjiMetaTelemetryParser.Parse(DjiMetaTelemetryParser.ExtractRawStream(path, stream), source.Video.Fps);
			}
			catch (Exception ex)
			{
				// Native djmd decode is verified against DJI Osmo Action 6 firmware; exiftool may still read another layout.
				AppLogger.Warn(ex, $"Native djmd decode failed for {path}, falling back to exiftool");
			}

		return ExifToolTelemetry.Extract(path);
	}

	/// <summary>
	///     The accelerometer reads the force holding it up (gravity's opposite) along forward (AccelX), right (AccelY) and
	///     down (AccelZ), so gravity is (-AccelY, -AccelZ, -AccelX). Found on a controlled tilt-test recording (right,
	///     left, floor and ceiling tilts at known timestamps: right swung AccelY hugely negative with AccelX flat, left the
	///     mirror, floor/ceiling the same on AccelX with AccelY flat), roll's sign checked live in the GUI, pitch's by GPS
	///     acceleration on 6 real rides (CameraTilt), and AccelZ at -0.75 to -0.98 on average over 11 real Osmo Action 6
	///     recordings from the summary cache, mounted about level.
	/// </summary>
	public Direction? Gravity(TelemetryFrame frame)
	{
		return new Direction(-frame.AccelY, -frame.AccelZ, -frame.AccelX);
	}

	public string DescribeTelemetry(IReadOnlyList<TelemetryFrame> frames)
	{
		var withCameraSettings = frames.Count(f => f.Iso is not null);
		return withCameraSettings == frames.Count
			? "Telemetry source: native djmd decoder (ISO/shutter/color temp all present, exiftool not needed)"
			: withCameraSettings > 0
				? $"Telemetry source: native djmd decoder, partial camera settings ({withCameraSettings}/{frames.Count})"
				: "Telemetry source: exiftool fallback (native djmd decode failed or ISO/shutter/CT unavailable)";
	}

	public void CopyMetadata(string outputPath, IReadOnlyList<string> sourcePaths, CameraMetadataSelection selection)
	{
		Mp4CameraMetadata.CopyInto(outputPath, sourcePaths, selection);
	}
}
