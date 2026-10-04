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
	string StartLabel = RouteIntroSettings.DefaultStartLabel,
	string FinishLabel = RouteIntroSettings.DefaultFinishLabel,
	RouteIntroLabels? Labels = null)
{
	public const string DefaultStartLabel = "START";
	public const string DefaultFinishLabel = "FINISH";

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
			settings.RouteIntroStartLabel ?? DefaultStartLabel, settings.RouteIntroFinishLabel ?? DefaultFinishLabel,
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
	///     zeros for its whole duration. Same availability rule OverlayDataRequirements applies to widgets.
	/// </summary>
	public static RouteIntroSettings ForRecording(OverlaySettings settings, bool hasGpsFix)
	{
		return hasGpsFix ? From(settings) : Disabled;
	}
}
