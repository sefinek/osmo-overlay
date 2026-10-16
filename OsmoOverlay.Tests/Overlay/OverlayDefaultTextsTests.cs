using System.Globalization;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

/// <summary>The widgets' built-in texts follow the interface's language; a text of the user's never does.</summary>
[TestClass]
public sealed class OverlayDefaultTextsTests
{
	private static T In<T>(string language, Func<T> read)
	{
		CultureInfo before = CultureInfo.CurrentUICulture;
		CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
		try
		{
			return read();
		}
		finally
		{
			CultureInfo.CurrentUICulture = before;
		}
	}

	[TestMethod]
	public void BuiltInTexts_FollowTheInterfaceLanguage()
	{
		Assert.AreEqual("FINISH", In("en", () => OverlayRenderer.DefaultTripArrivedLabel));
		Assert.AreEqual("META", In("pl", () => OverlayRenderer.DefaultTripArrivedLabel));
		Assert.AreEqual("Elevation", In("en", () => OverlayRenderer.DefaultLabel(new ElevationElement { X = 0, Y = 0 })));
		Assert.AreEqual("Wysokość", In("pl", () => OverlayRenderer.DefaultLabel(new ElevationElement { X = 0, Y = 0 })));
		Assert.AreEqual("DISTANCE", In("en", () => RouteIntroStat.DefaultLabel(RouteIntroStats.Distance)));
		Assert.AreEqual("DYSTANS", In("pl", () => RouteIntroStat.DefaultLabel(RouteIntroStats.Distance)));
	}

	[TestMethod]
	public void UsersOwnText_IsKeptInEveryLanguage()
	{
		var texts = new GpsAlertTexts().With(GpsAlertMessage.Lost, new GpsAlertText("Lost it", "Held"));

		Assert.AreEqual("LOST IT", In("pl", () => OverlayRenderer.GpsAlertLines(texts).Titles[(int)GpsAlertMessage.Lost]));
	}

	[TestMethod]
	[DataRow("en")]
	[DataRow("pl")]
	public void TripStatBox_FitsEveryBuiltInCaption(string language)
	{
		In(language, () =>
		{
			float fitted = OverlayElementBounds.GetBounds(OverlayElementType.TripStat, 0, 0, 1).Width;
			foreach (TripStatKind stat in Enum.GetValues<TripStatKind>())
			{
				float own = OverlayElementBounds.GetBounds(OverlayElementType.TripStat, 0, 0, 1, label: OverlayRenderer.DefaultTripStatLabel(stat)).Width;
				Assert.IsTrue(fitted >= own - 0.01f, $"{stat}: {own} > {fitted}");
			}

			return 0;
		});
	}
}
