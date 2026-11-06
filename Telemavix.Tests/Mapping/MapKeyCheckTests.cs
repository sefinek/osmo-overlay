using System.Net;
using Telemavix.Core.Mapping;

namespace Telemavix.Tests.Mapping;

[TestClass]
public sealed class MapKeyCheckTests
{
	private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(respond());
		}
	}

	private static HttpResponseMessage Json(HttpStatusCode status, string body)
	{
		return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
	}

	private Task<MapKeyVerdict> Check(Func<HttpResponseMessage> respond)
	{
		return MapKeyCheck.CheckAsync(new HttpClient(new StubHandler(respond)), "a-key", TestContext.CancellationToken);
	}

	public TestContext TestContext { get; set; } = null!;

	[TestMethod]
	public async Task AnImage_IsAccepted()
	{
		Assert.AreEqual(MapKeyVerdict.Accepted, await Check(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
	}

	[TestMethod]
	[DataRow(HttpStatusCode.Unauthorized)]
	[DataRow(HttpStatusCode.Forbidden)]
	public async Task ARefusal_IsRejected(HttpStatusCode status)
	{
		Assert.AreEqual(MapKeyVerdict.Rejected, await Check(() => Json(status, "{\"error\":{\"code\":498,\"message\":\"Token Invalid.\"}}")));
	}

	[TestMethod]
	public async Task ATokenErrorInA200_IsRejected()
	{
		Assert.AreEqual(MapKeyVerdict.Rejected, await Check(() => Json(HttpStatusCode.OK, "{\"error\":{\"code\":498}}")));
	}

	[TestMethod]
	public async Task AnErrorThatIsNotTheToken_SaysNothing()
	{
		Assert.AreEqual(MapKeyVerdict.Unverified, await Check(() => Json(HttpStatusCode.OK, "{\"error\":{\"code\":500}}")));
		Assert.AreEqual(MapKeyVerdict.Unverified, await Check(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
	}

	[TestMethod]
	public async Task NoConnection_SaysNothing()
	{
		Assert.AreEqual(MapKeyVerdict.Unverified, await Check(() => throw new HttpRequestException("offline")));
	}

	[TestMethod]
	public void ARejectedEsriKey_CountsAsNoKey_ButStaysInTheBox()
	{
		const string key = "rejected-key-for-the-test";
		MapKeyCheck.Remember(key, MapKeyVerdict.Rejected);
		try
		{
			var sources = new MapSources(new Dictionary<string, string> { [MapProviders.EsriKeyGroup] = key });

			Assert.IsNull(sources.ApiKey(MapProviders.EsriKeyGroup));
			Assert.AreEqual(key, sources.StoredApiKey(MapProviders.EsriKeyGroup));
			Assert.AreEqual(MapProviders.EsriPublicId, sources.Resolve(MapProviders.AutoSatelliteId));
			Assert.AreEqual(MapProviders.EsriStreetsPublicId, sources.Resolve(MapProviders.StreetsAutoId));
		}
		finally
		{
			MapKeyCheck.Forget(key);
		}
	}

	[TestMethod]
	public void AnUncheckedKey_IsTakenAsGood()
	{
		var sources = new MapSources(new Dictionary<string, string> { [MapProviders.EsriKeyGroup] = "never-checked-key" });

		Assert.AreEqual("never-checked-key", sources.ApiKey(MapProviders.EsriKeyGroup));
		Assert.AreEqual(MapProviders.EsriImageryId, sources.Resolve(MapProviders.AutoSatelliteId));
	}
}
