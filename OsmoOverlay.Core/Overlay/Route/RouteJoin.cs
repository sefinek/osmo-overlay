namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     How the drawn routes (the compass trail, the map widget, the route intro's map) cross a part cut out of the
///     render: the telemetry either side of a cut is next to each other, so a plain line would run straight across
///     whatever the cut-out detour went around (a lake, a block). Each cut has its own (FrameRange.Join); the same goes for
///     a gap between two files the camera recorded separately (TelemetryFrame.StartsAfterGap), which takes the default. A
///     widget picks one for a stretch without a GPS fix (TrailOverlayElement.GpsLossJoin).
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

/// <summary>What a route join falls back to, and the one order every menu lists them in - the gap, which draws least, last.</summary>
public static class RouteJoins
{
	public const RouteJoin Default = RouteJoin.Dashed;

	public static IReadOnlyList<RouteJoin> Menu { get; } = [RouteJoin.Dashed, RouteJoin.Straight, RouteJoin.Gap];
}
