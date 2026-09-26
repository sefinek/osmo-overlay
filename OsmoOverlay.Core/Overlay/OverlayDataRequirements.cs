namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Which telemetry a given widget type actually needs to show something other than "--"/0/a
///     placeholder - e.g. the GUI greys out a widget's checkbox instead of letting someone enable it
///     for a file that can never make it show real data (a recording with no GPS fix at all, or one
///     whose djmd stream never decoded a GPS timestamp).
/// </summary>
public static class OverlayDataRequirements
{
	/// <summary>
	///     hasContainerTime is separate from hasGpsTimestamp - DateTimeText/UtcTimeText can still show
	///     something useful from the container's own creation_time tag (OverlayRenderer.DrawTimeText)
	///     even when this recording never had a real GPS timestamp, so it isn't gated behind GPS at
	///     all like the position-based widgets below are.
	/// </summary>
	public static bool IsSupported(OverlayElementType type, bool hasGpsFix, bool hasGpsTimestamp, bool hasContainerTime)
	{
		return type switch
		{
			OverlayElementType.DateTimeText or OverlayElementType.UtcTimeText => hasGpsTimestamp || hasContainerTime,
			OverlayElementType.Compass or OverlayElementType.MapWidget or OverlayElementType.Elevation
				or OverlayElementType.Gradient or OverlayElementType.Distance or OverlayElementType.SpeedGauge
				or OverlayElementType.TripProgressBar => hasGpsFix,
			// The sun dot needs a real position and a real GPS timestamp (TelemetryProcessor leaves Sun at
			// default otherwise, which would draw a fake sun on the horizon) - the container creation_time
			// fallback DateTimeText uses isn't good enough here, it's the camera's own unsynced clock.
			OverlayElementType.SunWidget => hasGpsFix && hasGpsTimestamp,
			// PitchGauge and GMeter come from the accelerometer alone, which is present whenever the
			// djmd stream itself is - no GPS/timestamp dependency to gate on.
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
	public static IReadOnlyList<OverlayElement> ApplyAvailability(IReadOnlyList<OverlayElement> layout,
		bool hasGpsFix, bool hasGpsTimestamp, bool hasContainerTime)
	{
		return
		[
			.. layout.Select(e => e.Visible && !IsSupported(e.Type, hasGpsFix, hasGpsTimestamp, hasContainerTime)
				? e with { Visible = false }
				: e)
		];
	}
}
