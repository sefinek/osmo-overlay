using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class DroneSticksBetweenTests
{
	private static readonly DateTime Start = new(2026, 11, 4, 11, 0, 0, DateTimeKind.Utc);

	private static ExternalTrack Track(params (double Seconds, double Aileron)[] readings)
	{
		return new ExternalTrack([new ExternalTrackPoint(Start, 50, 20), new ExternalTrackPoint(Start.AddSeconds(10), 50.001, 20)],
			droneSticks: readings.Select(r => new ExternalDroneSticksReading(Start.AddSeconds(r.Seconds), new DroneSticks(r.Aileron, 0, 0, 0))));
	}

	[TestMethod]
	public void BetweenTwoReadings_TheSticksAreCarriedBetweenThem()
	{
		ExternalTrack track = Track((1, 0), (1.1, 1));

		Assert.AreEqual(0.5, track.DroneSticksBetween(Start.AddSeconds(1.05))!.Aileron, 1e-9);
		Assert.AreEqual(1, track.DroneSticksBetween(Start.AddSeconds(5))!.Aileron, 1e-9, "after the last one it is held");
		Assert.IsNull(track.DroneSticksBetween(Start.AddSeconds(0.5)), "before the first there is none");
	}

	[TestMethod]
	public void WhereTheLogWroteNothingForAWhile_TheLastReadingIsHeld()
	{
		ExternalTrack track = Track((1, 0), (3, 1));

		Assert.AreEqual(0, track.DroneSticksBetween(Start.AddSeconds(2))!.Aileron, 1e-9);
	}
}
