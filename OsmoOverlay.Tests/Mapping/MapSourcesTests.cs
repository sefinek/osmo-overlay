using System.Text.Json;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Mapping;

[TestClass]
public sealed class MapSourcesTests
{
	private static readonly MapBounds InPoland = new(53.2, 16.4, 53.3, 16.5);
	private static readonly MapBounds InGermany = new(52.4, 13.3, 52.6, 13.5);
	private static readonly MapBounds AcrossTheBorder = new(52.3, 13.9, 52.4, 14.6);

	[TestMethod]
	public void UrlTemplate_UsesTheSameKeyForEveryStyleOfAProvider()
	{
		var sources = new MapSources(new Dictionary<string, string>
		{
			[MapProviders.CartoKeyGroup] = " abc_def_1_0123456789abcdef01234567 ",
			[MapProviders.MapTilerKeyGroup] = "mt"
		});

		StringAssert.Contains(sources.UrlTemplate("carto-dark"), "key=abc_def_1_0123456789abcdef01234567");
		StringAssert.Contains(sources.UrlTemplate("carto-voyager"), "key=abc_def_1_0123456789abcdef01234567");
		StringAssert.Contains(sources.UrlTemplate("maptiler-satellite"), "key=mt");
		StringAssert.Contains(sources.UrlTemplate("maptiler-outdoor"), "key=mt");
	}

	[TestMethod]
	public void MissesApiKey_OnlyForAProviderTakingAKeyWithoutOne()
	{
		var sources = new MapSources(new Dictionary<string, string> { [MapProviders.CartoKeyGroup] = "abc" },
			"https://tiles.example.com/{z}/{x}/{y}.png?k={api_key}");

		Assert.IsTrue(sources.MissesApiKey("thunderforest-outdoors"));
		Assert.IsTrue(sources.MissesApiKey(MapProviders.CustomId), "a custom URL with {api_key}");
		Assert.IsFalse(sources.MissesApiKey("carto-dark"));
		Assert.IsFalse(sources.MissesApiKey(MapProviders.AutoSatelliteId));
		Assert.IsFalse(sources.MissesApiKey("opentopomap"));
		Assert.IsTrue(new MapSources(new Dictionary<string, string> { [MapProviders.ThunderforestKeyGroup] = "  " })
			.MissesApiKey("thunderforest-cycle"), "a blank key");
	}

	[TestMethod]
	public void TheDefault_WithoutKeys_IsEsrisPublicServiceWhereverTheRouteIs()
	{
		Assert.AreEqual(MapProviders.EsriPublicId, new MapSources(RouteBounds: InPoland).Resolve(MapProviders.AutoSatelliteId));
		Assert.AreEqual(MapProviders.EsriPublicId, new MapSources(RouteBounds: InGermany).Resolve(MapProviders.AutoSatelliteId));
		Assert.AreEqual(MapProviders.EsriPublicId, new MapSources(RouteBounds: AcrossTheBorder).Resolve(MapProviders.AutoSatelliteId));
		Assert.AreEqual(MapProviders.EsriPublicId, new MapSources().Resolve(null), "no route known yet");
	}

	[TestMethod]
	public void TheDefault_IsEsri_KeyedWithItsKey()
	{
		var mapTiler = new MapSources(new Dictionary<string, string> { [MapProviders.MapTilerKeyGroup] = "mt" }, RouteBounds: InPoland);
		var both = mapTiler with { ApiKeys = new Dictionary<string, string> { [MapProviders.MapTilerKeyGroup] = "mt", [MapProviders.EsriKeyGroup] = "e" } };

		Assert.AreEqual(MapProviders.EsriPublicId, mapTiler.Resolve(MapProviders.AutoSatelliteId), "Esri only, one source");
		Assert.AreEqual(MapProviders.EsriImageryId, both.Resolve(MapProviders.AutoSatelliteId));
		StringAssert.Contains(both.UrlTemplate(MapProviders.AutoSatelliteId), "token=e");
		Assert.AreEqual("carto-dark", both.Resolve("carto-dark"), "a picked provider stays");
	}

