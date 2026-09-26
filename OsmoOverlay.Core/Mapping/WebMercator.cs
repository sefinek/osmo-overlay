using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Mapping;

/// <summary>
///     Standard "slippy map" Web Mercator math (the projection every XYZ tile server, including
///     OpenStreetMap, uses) - lat/lon to the pixel/tile grid at a given zoom, 256px tiles. Kept
///     separate from the flat-earth local East/North meters used elsewhere (TelemetryProcessor,
///     the Compass trail) because tile pixel positions must line up exactly with the downloaded
///     tile images, not just be locally consistent.
/// </summary>
public static class WebMercator
{
	public const int TileSize = 256;

	/// <summary>World pixel X/Y at the given zoom - the coordinate space tile images are laid out in.</summary>
	public static (double X, double Y) LatLonToWorldPixel(double lat, double lon, int zoom)
	{
		var latRad = AngleMath.DegToRad(lat);
		var scale = TileSize * Math.Pow(2, zoom);

		var x = (lon + 180.0) / 360.0 * scale;
		var y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * scale;
		return (x, y);
	}

	public static (int X, int Y) WorldPixelToTile(double worldX, double worldY)
	{
		return ((int)Math.Floor(worldX / TileSize), (int)Math.Floor(worldY / TileSize));
	}
}
