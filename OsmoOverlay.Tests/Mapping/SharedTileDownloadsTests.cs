using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Tests.Mapping;

[TestClass]
public sealed class SharedTileDownloadsTests
{
	[TestMethod]
	public async Task OneCancelledConsumerDoesNotCancelAnother_AllCancelledStopsTheDownload()
	{
		CancellationToken downloadToken = default;
		int calls = 0;
		var downloads = new SharedTileDownloads(async (_, ct) =>
		{
			calls++;
			downloadToken = ct;
			await Task.Delay(Timeout.Infinite, ct);
			return [];
		});
		using var firstCancel = new CancellationTokenSource();
		using var secondCancel = new CancellationTokenSource();
		Task first = downloads.GetAsync("https://tiles.example/one", firstCancel.Token);
		Task second = downloads.GetAsync("https://tiles.example/one", secondCancel.Token);
		Assert.AreEqual(1, calls);
		firstCancel.Cancel();
		await Assert.ThrowsAsync<OperationCanceledException>(() => first);
		Assert.IsFalse(downloadToken.IsCancellationRequested);
		secondCancel.Cancel();
		await Assert.ThrowsAsync<OperationCanceledException>(() => second);
		Assert.IsTrue(downloadToken.IsCancellationRequested);
	}

	[TestMethod]
	public async Task CompletedTilesAreShared_ButFailuresAreNotCached()
	{
		int calls = 0;
		var downloads = new SharedTileDownloads((_, _) => ++calls == 1
			? Task.FromException<byte[]>(new HttpRequestException()) : Task.FromResult<byte[]>([1, 2, 3]));

		await Assert.ThrowsExactlyAsync<HttpRequestException>(() => downloads.GetAsync("tile", CancellationToken.None));
		byte[] first = await downloads.GetAsync("tile", CancellationToken.None);
		byte[] second = await downloads.GetAsync("tile", CancellationToken.None);

		Assert.AreSame(first, second);
		Assert.AreEqual(2, calls);
	}
}
