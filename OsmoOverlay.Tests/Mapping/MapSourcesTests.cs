using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class MapSourcesTests
{
	[TestMethod]
	public void UrlTemplate_UsesTheSameKeyForEveryCartoStyle()
	{
		var sources = new MapSources(new Dictionary<string, string> { [MapProviders.CartoKeyGroup] = " abc_def_1_0123456789abcdef01234567 " });

		StringAssert.Contains(sources.UrlTemplate("carto-dark"), "key=abc_def_1_0123456789abcdef01234567");
		StringAssert.Contains(sources.UrlTemplate("carto-voyager"), "key=abc_def_1_0123456789abcdef01234567");
		Assert.AreEqual(MapTileFetcher.SatelliteUrlTemplate, sources.UrlTemplate("esri"));
	}

	[TestMethod]
	public void UrlTemplate_FallsBackForUnknownProviderAndEmptyCustom()
	{
		var sources = new MapSources();

		Assert.AreEqual(MapTileFetcher.SatelliteUrlTemplate, sources.UrlTemplate(null));
		Assert.AreEqual(MapTileFetcher.SatelliteUrlTemplate, sources.UrlTemplate("gone"));
		Assert.AreEqual(MapTileFetcher.OpenStreetMapUrlTemplate, sources.UrlTemplate(MapProviders.CustomId));
	}

	[TestMethod]
	public void Custom_TakesAKeyOnlyWhenItsUrlHasThePlaceholder()
	{
		var plain = new MapSources(CustomUrlTemplate: "https://t.example/{z}/{x}/{y}.png");
		var keyed = new MapSources(new Dictionary<string, string> { [MapSources.CustomKeyGroup] = "k 1" }, "https://t.example/{z}/{x}/{y}.png?k={api_key}");

		Assert.IsNull(plain.KeyGroupOf(MapProviders.CustomId));
		Assert.AreEqual(MapSources.CustomKeyGroup, keyed.KeyGroupOf(MapProviders.CustomId));
		Assert.AreEqual("https://t.example/{z}/{x}/{y}.png?k=k%201", keyed.UrlTemplate(MapProviders.CustomId));
	}

	[TestMethod]
	public void Attribution_IsNullWhenThereIsNoneToShow()
	{
		Assert.IsNull(new MapSources().Attribution(MapProviders.CustomId));
		Assert.AreEqual("my credit", new MapSources(CustomAttribution: " my credit ").Attribution(MapProviders.CustomId));
		Assert.AreEqual(MapTileFetcher.SatelliteAttribution, new MapSources().Attribution(null));
	}

	[TestMethod]
	public void Attribution_TurnedOffHidesItForEveryProvider()
	{
		var sources = new MapSources(ShowAttribution: false, CustomAttribution: "c");

		Assert.IsNull(sources.Attribution(null));
		Assert.IsNull(sources.Attribution("osm"));
		Assert.IsNull(sources.Attribution(MapProviders.CustomId));
	}
}
