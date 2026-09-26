namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     How the drawn routes (the compass trail, the map widget, the route intro's map) cross a part cut out of the
///     render: the telemetry either side of a cut is next to each other, so a plain line would run straight across
///     whatever the cut-out detour went around (a lake, a block). The same goes for a gap between two files the camera
///     recorded separately (TelemetryFrame.StartsAfterGap).
/// </summary>
public enum RouteJoin
{
	/// <summary>The route stops at the cut and starts again after it - no line that wasn't travelled.</summary>
	Gap,

	/// <summary>A dashed line across the cut - visibly skipped, still connected.</summary>
	Dashed,

	/// <summary>A solid straight line across the cut, as if nothing was left out.</summary>
	Straight
}
