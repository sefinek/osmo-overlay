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
	public void AltitudeWithoutPosition_IsIgnored()
	{
		var fill = new GpsForwardFill();

		fill.Apply(50, 20, 300);
		(double lat, _, double alt, bool hasFix) = fill.Apply(null, null, 0);

		Assert.AreEqual(50.0, lat);
		Assert.AreEqual(300.0, alt, "the camera writes 0 m without a fix - not a descent to sea level");
		Assert.IsFalse(hasFix);
	}

	[TestMethod]
	public void FixWithoutAltitude_KeepsTheLastAltitude()
	{
		var fill = new GpsForwardFill();

		fill.Apply(50, 20, 300);
		(_, _, double alt, bool hasFix) = fill.Apply(50.001, 20, null);

		Assert.AreEqual(300.0, alt);
		Assert.IsTrue(hasFix);
	}

	[TestMethod]
	public void NoFixYet_IsZeroButFlaggedAsNoFix()
	{
		(double lat, double lon, _, bool hasFix) = new GpsForwardFill().Apply(null, null, null);

		Assert.AreEqual((0.0, 0.0, false), (lat, lon, hasFix));
	}
}
