using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The map behind a GPS track viewed with no recording (TrackBackdrop). Follow keeps the picture on the position and
///     widens it with the speed (TrackCamera); off shows the whole track at once. MaxZoom is the closest tile level used - the
///     map backs off by itself where a long track would need too many tiles.
/// </summary>
public sealed record TrackMapSettings(string? ProviderId = null, bool ShowLabels = true, bool Follow = true, int MaxZoom = 18, bool ShowTrail = true)
{
	public static TrackMapSettings Default { get; } = new();

	public static TrackMapSettings From(OverlaySettings settings)
	{
		return (settings.TrackMap ?? Default).Sanitized();
	}

	/// <summary>A value read from settings.json that is out of range is brought back into it (From does it).</summary>
	public TrackMapSettings Sanitized()
	{
		return this with { MaxZoom = Math.Clamp(MaxZoom, RouteMapMosaic.MinZoom, RouteMapMosaic.MaxZoom) };
	}
}
