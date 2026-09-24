namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Groups OverlaySettings' RouteIntro* fields into one OverlayRenderer constructor parameter
///     instead of one per field - built by the caller (RenderJob, PreviewPlayer) from a loaded
///     OverlaySettings right before constructing the renderer. Not itself persisted; OverlaySettings
///     (the flat, serialized record) stays the single source of truth on disk.
/// </summary>
public sealed record RouteIntroSettings(
	bool Enabled,
	double DurationSeconds,
	bool ShowDistance,
	bool ShowMaxSpeed,
	bool ShowAvgSpeed,
	bool ShowDate,
	bool ShowDuration,
	bool ShowCameraModel,
	bool ShowElevationGain,
	UnitSystem Units,
	bool ColorBySpeed)
{
	public static readonly RouteIntroSettings Disabled =
		new(false, 0, false, false, false, false, false, false, false, UnitSystem.Metric, false);

	public static RouteIntroSettings From(OverlaySettings settings)
	{
		return new RouteIntroSettings(settings.ShowRouteIntro, settings.RouteIntroDurationSeconds,
			settings.RouteIntroShowDistance, settings.RouteIntroShowMaxSpeed, settings.RouteIntroShowAvgSpeed,
			settings.RouteIntroShowDate, settings.RouteIntroShowDuration, settings.RouteIntroShowCameraModel,
			settings.RouteIntroShowElevationGain, settings.RouteIntroUnits, settings.RouteIntroColorBySpeed);
	}

	/// <summary>
	///     From(settings), but Disabled for a recording with no GPS fix at all - the card is a route summary
	///     (map, distance, speeds, elevation), so without a fix it would only show "MAP UNAVAILABLE" and
	///     zeros for its whole duration. Same availability rule OverlayDataRequirements applies to widgets.
	/// </summary>
	public static RouteIntroSettings ForRecording(OverlaySettings settings, bool hasGpsFix)
	{
		return hasGpsFix ? From(settings) : Disabled;
	}
}
