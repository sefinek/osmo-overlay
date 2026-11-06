using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     The map behind a GPS track viewed with no recording (TrackBackdrop). Follow keeps the picture on the position and
///     widens it with the speed (TrackCamera); off shows the whole track at once. MaxZoom is the closest tile level used - the
///     map backs off by itself where a long track would need too many tiles. The line is drawn like the Map and Compass widgets'
///     (color, by speed or not, width in pixels of a 1080-pixel-high picture), the part still ahead dimmed when ShowAhead; MarkerScale
///     sizes the aircraft or the dot.
/// </summary>
public sealed record TrackMapSettings(string? ProviderId = null, bool ShowLabels = true, bool Follow = true, int MaxZoom = 18, bool ShowTrail = true,
	bool ColorBySpeed = true, string? TrailColor = null, float TrailWidth = 3, bool ShowAhead = true, float MarkerScale = 1.3f)
{
	/// <summary>In pixels of a 1080-pixel-high picture; the line scales with the picture.</summary>
	public const float DefaultTrailWidth = 3, DefaultMarkerScale = 1.3f;

	public const float MinTrailWidth = 1, MaxTrailWidth = 12, MinMarkerScale = 0.5f, MaxMarkerScale = 3;

	public static TrackMapSettings Default { get; } = new();

	public static TrackMapSettings From(OverlaySettings settings)
	{
		return (settings.TrackMap ?? Default).Sanitized();
	}

	/// <summary>A value read from settings.json that is out of range is brought back into it (From does it).</summary>
	public TrackMapSettings Sanitized()
	{
		return this with
		{
			MaxZoom = Math.Clamp(MaxZoom, RouteMapMosaic.MinZoom, RouteMapMosaic.MaxZoom),
			TrailWidth = float.IsFinite(TrailWidth) ? Math.Clamp(TrailWidth, MinTrailWidth, MaxTrailWidth) : DefaultTrailWidth,
			MarkerScale = float.IsFinite(MarkerScale) ? Math.Clamp(MarkerScale, MinMarkerScale, MaxMarkerScale) : DefaultMarkerScale
		};
	}
}
