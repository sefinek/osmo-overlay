using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Mapping;

[TestClass]
public sealed class ExportMapsTests
{
	[TestMethod]
	[DataRow(MapUse.Allowed, MapUse.Allowed, MapUse.Allowed)]
	[DataRow(MapUse.Allowed, MapUse.NotStated, MapUse.NotStated)]
	[DataRow(MapUse.NotStated, MapUse.Allowed, MapUse.NotStated)]
	[DataRow(MapUse.PaidPlanOnly, MapUse.NotAllowed, MapUse.NotAllowed)]
	[DataRow(MapUse.NotAllowed, MapUse.PaidPlanOnly, MapUse.NotAllowed)]
	[DataRow(MapUse.PaidPlanOnly, MapUse.NotStated, MapUse.NotStated)]
	[DataRow(MapUse.NotStated, MapUse.NotAllowed, MapUse.NotAllowed)]
	[DataRow(MapUse.PaidPlanOnly, MapUse.Conditional, MapUse.PaidPlanOnly)]
	[DataRow(MapUse.Conditional, MapUse.PaidPlanOnly, MapUse.PaidPlanOnly)]
	[DataRow(MapUse.Allowed, MapUse.Conditional, MapUse.Conditional)]
	public void CommercialVideo_RequiresBothPermissions(MapUse commercial, MapUse video, MapUse expected)
	{
		var terms = new MapTerms(commercial, video, MapCreditRule.WhileMapVisible, "https://example.com/terms");

		Assert.AreEqual(expected, terms.CommercialVideo);
	}

	[TestMethod]
	[DataRow("opentopomap")]
	[DataRow("thunderforest-cycle")]
	[DataRow("thunderforest-outdoors")]
	[DataRow("thunderforest-landscape")]
	[DataRow("thunderforest-atlas")]
	public void ShareAlikeMaps_ReportTheirVideoConditions(string providerId)
	{
		MapProvider provider = MapProviders.Get(providerId);

		Assert.AreEqual(providerId, provider.Id);
		Assert.IsTrue(provider.Terms.ShareAlike);
		Assert.AreEqual(MapUse.Conditional, provider.Terms.Video);
		Assert.AreEqual(MapUse.Allowed, provider.Terms.Commercial);
	}

	[TestMethod]
	public void NoVisibleMap_NoMaps()
	{
		OverlayElement[] layout = [new MapWidgetElement { X = 0, Y = 0, Visible = false }];

		Assert.IsEmpty(ExportMaps.Used(layout, new OverlaySettings(ShowRouteIntro: false)));
	}

	[TestMethod]
	public void VisibleMaps_AreResolvedAndDistinct()
	{
		OverlayElement[] layout =
		[
			new MapWidgetElement { X = 0, Y = 0, Visible = true, MapProviderId = MapProviders.GugikId },
			new MapWidgetElement { X = 0, Y = 0, Visible = true, MapProviderId = MapProviders.GugikId },
			new MapWidgetElement { X = 0, Y = 0, Visible = true, MapProviderId = null }
		];

		IReadOnlyList<ExportMap> maps = ExportMaps.Used(layout, new OverlaySettings(ShowRouteIntro: false));

		CollectionAssert.AreEqual(new[] { MapProviders.GugikId, MapProviders.EsriPublicId }, maps.Select(m => m.ProviderId).ToArray());
	}

	[TestMethod]
	public void EsriKey_ResolvesTheDefaultToTheKeyedService()
	{
		var settings = new OverlaySettings(ShowRouteIntro: false) { MapApiKeys = new Dictionary<string, string> { [MapProviders.EsriKeyGroup] = "key" } };

		ExportMap map = ExportMaps.Used([new MapWidgetElement { X = 0, Y = 0, Visible = true }], settings).Single();

		Assert.AreEqual(MapProviders.EsriImageryId, map.ProviderId);
	}

	[TestMethod]
	public void RouteIntro_AddsItsOwnProviderOnlyWhenOn()
	{
		var off = new OverlaySettings(ShowRouteIntro: false) { ShowRouteIntro = false, RouteIntroMapProvider = MapProviders.EoxId };
		var on = off with { ShowRouteIntro = true };

		Assert.IsEmpty(ExportMaps.Used(null, off));
		Assert.AreEqual(MapProviders.EoxId, ExportMaps.Used(null, on).Single().ProviderId);
	}

	[TestMethod]
	public void CustomSource_HasNoProviderTerms()
	{
		ExportMap map = ExportMaps.Used([new MapWidgetElement { X = 0, Y = 0, Visible = true, MapProviderId = MapProviders.CustomId }], new OverlaySettings(ShowRouteIntro: false)).Single();

		Assert.IsNull(map.Provider);
	}
}