	[TestMethod]
	public void Streets_TakeTheSameKeysInTheSameOrder()
	{
		var esri = new MapSources(new Dictionary<string, string> { [MapProviders.EsriKeyGroup] = "e", [MapProviders.MapTilerKeyGroup] = "mt" });
		var mapTiler = new MapSources(new Dictionary<string, string> { [MapProviders.MapTilerKeyGroup] = "mt" });

		Assert.AreEqual(MapProviders.EsriStreetsId, esri.Resolve(MapProviders.StreetsAutoId));
		Assert.AreEqual(MapProviders.EsriStreetsPublicId, mapTiler.Resolve(MapProviders.StreetsAutoId), "Esri only, one source");
		Assert.AreEqual(MapProviders.EsriStreetsPublicId, new MapSources().Resolve(MapProviders.StreetsAutoId));
		StringAssert.Contains(esri.UrlTemplate(MapProviders.StreetsAutoId), "token=e");
	}

	[TestMethod]
	public void NoChoice_IsTheDefaultStyle()
	{
		Assert.AreEqual(MapProviders.EsriPublicId, new MapSources().Resolve(null));
		Assert.AreEqual(MapProviders.EsriStreetsPublicId, new MapSources(DefaultProviderId: MapProviders.StreetsAutoId).Resolve(null));
		Assert.AreEqual(MapProviders.EsriPublicId, new MapSources(DefaultProviderId: MapProviders.StreetsAutoId).Resolve(MapProviders.AutoSatelliteId),
			"a picked style stays");
	}

	[TestMethod]
	public void ARemovedOrUnknownProvider_FallsBackToTheDefault()
	{
		var sources = new MapSources(RouteBounds: InPoland);

		Assert.AreEqual(MapProviders.EsriPublicId, sources.Resolve("esri"));
		Assert.AreEqual(MapProviders.EsriPublicId, sources.Resolve("osm"));
		Assert.AreEqual(MapProviders.Get(MapProviders.EsriPublicId).UrlTemplate, sources.UrlTemplate("gone"));
		Assert.AreEqual("opentopomap", sources.Resolve("opentopomap"));
	}

	[TestMethod]
	public void Custom_TakesAKeyOnlyWhenItsUrlHasThePlaceholder_AndFallsBackWhenEmpty()
	{
		var plain = new MapSources(CustomUrlTemplate: "https://t.example/{z}/{x}/{y}.png");
		var keyed = new MapSources(new Dictionary<string, string> { [MapSources.CustomKeyGroup] = "k 1" }, "https://t.example/{z}/{x}/{y}.png?k={api_key}");

		Assert.IsNull(plain.KeyGroupOf(MapProviders.CustomId));
		Assert.AreEqual(MapSources.CustomKeyGroup, keyed.KeyGroupOf(MapProviders.CustomId));
		Assert.AreEqual("https://t.example/{z}/{x}/{y}.png?k=k%201", keyed.UrlTemplate(MapProviders.CustomId));
		Assert.AreEqual(MapProviders.Get(MapProviders.EsriPublicId).UrlTemplate, new MapSources().UrlTemplate(MapProviders.CustomId));
	}

	[TestMethod]
	public void Attribution_IsTheProvidersOwn_OrTheCustomServersGiven()
	{
		Assert.IsNull(new MapSources().Attribution(MapProviders.CustomId));
		Assert.AreEqual("my credit", new MapSources(CustomAttribution: " my credit ").Attribution(MapProviders.CustomId));
		StringAssert.Contains(new MapSources().Attribution("carto-positron"), "© OpenStreetMap contributors, © CARTO");
		StringAssert.Contains(new MapSources(RouteBounds: InPoland).Attribution(null), "Earthstar Geographics");
	}

	[TestMethod]
	public void CreditRule_IsTheProvidersTerms_OrTheUsersWordForACustomServer()
	{
		var sources = new MapSources(RouteBounds: InPoland);

		Assert.AreEqual(MapCreditRule.Briefly, sources.CreditRule(MapProviders.GugikId));
		Assert.AreEqual(MapCreditRule.WhileMapVisible, sources.CreditRule(MapProviders.EoxId));
		Assert.AreEqual(MapCreditRule.WhileMapVisible, sources.CreditRule("carto-dark"));
		Assert.AreEqual(MapCreditRule.WhileMapVisible, sources.CreditRule(MapProviders.CustomId), "unless the user says otherwise");
		Assert.AreEqual(MapCreditRule.Briefly, (sources with { CustomCreditBriefly = true }).CreditRule(MapProviders.CustomId));
	}

	[TestMethod]
	public void CacheMaxAge_FollowsTheTerms()
	{
		var sources = new MapSources();

		Assert.AreEqual(TimeSpan.FromDays(30), sources.CacheMaxAge("carto-positron"));
		Assert.IsNull(sources.CacheMaxAge("opentopomap"));
		Assert.AreEqual(TimeSpan.Zero, sources.CacheMaxAge("esri-imagery"), "never stored");
		Assert.IsNull(sources.CacheMaxAge(MapProviders.CustomId));
	}

