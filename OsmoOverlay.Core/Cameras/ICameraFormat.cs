using System.Text.Json.Nodes;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Cameras;

/// <summary>
///     A recording's telemetry frames (on the file's own timeline) and the camera model its telemetry names. GpsClockShift is
///     set when the camera format moved the GPS time it read onto the camera's own clock (a camera that writes it in the
///     wrong time zone) - by how much, for the log.
/// </summary>
public sealed record TelemetryExtractionResult(List<TelemetryFrame> Frames, string? CameraModel, TimeSpan? GpsClockShift = null);

/// <summary>What a camera format found in a file (ICameraFormat.Detect).</summary>
/// <param name="TelemetryStream">The ffprobe index of the stream holding the telemetry, when it is one (DJI's djmd).</param>
/// <param name="Lenses">A 360 recording's lenses - the app then works with a flat view of them (Reframe).</param>
public sealed record CameraRecording(ICameraFormat Format, int? TelemetryStream = null, DualFisheye? Lenses = null);

/// <summary>
///     A camera whose recordings the app reads - everything about it that isn't the same for every camera: telling its
///     files apart, reading its telemetry, what its accelerometer's axes mean, and its own metadata. Each lives in a
///     project of its own (OsmoOverlay.Cameras.*) and the apps register them at startup (CameraFormats); Core itself
///     knows no camera. Telemetry frames keep the camera's own accelerometer axes - only its format interprets them.
/// </summary>
public interface ICameraFormat
{
	/// <summary>Kept in FileSummary (and its cache) - never change a released one.</summary>
	string Id { get; }

	string DisplayName { get; }

	/// <summary>Whether this camera recorded the file, from ffprobe's stream list and the file itself - null when it didn't.</summary>
	CameraRecording? Detect(string path, JsonArray streams);

	/// <summary>The file's telemetry, one frame per video frame or so, SampleTimeSeconds from 0.</summary>
	TelemetryExtractionResult ExtractTelemetry(string path, SourceInfo source);

	/// <summary>
	///     Which way gravity pulls as the accelerometer feels it, in g, in the camera's own space (Direction: x right,
	///     y down, z forward - a 360 camera's front lens, the lens space it's reframed from) - the camera's acceleration
	///     included. The one place a camera's accelerometer axes are read as directions: roll and pitch (CameraTilt), the
	///     G-meter (DerivedFrame.LateralAccelG) and a 360 picture's leveling (HorizonLeveling). Null when the axes aren't
	///     known - those widgets are then unavailable (OverlayAvailability.CameraAxes).
	/// </summary>
	Direction? Gravity(TelemetryFrame frame);

	/// <summary>A line for the log saying what the telemetry was read from and what it holds.</summary>
	string DescribeTelemetry(IReadOnlyList<TelemetryFrame> frames);

	/// <summary>
	///     What a user should know about this camera's support before relying on it (e.g. little tested), shown once when
	///     its recording is loaded - null when it's fully supported. `cameraModel` is the model the telemetry names, for a
	///     format that supports some of its cameras and not others.
	/// </summary>
	string? SupportNotice(string? cameraModel)
	{
		return null;
	}

	/// <summary>Whether CopyMetadata has anything to carry over into a render.</summary>
	bool HasMetadataToCopy => false;

	/// <summary>The camera's own metadata (its data tracks, thumbnails) into a finished render - after ffmpeg, which can't mux it.</summary>
	void CopyMetadata(string outputPath, IReadOnlyList<string> sourcePaths, CameraMetadataSelection selection)
	{
	}
}

/// <summary>The cameras the app knows - registered once at startup by the app (Program), in the order they're tried.</summary>
public static class CameraFormats
{
	private static ICameraFormat[] _formats = [];

	public static IReadOnlyList<ICameraFormat> All => _formats;

	public static void Register(params ICameraFormat[] formats)
	{
		_formats = [.. _formats, .. formats.Where(f => _formats.All(known => known.Id != f.Id))];
	}

	public static ICameraFormat? Find(string? id)
	{
		return id is null ? null : Array.Find(_formats, f => f.Id == id);
	}

	/// <summary>The first registered camera that recorded the file - null for a file none of them knows.</summary>
	internal static CameraRecording? Detect(string path, JsonArray streams)
	{
		foreach (ICameraFormat format in _formats)
		{
			if (format.Detect(path, streams) is { } recording)
				return recording;
		}

		return null;
	}
}
