using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Tests.Mapping;

[TestClass]
public sealed class TileCoordinatesTests
{
	[TestMethod]
	[DataRow(2, 8, 3, 0)]
	[DataRow(2, -1, 0, 3)]
	[DataRow(0, 4, 0, 0)]
	[DataRow(19, 524287, 524287, 524287)]
	public void LongitudeWrapsWithoutRequestingNonexistentTiles(int zoom, int x, int y, int expectedX)
	{
		Assert.IsTrue(WebMercator.TryNormalizeTile(zoom, x, y, out int actual));
		Assert.AreEqual(expectedX, actual);
	}

	[TestMethod]
	[DataRow(2, 8, 9)]
	[DataRow(2, 0, -1)]
	[DataRow(2, 0, 4)]
	[DataRow(-1, 0, 0)]
	[DataRow(31, 0, 0)]
	public async Task NonexistentPolarTilesDoNotMakeNetworkRequests(int zoom, int x, int y)
	{
		Assert.IsFalse(WebMercator.TryNormalizeTile(zoom, x, y, out _));
		Assert.IsNull(await MapTileFetcher.FetchAsync("not-a-url", zoom, x, y, TimeSpan.Zero, CancellationToken.None));
	}
}
