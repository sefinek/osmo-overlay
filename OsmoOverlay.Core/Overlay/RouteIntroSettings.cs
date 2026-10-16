using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Cameras;

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
	RouteIntroLabels? Labels = null)
{
	// The start's and finish's captions when the user gave none (null) - "" is a dot without one. Drawn as is, so in capitals.
	public static string DefaultStartLabel => CoreStrings.Overlay_Start.ToUpperInvariant();
	public static string DefaultFinishLabel => CoreStrings.Overlay_Finish.ToUpperInvariant();

	public static readonly RouteIntroSettings Disabled = new(false, 0, RouteIntroStats.None, UnitSystem.Metric, false);

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
			RouteIntroLabels.From(settings.RouteIntroLabels));
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
			RouteIntroFinishLabel = FinishLabel
		};
	}

	/// <summary>
	///     From(settings), but Disabled for a recording with no GPS fix at all - the card is a route summary
	///     (map, distance, speeds, elevation), so without a fix it would only show "MAP UNAVAILABLE" and
	///     zeros for its whole duration. Same availability rule OverlayDataRequirements applies to widgets. Disabled for the
	///     performance test's recording too (SyntheticCamera): it's there to show the picture and the widgets, and a card over
	///     its first seconds would hide them - the settings stay as they are for the user's own recordings.
	/// </summary>
	public static RouteIntroSettings ForRecording(OverlaySettings settings, bool hasGpsFix, ICameraFormat? camera)
	{
		return hasGpsFix && camera is not SyntheticCamera ? From(settings) : Disabled;
	}
}
