using OsmoOverlay.Cameras.Dji;

namespace OsmoOverlay.Tests.Cameras.Dji;

[TestClass]
public sealed class GpsForwardFillTests
{
	[TestMethod]
	public void MissingFix_HoldsLastKnownPosition()
	{
		var fill = new GpsForwardFill();

		fill.Apply(50, 20, 300);
		(double lat, double lon, double alt, bool hasFix) = fill.Apply(null, null, null);

		Assert.AreEqual((50.0, 20.0, 300.0, false), (lat, lon, alt, hasFix));
	}

	[TestMethod]
	public void AltitudeWithoutPosition_UpdatesAltitudeOnly()
	{
		var fill = new GpsForwardFill();

		fill.Apply(50, 20, 300);
		(double lat, _, double alt, bool hasFix) = fill.Apply(null, null, 310);

		Assert.AreEqual(50.0, lat);
		Assert.AreEqual(310.0, alt);
		Assert.IsFalse(hasFix);
	}

	[TestMethod]
	public void NoFixYet_IsZeroButFlaggedAsNoFix()
	{
		(double lat, double lon, _, bool hasFix) = new GpsForwardFill().Apply(null, null, null);

		Assert.AreEqual((0.0, 0.0, false), (lat, lon, hasFix));
	}
}
