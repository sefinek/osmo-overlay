using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     What telemetry a recording has - which widgets it can feed (OverlayDataRequirements). ContainerTime is separate
///     from GpsTimestamp: DateTimeText/UtcTimeText can still show something useful from the recording's own creation_time
///     (OverlayRenderer.DrawTimeText) without a GPS clock. CameraAxes: the camera format knows its accelerometer's axes
///     (ICameraFormat.Gravity). CameraSettings: ISO and color temperature are recorded.
/// </summary>
public readonly record struct OverlayAvailability(
	bool GpsFix,
	bool GpsTimestamp,
	bool ContainerTime,
	bool CameraAxes = true,
	bool CameraSettings = true,
	bool AmbientTemperature = false,
	bool ImuTemperature = false,
	bool DeviceDistance = false,
	bool DeviceBattery = false,
	bool AudioLevel = false,
	bool WindProcessing = false,
	bool ImageLuma = false)
{
	public static OverlayAvailability Of(IReadOnlyList<TelemetryFrame> frames, bool containerTime, ICameraFormat? camera)
	{
		return new OverlayAvailability(TelemetryProcessor.HasAnyGpsFix(frames), TelemetryProcessor.HasAnyGpsTimestamp(frames), containerTime,
			frames.Count > 0 && camera?.Gravity(frames[0]) is not null, frames.Any(f => f.Iso is not null),
			frames.Any(f => f.AmbientTemperatureCelsius is { } t && double.IsFinite(t)),
			frames.Any(f => f.ImuTemperatureCelsius is { } t && double.IsFinite(t)),
			frames.Any(f => f.DeviceDistanceMeters is { } d && double.IsFinite(d)),
			frames.Any(f => f.DeviceBatteryPercent is { } b && double.IsFinite(b)),
			frames.Any(f => f.AudioRmsDbfs is >= -128 and <= 0 || f.AudioPeakDbfs is >= -128 and <= 0),
			frames.Any(f => f.WindProcessingActive is not null || f.WindProcessingLevel is >= 0 and <= 100),
			frames.Any(f => f.ImageLuma is >= 0 and <= 255));
	}

	public IReadOnlyList<OverlayElement> Apply(IReadOnlyList<OverlayElement> layout)
	{
		return OverlayDataRequirements.ApplyAvailability(layout, this);
	}
}

/// <summary>
///     Which telemetry a given widget type actually needs to show something other than "--"/0/a
///     placeholder - e.g. the GUI greys out a widget's checkbox instead of letting someone enable it
///     for a file that can never make it show real data (a recording with no GPS fix at all, or one
///     whose djmd stream never decoded a GPS timestamp).
/// </summary>
public static class OverlayDataRequirements
{
	public static bool IsSupported(OverlayElementType type, OverlayAvailability data)
	{
		return type switch
		{
			OverlayElementType.DateTimeText or OverlayElementType.UtcTimeText => data.GpsTimestamp || data.ContainerTime,
			OverlayElementType.Compass or OverlayElementType.MapWidget or OverlayElementType.Elevation
				or OverlayElementType.Gradient or OverlayElementType.Distance or OverlayElementType.SpeedGauge
				or OverlayElementType.TripProgressBar or OverlayElementType.ProfileChart or OverlayElementType.TripStat => data.GpsFix,
			// Without a fix anywhere it would only ever say it's searching.
			OverlayElementType.GpsAlert => data.GpsFix,
			// The sun dot needs a real position and a real GPS timestamp (TelemetryProcessor leaves Sun at
			// default otherwise, which would draw a fake sun on the horizon) - the container creation_time
			// fallback DateTimeText uses isn't good enough here, it's the camera's own unsynced clock.
			OverlayElementType.SunWidget => data.GpsFix && data.GpsTimestamp,
			// From the accelerometer's axes - GPS only refines the tilt (CameraTilt), so no GPS needed.
			OverlayElementType.RollGauge or OverlayElementType.PitchGauge or OverlayElementType.GMeter => data.CameraAxes,
			OverlayElementType.CameraInfo => data.CameraSettings,
			OverlayElementType.AmbientTemperature => data.AmbientTemperature,
			OverlayElementType.ImuTemperature => data.ImuTemperature,
			OverlayElementType.DeviceDistance => data.DeviceDistance,
			OverlayElementType.DeviceBattery => data.DeviceBattery,
			OverlayElementType.AudioLevel => data.AudioLevel,
			OverlayElementType.WindProcessing => data.WindProcessing,
			OverlayElementType.ImageLuma => data.ImageLuma,
			// Text and Image need no data.
			_ => true
		};
	}

	/// <summary>
	///     `layout` with any element that's on but unsupported for this file forced Visible=false -
	///     never mutates the caller's list or the saved preset, only what actually gets rendered/
	///     previewed. Applied at every point a layout reaches OverlayRenderer (PreviewPlayer, RenderJob)
	///     so a widget that was checked before a different, data-less file was loaded can't still get
	///     burned into the video (or shown in the live preview) as a "--"/0/placeholder.
	/// </summary>
	public static IReadOnlyList<OverlayElement> ApplyAvailability(IReadOnlyList<OverlayElement> layout, OverlayAvailability data)
	{
		return [.. layout.Select(e => e.Visible && !IsSupported(e.Type, data) ? e with { Visible = false } : e)];
	}
}
