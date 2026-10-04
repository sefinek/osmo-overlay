using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class RouteIntroSettingsTests
{
	[TestMethod]
	public void ApplyTo_IsTheReverseOfFrom_AndLeavesTheRestAlone()
	{
		var routeIntro = new RouteIntroSettings(false, 7.5, RouteIntroStats.MaxSpeed | RouteIntroStats.MaxLean, UnitSystem.Imperial, true, "carto-dark",
			false, "Początek", "", RouteIntroLabels.From(new Dictionary<string, string> { [nameof(RouteIntroStats.MaxLean)] = "MAKS. PRZECHYŁ" }));
		var settings = new OverlaySettings(ActivePresetId: "p1", SpeedCorrectionPercent: 4.2);

		OverlaySettings applied = routeIntro.ApplyTo(settings);

		Assert.AreEqual(routeIntro, RouteIntroSettings.From(applied));
		Assert.AreEqual(settings, RouteIntroSettings.From(settings).ApplyTo(applied));
	}

	[TestMethod]
	public void Labels_AreTheUsersOwn_ElseTheDefault_AndCompareByContent()
	{
		var labels = RouteIntroLabels.From(new Dictionary<string, string>
		{
			[nameof(RouteIntroStats.Distance)] = " DYSTANS ",
			[nameof(RouteIntroStats.Date)] = "  "
		});

		Assert.AreEqual("DYSTANS", labels.For(RouteIntroStats.Distance));
		Assert.AreEqual("DATE", labels.For(RouteIntroStats.Date), "a blank caption keeps the default");
		Assert.AreEqual(labels, RouteIntroLabels.From(new Dictionary<string, string> { [nameof(RouteIntroStats.Distance)] = "DYSTANS" }));
		Assert.IsNull(RouteIntroLabels.None.ToSettings());
	}

	[TestMethod]
	public void All_IsEveryStatOnce_InTheCardsOrder()
	{
		Assert.AreEqual(RouteIntroStats.Date, RouteIntroStat.All[0]);
		Assert.AreEqual(RouteIntroStat.All.Length, RouteIntroStat.All.Distinct().Count());
		Assert.AreEqual(Enum.GetValues<RouteIntroStats>().Length - 2, RouteIntroStat.All.Length, "every stat but None and Default");
		Assert.IsFalse(RouteIntroStat.All.Contains(RouteIntroStats.Default));
		Assert.IsTrue(RouteIntroStat.All.All(s => RouteIntroStat.DefaultLabel(s) == RouteIntroStat.DefaultLabel(s).ToUpperInvariant()));
	}
}