	[TestMethod]
	public void EsriLabels_FollowTheSatelliteSelectionAndItsOwnKey()
	{
		var sources = new MapSources(new Dictionary<string, string> { [MapProviders.EsriKeyGroup] = " e &1 " });
		string? labels = sources.LabelsUrlTemplate(MapProviders.AutoSatelliteId);

		StringAssert.Contains(labels, "/arcgis/imagery/labels/static/tile/{hz}/{hy}/{hx}?token=e%20%261");
		Assert.AreEqual(labels, sources.LabelsUrlTemplate(MapProviders.EsriImageryId));
		Assert.IsNull(sources.LabelsUrlTemplate(MapProviders.StreetsAutoId));
		Assert.IsNull(sources.LabelsUrlTemplate(MapProviders.MapTilerSatelliteId));
		Assert.IsNull(sources.LabelsUrlTemplate(MapProviders.CustomId));
		Assert.IsNull((sources with { ShowEsriLabels = false }).LabelsUrlTemplate(null));
		Assert.IsNull(new MapSources().LabelsUrlTemplate(MapProviders.EsriImageryId), "a keyed provider never requests labels without its key");
		StringAssert.Contains(new MapSources().LabelsUrlTemplate(null), "/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}");
	}

	[TestMethod]
	public void EsriLabels_IncludeOnlyTheVisibleLayersInTheCredit()
	{
		var sources = new MapSources(new Dictionary<string, string> { [MapProviders.EsriKeyGroup] = "e" });
		string? hybrid = sources.Attribution(null);
		string? imagery = (sources with { ShowEsriLabels = false }).Attribution(null);

		StringAssert.Contains(hybrid, "Earthstar Geographics");
		StringAssert.Contains(hybrid, "TomTom");
		StringAssert.Contains(hybrid, "© OpenStreetMap contributors");
		StringAssert.Contains(imagery, "Earthstar Geographics");
		Assert.IsFalse(imagery!.Contains("TomTom"));
	}

	[TestMethod]
	public void EsriLabels_AreOnForExistingSettingsAndRememberAnOptOut()
	{
		OverlaySettings settings = JsonSerializer.Deserialize<OverlaySettings>("{}")!;
		Assert.IsTrue(settings.ShowEsriMapLabels);
		Assert.IsTrue(MapSources.From(settings).ShowEsriLabels);

		OverlaySettings restored = JsonSerializer.Deserialize<OverlaySettings>(JsonSerializer.Serialize(settings with { ShowEsriMapLabels = false }))!;
		Assert.IsFalse(restored.ShowEsriMapLabels);
		Assert.IsFalse(MapSources.From(restored).ShowEsriLabels);
	}

	[TestMethod]
	public async Task EsriLabels_ChangingTheSwitchInvalidatesBothPreparedMaps()
	{
		var sources = new MapSources(new Dictionary<string, string> { [MapProviders.EsriKeyGroup] = "e" });
		OverlayElement[] layout = [new MapWidgetElement { X = 0, Y = 0, Visible = true }];
		using var renderer = new OverlayRenderer(1280, 720, 0, layout, [], mapSources: sources,
			routeIntro: RouteIntroSettings.From(new OverlaySettings()));

		await renderer.BuildMapMosaicAsync();
		await renderer.BuildRouteIntroMosaicAsync();
		Assert.IsFalse(renderer.NeedsMapPrepare(layout));
		Assert.IsFalse(renderer.NeedsRouteIntroPrepare());

		renderer.MapSources = sources with { ShowEsriLabels = false };
		Assert.IsTrue(renderer.NeedsMapPrepare(layout));
		Assert.IsTrue(renderer.NeedsRouteIntroPrepare());
	}

	[TestMethod]
	public void EveryBuiltInProvider_HasItsTermsAndACredit()
	{
		foreach (MapProvider provider in MapProviders.BuiltIn)
		{
			Assert.IsFalse(string.IsNullOrWhiteSpace(provider.Attribution), provider.Id);
			StringAssert.StartsWith(provider.Terms.TermsUrl, "https://", provider.Id);
			Assert.IsTrue(new[] { "{z}", "{x}", "{y}" }.All(provider.UrlTemplate.Contains) || new[] { "{hz}", "{hx}", "{hy}" }.All(provider.UrlTemplate.Contains),
				provider.Id);
			Assert.AreEqual(provider.NeedsApiKey, provider.UrlTemplate.Contains("{api_key}"), provider.Id);
		}
	}
}
