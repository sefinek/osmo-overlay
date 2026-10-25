using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Localization;

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
	RouteIntroStats Stats,
	UnitSystem Units,
	bool ColorBySpeed,
	string? MapProviderId = null,
	bool ShowStartFinish = false,
	string? StartLabel = null,
	string? FinishLabel = null,
	RouteIntroLabels? Labels = null,
	bool ShowWhenTrimmed = false,
	RouteJoin GpsLossJoin = RouteJoins.Default,
	int? MapZoom = null,
	string? Title = null,
	string? Subtitle = null,
	bool ElevationProfile = true,
	bool MapZoomIn = true,
	double MapZoomInFactor = RouteIntroSettings.DefaultZoomInFactor,
	double MapZoomInSeconds = RouteIntroSettings.DefaultZoomInSeconds,
	double MapZoomInDelaySeconds = RouteIntroSettings.DefaultZoomInDelaySeconds)
{
	// The zoom-in starts this many times wider than the final view, takes this long and begins after this delay. The factor
	// is capped by what one mosaic can cover (RouteMapMosaic's tile budget).
	public const double DefaultZoomInFactor = 3;
	public const double DefaultZoomInSeconds = 8;
	public const double DefaultZoomInDelaySeconds = 2;
	public const double MinZoomInFactor = 1.5;
	public const double MaxZoomInFactor = 4;
	public const double MinZoomInSeconds = 0.5;
	public const double MaxZoomInSeconds = 10;
	public const double MaxZoomInDelaySeconds = 5;

	// The start's and finish's captions when the user gave none (null) - "" is a dot without one. Drawn as is, so in capitals.
	public static string DefaultStartLabel => CoreStrings.Overlay_Start.ToUpperInvariant();
	public static string DefaultFinishLabel => CoreStrings.Overlay_Finish.ToUpperInvariant();

	public static readonly RouteIntroSettings Disabled = new(false, 0, RouteIntroStats.None, UnitSystem.Metric, false);

	public static double ClampZoomInFactor(double value)
	{
		return double.IsFinite(value) ? Math.Clamp(value, MinZoomInFactor, MaxZoomInFactor) : DefaultZoomInFactor;
	}

	public static double ClampZoomInSeconds(double value)
	{
		return double.IsFinite(value) ? Math.Clamp(value, MinZoomInSeconds, MaxZoomInSeconds) : DefaultZoomInSeconds;
	}

	public static double ClampZoomInDelay(double value)
	{
		return double.IsFinite(value) ? Math.Clamp(value, 0, MaxZoomInDelaySeconds) : DefaultZoomInDelaySeconds;
	}

	public bool Shows(RouteIntroStats stat)
	{
		return (Stats & stat) != 0;
	}

	/// <summary>The stat's caption on the card: the user's own, else its default.</summary>
	public string LabelOf(RouteIntroStats stat)
	{
		return (Labels ?? RouteIntroLabels.None).For(stat);
	}

	public static RouteIntroSettings From(OverlaySettings settings)
	{
		return new RouteIntroSettings(settings.ShowRouteIntro, settings.RouteIntroDurationSeconds, settings.RouteIntroStats,
			settings.RouteIntroUnits, settings.RouteIntroColorBySpeed, settings.RouteIntroMapProvider, settings.RouteIntroShowStartFinish,
			settings.RouteIntroStartLabel, settings.RouteIntroFinishLabel,
			RouteIntroLabels.From(settings.RouteIntroLabels), settings.RouteIntroWhenTrimmed, settings.RouteIntroGpsLossJoin,
			settings.RouteIntroMapZoom,
			settings.RouteIntroTitle, settings.RouteIntroSubtitle, settings.RouteIntroElevationProfile,
			settings.RouteIntroMapZoomIn, ClampZoomInFactor(settings.RouteIntroMapZoomInFactor), ClampZoomInSeconds(settings.RouteIntroMapZoomInSeconds),
			ClampZoomInDelay(settings.RouteIntroMapZoomInDelaySeconds));
	}

	/// <summary>The reverse of From: these values written into the settings.</summary>
	public OverlaySettings ApplyTo(OverlaySettings settings)
	{
		return settings with
		{
			ShowRouteIntro = Enabled,
			RouteIntroDurationSeconds = DurationSeconds,
			RouteIntroStats = Stats,
			RouteIntroLabels = Labels?.ToSettings(),
			RouteIntroUnits = Units,
			RouteIntroColorBySpeed = ColorBySpeed,
			RouteIntroMapProvider = MapProviderId,
			RouteIntroShowStartFinish = ShowStartFinish,
			RouteIntroStartLabel = StartLabel,
			RouteIntroFinishLabel = FinishLabel,
			RouteIntroWhenTrimmed = ShowWhenTrimmed,
			RouteIntroGpsLossJoin = GpsLossJoin,
			RouteIntroMapZoom = MapZoom,
			RouteIntroTitle = Title,
			RouteIntroSubtitle = Subtitle,
			RouteIntroElevationProfile = ElevationProfile,
			RouteIntroMapZoomIn = MapZoomIn,
			RouteIntroMapZoomInFactor = MapZoomInFactor,
			RouteIntroMapZoomInSeconds = MapZoomInSeconds,
			RouteIntroMapZoomInDelaySeconds = MapZoomInDelaySeconds
		};
	}

	/// <summary>
	///     From(settings), but Disabled for a recording with no GPS fix at all - the card is a route summary
	///     (map, distance, speeds, elevation), so without a fix it would only show "MAP UNAVAILABLE" and
	///     zeros for its whole duration. Same availability rule OverlayDataRequirements applies to widgets. Disabled for the
	///     performance test's recording too (SyntheticCamera): it's there to show the picture and the widgets, and a card over
	///     its first seconds would hide them - the settings stay as they are for the user's own recordings.
	/// </summary>
	public static RouteIntroSettings ForRecording(OverlaySettings settings, bool hasGpsFix, ICameraFormat? camera, bool trimmed = false)
	{
		return hasGpsFix && camera is not SyntheticCamera && Applies(settings.RouteIntroWhenTrimmed, trimmed) ? From(settings) : Disabled;
	}

	/// <summary>
	///     Whether the card is for an output that is `trimmed` - starts later than the recording does (RenderPlan.StartsLate): only with
	///     RouteIntroWhenTrimmed. A cut further in leaves the start the card opens, so it keeps it.
	/// </summary>
	public static bool Applies(bool showWhenTrimmed, bool trimmed)
	{
		return showWhenTrimmed || !trimmed;
	}
}
