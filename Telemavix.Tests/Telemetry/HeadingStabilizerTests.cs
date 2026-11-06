using Telemavix.Core.Telemetry;

namespace Telemavix.Tests.Telemetry;

/// <summary>The heading the map turns by, steadied against GPS noise (HeadingStabilizer).</summary>
[TestClass]
public sealed class HeadingStabilizerTests
{
	private const double Hz = 10;

	private static TelemetryFrame[] Frames(int count, Func<int, bool>? gap = null, Func<int, bool>? fix = null)
	{
		return [.. Enumerable.Range(0, count).Select(i =>
			new TelemetryFrame(i, i / Hz, 50, 20, 200, null, 0, 1, 0, HasGpsFix: fix?.Invoke(i) ?? true, StartsAfterGap: gap?.Invoke(i) ?? false))];
	}

	private static double[] Compute(TelemetryFrame[] frames, Func<int, double> speed, Func<int, double> heading)
	{
		return HeadingStabilizer.Compute(frames, [.. frames.Select((_, i) => speed(i))], [.. frames.Select((_, i) => heading(i))]);
	}

	private static double Diff(double a, double b)
	{
		return Math.Abs((a - b + 540) % 360 - 180);
	}

	[TestMethod]
	public void Standing_KeepsTheLastReliableDirection_NotTheNoise()
	{
		TelemetryFrame[] frames = Frames(300);
		double[] result = Compute(frames, i => i < 100 ? 30 : 0, i => i < 100 ? 90 : i * 37 % 360);

		for (int i = 120; i < 300; i++) Assert.AreEqual(90, result[i], 1.0, $"frame {i}");
	}

	[TestMethod]
	public void SetsOffSmoothly_TowardsTheNewDirection()
	{
		TelemetryFrame[] frames = Frames(500);
		double[] result = Compute(frames, i => i is >= 50 and < 100 ? 0 : 30, i => i < 100 ? 90 : 150);

		Assert.AreEqual(90, result[99], 1e-6);
		Assert.IsTrue(result[100] > 90 && result[100] < 130, "the way back is not a snap");
		Assert.IsTrue(Diff(result[499], 150) < 1);
		for (int i = 100; i < 500; i++) Assert.IsTrue(result[i] >= result[i - 1] - 1e-9, "monotonic");
	}

	[TestMethod]
	public void ShortJump_IsIgnored_ALastingTurnIsFollowed()
	{
		TelemetryFrame[] frames = Frames(300);
		double[] spike = Compute(frames, _ => 40, i => i is >= 100 and < 105 ? 270 : 90);
		Assert.IsTrue(Enumerable.Range(100, 200).All(i => Diff(spike[i], 90) < 1), "half a second of 180 degrees is noise");

		double[] turn = Compute(frames, _ => 40, i => i < 100 ? 90 : 270);
		Assert.IsTrue(Diff(turn[299], 270) < 2, "a direction that stays is the new one");
		Assert.IsTrue(Diff(turn[105], 90) < 1, "but it is waited for");
	}

	[TestMethod]
	public void CrossingNorth_TakesTheShortWay()
	{
		TelemetryFrame[] frames = Frames(200);
		double[] result = Compute(frames, _ => 40, i => i < 50 ? 350 : 10);

		for (int i = 50; i < 200; i++) Assert.IsTrue(result[i] >= 349 || result[i] <= 11, $"frame {i}: {result[i]}");
		Assert.IsTrue(Diff(result[199], 10) < 1);
	}

	[TestMethod]
	public void StartOfRecording_TakesTheFirstReliableDirection()
	{
		TelemetryFrame[] frames = Frames(100);
		double[] result = Compute(frames, i => i < 30 ? 0 : 20, i => i < 30 ? 5 : 200);

		for (int i = 0; i < 30; i++) Assert.AreEqual(200, result[i], 1e-9);
	}

	[TestMethod]
	public void NoReliableDirectionAtAll_FallsBackToTheMeasuredOne()
	{
		TelemetryFrame[] frames = Frames(20);
		double[] result = Compute(frames, _ => 0, i => i * 5);

		for (int i = 0; i < 20; i++) Assert.AreEqual(i * 5, result[i], 1e-9);
	}

	[TestMethod]
	public void NoFix_HoldsTheDirection_AndAGapStartsOver()
	{
		TelemetryFrame[] frames = Frames(200, gap: i => i == 100, fix: i => i is < 40 or >= 60);
		double[] result = Compute(frames, _ => 40, i => i < 100 ? 90 : 180);

		for (int i = 40; i < 60; i++) Assert.AreEqual(90, result[i], 1e-9, "held without a fix");
		Assert.AreEqual(180, result[100], 1e-9, "nothing is carried across a gap between files");
	}

	[TestMethod]
	public void SameResult_WhateverTheOrderItIsAskedIn()
	{
		TelemetryFrame[] frames = Frames(200);
		Func<int, double> heading = i => (i * 3) % 360;
		Assert.IsTrue(Compute(frames, _ => 25, heading).SequenceEqual(Compute(frames, _ => 25, heading)));
	}

	[TestMethod]
	public void AfterAHold_TheNewDirectionIsTurnedToAtALimitedPace()
	{
		TelemetryFrame[] frames = Frames(400);
		double[] result = Compute(frames, i => i is >= 50 and < 100 ? 0 : 40, i => i < 100 ? 90 : 180);

		for (int i = 101; i < 400; i++) Assert.IsTrue(Diff(result[i], result[i - 1]) <= 18.0001, $"frame {i}: a step of {Diff(result[i], result[i - 1])} degrees");
		Assert.IsTrue(Diff(result[399], 180) < 1);
	}

	[TestMethod]
	public void AFlightLogs_DirectionIsTrustedAtAWalkingPace_ACamerasIsNot()
	{
		TelemetryFrame[] camera = Frames(200);
		TelemetryFrame[] aircraft = [.. camera.Select(f => f with { DroneAttitude = new DroneAttitude(0, 0, 0) })];

		double[] slowCamera = Compute(camera, i => i < 50 ? 30 : 2.5, i => i < 50 ? 90 : 180);
		double[] slowAircraft = Compute(aircraft, i => i < 50 ? 30 : 2.5, i => i < 50 ? 90 : 180);

		Assert.IsTrue(Diff(slowCamera[199], 90) < 1, "a camera at 2.5 km/h keeps the last reliable direction");
		Assert.IsTrue(Diff(slowAircraft[199], 180) < 5, "an aircraft at 2.5 km/h follows");
	}
}
