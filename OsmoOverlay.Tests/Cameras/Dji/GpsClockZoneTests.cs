using OsmoOverlay.Cameras.Dji;

namespace OsmoOverlay.Tests.Cameras.Dji;

[TestClass]
public sealed class GpsClockZoneTests
{
	private static readonly DateTime Camera = new(2026, 9, 30, 13, 27, 43, DateTimeKind.Utc);

	[TestMethod]
	public void AWholeHourOff_IsMovedOntoTheCameraClock()
	{
		// As on a real Osmo Action 6 recording: the GPS time 60 min behind, give or take the clock's few seconds.
		Assert.AreEqual(TimeSpan.FromHours(1), GpsClockZone.Shift(new DateTime(2026, 9, 30, 12, 27, 49, 466), Camera));
		Assert.AreEqual(TimeSpan.FromHours(-2), GpsClockZone.Shift(Camera.AddHours(2).AddSeconds(-40), Camera));
	}

	[TestMethod]
	public void AQuarterHourZone_IsMovedToo()
	{
		Assert.AreEqual(new TimeSpan(5, 45, 0), GpsClockZone.Shift(Camera - new TimeSpan(5, 45, 30), Camera));
	}

	[TestMethod]
	[DataRow(6.0)]
	[DataRow(-90.0)]
	[DataRow(3 * 3600 + 300.0)]
	[DataRow(400 * 86400.0)]
	public void ACameraClockMerelyDriftingOrNeverSet_LeavesTheGpsTime(double secondsOff)
	{
		Assert.IsNull(GpsClockZone.Shift(Camera.AddSeconds(-secondsOff), Camera));
	}

	[TestMethod]
	public void WithoutEitherTime_NothingIsMoved()
	{
		Assert.IsNull(GpsClockZone.Shift(null, Camera));
		Assert.IsNull(GpsClockZone.Shift(Camera, null));
	}
}
