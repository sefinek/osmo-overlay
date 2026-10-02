using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class RouteIntroSettingsTests
{
	[TestMethod]
	public void ApplyTo_IsTheReverseOfFrom_AndLeavesTheRestAlone()
	{
		var routeIntro = new RouteIntroSettings(false, 7.5, false, true, false, true, false, true, false, UnitSystem.Imperial, true, "carto-dark");
		var settings = new OverlaySettings(ActivePresetId: "p1", SpeedCorrectionPercent: 4.2);

		OverlaySettings applied = routeIntro.ApplyTo(settings);

		Assert.AreEqual(routeIntro, RouteIntroSettings.From(applied));
		Assert.AreEqual(settings, RouteIntroSettings.From(settings).ApplyTo(applied));
	}
}
