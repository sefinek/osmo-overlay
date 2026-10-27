using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class TelemetryReportTests
{
	private static DerivedFrame Frame(int i, double roll = 0, double pitch = 0, bool fix = true, double accelZ = 1, double speed = 10)
	{
		var raw = new TelemetryFrame(i, i / 10.0, 50, 19, 100, i < 5 ? null : new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0, 0, accelZ, HasGpsFix: fix);
		return new DerivedFrame(raw, speed, 0, 0, 0, roll, pitch, default, 0, 0, 1, 0, 0);
	}

	private static List<DerivedFrame> Frames(int count, double roll = 0, double pitch = 0)
	{
		return [.. Enumerable.Range(0, count).Select(i => Frame(i, roll, pitch))];
	}

	[TestMethod]
	public void Create_CountsFramesTimeAndRate()
	{
		var report = TelemetryReport.Create(Frames(101), true);

		Assert.AreEqual(101, report.Frames);
		Assert.AreEqual(10, report.DurationSeconds, 1e-9);
		Assert.AreEqual(10, report.SampleRateHz, 1e-9);
	}

	[TestMethod]
	public void Create_MeasuresTheGpsFixAndItsLosses()
	{
		List<DerivedFrame> frames = [.. Enumerable.Range(0, 20).Select(i => Frame(i, fix: i is < 4 or >= 14))];

		var report = TelemetryReport.Create(frames, true);

		Assert.AreEqual(10 / 20.0, report.GpsFixShare, 1e-9);
		Assert.AreEqual(1, report.GpsLossStretches);
		Assert.AreEqual(0.9, report.LongestGpsLossSeconds, 1e-9);
		Assert.IsTrue(report.HasGpsTime);
	}

	[TestMethod]
	public void Create_NoTimestampsMeansNoGpsTime()
	{
		Assert.IsFalse(TelemetryReport.Create(Frames(3), true).HasGpsTime);
	}

	[TestMethod]
	public void Gravity_AtRestReadsAboutOneG()
	{
		Assert.IsTrue(TelemetryReport.Create(Frames(20), true).GravityLooksRight);
		Assert.IsFalse(TelemetryReport.Create([.. Enumerable.Range(0, 20).Select(i => Frame(i, accelZ: 9.81))], true).GravityLooksRight);
	}

	[TestMethod]
	public void CheckLean_RollSignTellsRightFromLeft()
	{
		var right = TelemetryReport.Create(Frames(30, roll: 25), true);

		Assert.IsTrue(right.CheckLean(Lean.Right));
		Assert.IsFalse(right.CheckLean(Lean.Left), "a wrong sign shows as the other lean");
		Assert.IsTrue(TelemetryReport.Create(Frames(30, roll: -25), true).CheckLean(Lean.Left));
	}

	[TestMethod]
	public void CheckLean_PitchSignTellsNoseUpFromNoseDown()
	{
		Assert.IsTrue(TelemetryReport.Create(Frames(30, pitch: 40), true).CheckLean(Lean.NoseUp));
		Assert.IsTrue(TelemetryReport.Create(Frames(30, pitch: -40), true).CheckLean(Lean.NoseDown));
		Assert.IsFalse(TelemetryReport.Create(Frames(30, pitch: -40), true).CheckLean(Lean.NoseUp));
	}

	[TestMethod]
	public void CheckLean_ALevelCameraLeansNoWay()
	{
		var level = TelemetryReport.Create(Frames(30, roll: 2, pitch: -1), true);

		Assert.IsFalse(level.CheckLean(Lean.Right));
		Assert.IsFalse(level.CheckLean(Lean.NoseDown));
	}

	[TestMethod]
	public void CheckLean_IsAMedianSoABriefJoltDoesNotDecideIt()
	{
		List<DerivedFrame> frames = Frames(30, roll: 25);
		frames[10] = Frame(10, roll: -80);

		Assert.IsTrue(TelemetryReport.Create(frames, true).CheckLean(Lean.Right));
	}

	[TestMethod]
	public void CheckLean_NeverPassesWhenTheCamerasAxesAreUnknown()
	{
		Assert.IsFalse(TelemetryReport.Create(Frames(30, roll: 25), false).CheckLean(Lean.Right));
		StringAssert.Contains(TelemetryReport.Create(Frames(30), false).ToText(), "unknown");
	}

	[TestMethod]
	[DataRow("right", Lean.Right)]
	[DataRow("LEFT", Lean.Left)]
	[DataRow("nose-up", Lean.NoseUp)]
	[DataRow("nose_down", Lean.NoseDown)]
	[DataRow("up", Lean.NoseUp)]
	public void ParseLean_TakesTheCommandLineNames(string text, Lean expected)
	{
		Assert.AreEqual(expected, TelemetryReport.ParseLean(text));
	}

	[TestMethod]
	public void ParseLean_RefusesOthers()
	{
		Assert.IsNull(TelemetryReport.ParseLean("sideways"));
	}

	[TestMethod]
	public void ToText_WarnsAboutAnAccelerometerFarFromOneG()
	{
		string text = TelemetryReport.Create([.. Enumerable.Range(0, 10).Select(i => Frame(i, accelZ: 9.81))], true).ToText();

		StringAssert.Contains(text, "scale may be wrong");
		StringAssert.Contains(text, "Roll");
	}

	[TestMethod]
	public void Create_RefusesNoFrames()
	{
		Assert.ThrowsExactly<ArgumentException>(() => TelemetryReport.Create([], true));
	}
}
