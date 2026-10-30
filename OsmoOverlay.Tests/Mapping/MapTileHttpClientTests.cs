using System.Net;
using System.Net.Http.Headers;
using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Tests.Mapping;

[TestClass]
public sealed class MapTileHttpClientTests
{
	private sealed class Clock : TimeProvider
	{
		private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		public List<TimeSpan> Delays { get; } = [];
		public override DateTimeOffset GetUtcNow() => _now;
		public Task Advance(TimeSpan duration, CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();
			Delays.Add(duration);
			_now += duration;
			return Task.CompletedTask;
		}
	}

	private sealed class Handler(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		public int Calls { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();
			return Task.FromResult(respond(++Calls, request));
		}
	}

	private static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public async Task RetryAfter_DeltaAndDatePauseOtherTilesOnTheSameServer(bool dateHeader)
	{
		var clock = new Clock();
		using var handler = new Handler((call, _) =>
		{
			if (call > 1) return Success();
			var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
			response.Headers.RetryAfter = dateHeader
				? new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(90))
				: new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
			return response;
		});
		using var http = new HttpClient(handler);
		var client = new MapTileHttpClient(http, clock, clock.Advance);

		await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetBytesAsync("https://tiles.example/one", CancellationToken.None));
		await client.GetBytesAsync("https://tiles.example/two", CancellationToken.None);

		Assert.AreEqual(TimeSpan.FromSeconds(90), TimeSpan.FromTicks(clock.Delays.Sum(d => d.Ticks)));
		Assert.AreEqual(2, handler.Calls);
	}

	[TestMethod]
	public async Task MissingRetryAfter_UsesExponentialCooldownInsteadOfMillisecondRetries()
	{
		var clock = new Clock();
		using var handler = new Handler((call, _) => call < 3 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Success());
		using var http = new HttpClient(handler);
		var client = new MapTileHttpClient(http, clock, clock.Advance);

		for (int i = 0; i < 2; i++)
			await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetBytesAsync("https://tiles.example/one", CancellationToken.None));
		await client.GetBytesAsync("https://tiles.example/two", CancellationToken.None);

		CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) }, clock.Delays);
	}

	[TestMethod]
	public async Task PendingTilesWaitTogether_AndCancellationDoesNotSendRequests()
	{
		var clock = new Clock();
		using var handler = new Handler((_, request) => request.RequestUri!.Host == "limited.example"
			? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Success());
		using var http = new HttpClient(handler);
		var client = new MapTileHttpClient(http, clock, (_, ct) => Task.Delay(Timeout.Infinite, ct));
		await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetBytesAsync("https://limited.example/first", CancellationToken.None));
		using var cancel = new CancellationTokenSource();
		Task first = client.GetBytesAsync("https://limited.example/second", cancel.Token);
		Task second = client.GetBytesAsync("https://limited.example/third", cancel.Token);

		Assert.AreEqual(1, handler.Calls);
		await client.GetBytesAsync("https://other.example/first", CancellationToken.None);
		Assert.AreEqual(2, handler.Calls, "another server is not held up");
		cancel.Cancel();
		await Assert.ThrowsAsync<OperationCanceledException>(() => first);
		await Assert.ThrowsAsync<OperationCanceledException>(() => second);
		Assert.AreEqual(2, handler.Calls);
	}

	[TestMethod]
	public async Task SuccessResetsBackoff_And404DoesNotThrottleTheServer()
	{
		var clock = new Clock();
		using var handler = new Handler((call, _) => call switch
		{
			1 or 3 => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
			5 => new HttpResponseMessage(HttpStatusCode.NotFound),
			_ => Success()
		});
		using var http = new HttpClient(handler);
		var client = new MapTileHttpClient(http, clock, clock.Advance);
		for (int i = 1; i <= 6; i++)
		{
			if (i is 1 or 3 or 5)
				await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetBytesAsync("https://tiles.example/tile", CancellationToken.None));
			else await client.GetBytesAsync("https://tiles.example/tile", CancellationToken.None);
		}
		CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) }, clock.Delays);
	}

	[TestMethod]
	public async Task RefusedRepeatedly_LaterRequestsFailAtOnceAndTheRefusalIsAnnouncedOnce()
	{
		var clock = new Clock();
		using var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));
		using var http = new HttpClient(handler);
		List<HttpStatusCode> announced = [];
		var client = new MapTileHttpClient(http, clock, clock.Advance, onAccessDenied: (_, status) => announced.Add(status));

		for (int i = 0; i < 6; i++)
		{
			HttpRequestException ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetBytesAsync($"https://tiles.example/{i}", CancellationToken.None));
			Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode);
		}

		Assert.AreEqual(3, handler.Calls, "after three refusals the server isn't asked again");
		CollectionAssert.AreEqual(new[] { HttpStatusCode.Forbidden }, announced);
	}

	[TestMethod]
	public async Task RefusedServer_IsAskedAgainAfterThePause_AndAnnouncesIfItRefusesAgain()
	{
		var clock = new Clock();
		using var handler = new Handler((call, _) => call <= 3 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Success());
		using var http = new HttpClient(handler);
		int announced = 0;
		var client = new MapTileHttpClient(http, clock, clock.Advance, onAccessDenied: (_, _) => announced++);
		for (int i = 0; i < 4; i++)
			await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetBytesAsync("https://tiles.example/tile", CancellationToken.None));

		await clock.Advance(TimeSpan.FromMinutes(1), CancellationToken.None);
		await client.GetBytesAsync("https://tiles.example/tile", CancellationToken.None);

		Assert.AreEqual(4, handler.Calls);
		Assert.AreEqual(1, announced);
	}

	[TestMethod]
	public async Task ATileBetweenRefusals_KeepsTheServerInUse()
	{
		var clock = new Clock();
		using var handler = new Handler((call, _) => call is 1 or 2 or 4 or 5 ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Success());
		using var http = new HttpClient(handler);
		var client = new MapTileHttpClient(http, clock, clock.Advance);

		for (int i = 1; i <= 6; i++)
		{
			if (i is 1 or 2 or 4 or 5)
				await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetBytesAsync("https://tiles.example/tile", CancellationToken.None));
			else await client.GetBytesAsync("https://tiles.example/tile", CancellationToken.None);
		}

		Assert.AreEqual(6, handler.Calls);
	}
}
