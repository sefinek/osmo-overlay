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
		double latRad = AngleMath.DegToRad(lat);
		double scale = TileSize * Math.Pow(2, zoom);

		double x = (lon + 180.0) / 360.0 * scale;
		double y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * scale;
		return (x, y);
	}

	public static (int X, int Y) WorldPixelToTile(double worldX, double worldY)
	{
		return ((int)Math.Floor(worldX / TileSize), (int)Math.Floor(worldY / TileSize));
	}

	/// <summary>Longitude wraps around the world; tiles beyond either pole do not exist.</summary>
	internal static bool TryNormalizeTile(int zoom, int x, int y, out int wrappedX)
	{
		wrappedX = x;
		if (zoom is < 0 or > 30) return false;
		long tiles = 1L << zoom;
		if (y < 0 || y >= tiles) return false;
		wrappedX = (int)((x % tiles + tiles) % tiles);
		return true;
	}
}
