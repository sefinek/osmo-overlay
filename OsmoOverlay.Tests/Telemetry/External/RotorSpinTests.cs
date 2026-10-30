using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class RotorSpinTests
{
	// 10 Hz for a minute.
	private static List<DerivedFrame> Frames(Func<int, double> speedKmh)
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i <= 600; i++)
		{
			var raw = new TelemetryFrame(i, i / 10.0, 50, 20, 0, null, 0, 0, 1, HasGpsFix: true);
			frames.Add(new DerivedFrame(raw, speedKmh(i), 0, 0, 0, 0, 0, default, 0, 0, 1, 0, 0));
		}

		return frames;
	}

	private static RotorSpin.RotorPose[] PosesAt(IReadOnlyList<DerivedFrame> frames, double[,] phases, double seconds, double reference)
	{
		RotorSpin.RotorPose[] poses = new RotorSpin.RotorPose[RotorSpin.Count];
		RotorSpin.At(frames, phases, seconds, reference, poses);
		return poses;
	}

	private static void Attitude(List<DerivedFrame> frames, DroneAttitude? attitude, double? climbMs = null)
	{
		for (int i = 0; i < frames.Count; i++)
			frames[i] = frames[i] with { Raw = frames[i].Raw with { DroneAttitude = attitude, VerticalSpeedMs = climbMs } };
	}

	[TestMethod]
	public void Rotors_TurnFasterTheFasterTheAircraftGoes()
	{
		List<DerivedFrame> hovering = Frames(_ => 0);
		List<DerivedFrame> flying = Frames(_ => 40);

		double slow = RotorSpin.Phases(hovering, 40)[600, 0];
		double fast = RotorSpin.Phases(flying, 40)[600, 0];

		Assert.AreEqual(60 * RotorSpin.IdleRevolutionsPerSecond, slow, 1e-6);
		Assert.AreEqual(60 * RotorSpin.TopRevolutionsPerSecond, fast, 1e-6);
	}

	[TestMethod]
	public void TheTurn_NeverGoesBackAndStartsAtZero()
	{
		List<DerivedFrame> frames = Frames(i => i % 100);
		Attitude(frames, new DroneAttitude(25, -25, 0), -5);
		double[,] phases = RotorSpin.Phases(frames, 60);

		for (int rotor = 0; rotor < RotorSpin.Count; rotor++)
		{
			Assert.AreEqual(0, phases[0, rotor]);
			for (int i = 1; i < frames.Count; i++) Assert.IsGreaterThan(phases[i - 1, rotor], phases[i, rotor]);
		}
	}

	[TestMethod]
	public void ANoseUp_SpeedsTheFrontPairAndSlowsTheRear()
	{
		List<DerivedFrame> frames = Frames(_ => 20);
		Attitude(frames, new DroneAttitude(15, 0, 0));

		RotorSpin.RotorPose[] poses = PosesAt(frames, RotorSpin.Phases(frames, 40), 30, 40);

		double level = RotorSpin.RevolutionsPerSecond(0.5);
		Assert.IsGreaterThan(level, poses[0].RevolutionsPerSecond);
		Assert.IsGreaterThan(level, poses[1].RevolutionsPerSecond);
		Assert.IsLessThan(level, poses[2].RevolutionsPerSecond);
		Assert.IsLessThan(level, poses[3].RevolutionsPerSecond);
	}

	[TestMethod]
	public void ABankToTheRight_SpeedsTheLeftPairAndSlowsTheRight()
	{
		List<DerivedFrame> frames = Frames(_ => 20);
		Attitude(frames, new DroneAttitude(0, 15, 0));

		RotorSpin.RotorPose[] poses = PosesAt(frames, RotorSpin.Phases(frames, 40), 30, 40);

		Assert.IsGreaterThan(poses[1].RevolutionsPerSecond, poses[0].RevolutionsPerSecond);
		Assert.IsGreaterThan(poses[3].RevolutionsPerSecond, poses[2].RevolutionsPerSecond);
	}

	[TestMethod]
	public void AClimbSpeedsAllFourAndADescentSlowsThem()
	{
		List<DerivedFrame> climbing = Frames(_ => 20);
		List<DerivedFrame> descending = Frames(_ => 20);
		Attitude(climbing, new DroneAttitude(0, 0, 0), 3);
		Attitude(descending, new DroneAttitude(0, 0, 0), -3);
		double level = RotorSpin.RevolutionsPerSecond(0.5);

		RotorSpin.RotorPose[] up = PosesAt(climbing, RotorSpin.Phases(climbing, 40), 30, 40);
		RotorSpin.RotorPose[] down = PosesAt(descending, RotorSpin.Phases(descending, 40), 30, 40);

		Assert.IsTrue(up.All(p => p.RevolutionsPerSecond > level));
		Assert.IsTrue(down.All(p => p.RevolutionsPerSecond < level));
	}

	[TestMethod]
	public void Tilt_CannotMakeARotorStopOrRunAway()
	{
		List<DerivedFrame> frames = Frames(_ => 40);
		Attitude(frames, new DroneAttitude(80, -80, 0), 50);

		RotorSpin.RotorPose[] poses = PosesAt(frames, RotorSpin.Phases(frames, 40), 30, 40);

		Assert.IsTrue(poses.All(p => p.RevolutionsPerSecond > 0 && p.RevolutionsPerSecond < RotorSpin.TopRevolutionsPerSecond * 2));
	}

	[TestMethod]
	public void TheSameTimeGivesTheSameTurn_WhateverWasAskedBefore()
	{
		List<DerivedFrame> frames = Frames(i => i % 100);
		Attitude(frames, new DroneAttitude(10, 5, 0));
		double[,] phases = RotorSpin.Phases(frames, 60);

		RotorSpin.RotorPose[] first = PosesAt(frames, phases, 33.33, 60);
		PosesAt(frames, phases, 5, 60);
		PosesAt(frames, phases, 58, 60);

		CollectionAssert.AreEqual(first, PosesAt(frames, phases, 33.33, 60));
	}

	[TestMethod]
	public void BetweenTwoFrames_TheTurnIsCarriedSmoothly()
	{
		List<DerivedFrame> frames = Frames(_ => 40);
		double[,] phases = RotorSpin.Phases(frames, 40);

		double start = PosesAt(frames, phases, 30.0, 40)[0].Revolutions;
		double half = PosesAt(frames, phases, 30.05, 40)[0].Revolutions;

		Assert.AreEqual(RotorSpin.TopRevolutionsPerSecond * 0.05, half - start, 1e-9);
	}

	[TestMethod]
	public void WithNoFrames_ThereIsNoTurn()
	{
		Assert.IsTrue(PosesAt([], new double[0, RotorSpin.Count], 5, 40).All(p => p == default));
		Assert.AreEqual(0, RotorSpin.Phases([], 40).Length);
	}

	[TestMethod]
	public void ASpeedWithNoNumber_CountsAsStandingStill()
	{
		Assert.AreEqual(0, RotorSpin.Ratio(double.NaN, 40));
		Assert.AreEqual(1, RotorSpin.Ratio(400, 40));
	}
}
