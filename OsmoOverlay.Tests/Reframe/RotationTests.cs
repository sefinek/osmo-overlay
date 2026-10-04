using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Tests.Reframe;

[TestClass]
public sealed class RotationTests
{
	private static readonly Direction Forward = new(0, 0, 1);
	private static readonly Direction Down = new(0, 1, 0);

	private static void AreClose(Direction expected, Direction actual, string? message = null)
	{
		Assert.AreEqual(expected.X, actual.X, 1e-9, message);
		Assert.AreEqual(expected.Y, actual.Y, 1e-9, message);
		Assert.AreEqual(expected.Z, actual.Z, 1e-9, message);
	}

	[TestMethod]
	public void FromView_FollowsV360()
	{
		// x right, y down: yaw turns the view right, pitch up.
		AreClose(new Direction(1, 0, 0), Rotation.FromView(new ReframeView(Yaw: 90)).Apply(Forward), "yaw 90 looks right");
		AreClose(new Direction(0, -1, 0), Rotation.FromView(new ReframeView(Pitch: 90)).Apply(Forward), "pitch 90 looks up");
		AreClose(Forward, Rotation.FromView(new ReframeView(Roll: 40)).Apply(Forward), "roll keeps the direction");
	}

	[TestMethod]
	public void FromView_AppliesRollFirstThenPitchThenYaw()
	{
		var view = new ReframeView(30, 20, 10);
		Rotation composed = Rotation.FromView(new ReframeView(Yaw: 30)) * Rotation.FromView(new ReframeView(Pitch: 20)) *
		                    Rotation.FromView(new ReframeView(Roll: 10));
		var probe = new Direction(0.3, -0.2, 0.9);

		AreClose(composed.Apply(probe), Rotation.FromView(view).Apply(probe));
	}

	[TestMethod]
	public void Leveling_TurnsTheWorldsDownIntoTheMeasuredOne()
	{
		foreach (Direction down in new[] { new Direction(0.4, 0.8, -0.3), new Direction(-1, 0, 0), new Direction(0, 0, 1), Down })
			AreClose(down.Normalized(), Rotation.Leveling(down).Apply(Down), $"down {down}");

		AreClose(new Direction(0, -1, 0), Rotation.Leveling(new Direction(0, -1, 0)).Apply(Down), "upside down");
	}

	[TestMethod]
	public void Leveling_KeepsLengths()
	{
		var r = Rotation.Leveling(new Direction(0.4, 0.8, -0.3));
		var probe = new Direction(0.2, -0.7, 0.1);

		Assert.AreEqual(probe.Length, r.Apply(probe).Length, 1e-9);
	}
}
