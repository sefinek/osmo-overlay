using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class ProjectFingerprintTests
{
	private static OverlayProject Project()
	{
		return new OverlayProject(["a.mp4"], "out.mp4", [new FrameRange(10, 20)], null, 5, null, null);
	}

	[TestMethod]
	public void MovingThePlayheadIsNotAnEdit()
	{
		Assert.AreEqual(Project().Fingerprint(), (Project() with { PreviewFrame = 9000 }).Fingerprint());
	}

	[TestMethod]
	public void EveryKindOfEditChangesIt()
	{
		string before = Project().Fingerprint();

		Assert.AreNotEqual(before, (Project() with { OutputPath = "other.mp4" }).Fingerprint());
		Assert.AreNotEqual(before, (Project() with { Cuts = [new FrameRange(10, 21)] }).Fingerprint());
		Assert.AreNotEqual(before, (Project() with { InputPaths = ["a.mp4", "b.mp4"] }).Fingerprint());
		Assert.AreNotEqual(before, (Project() with { Preset = OverlayPreset.CreateDefault("p", "P") }).Fingerprint());
		Assert.AreNotEqual(before, (Project() with { Volume = [new VolumePoint(1, -6)] }).Fingerprint());
		Assert.AreNotEqual(before, (Project() with { SettingsPresetId = "bike" }).Fingerprint());
		Assert.AreNotEqual(before, (Project() with { Moments = [new KeyMoment(100, "Peak")] }).Fingerprint());
	}

	[TestMethod]
	public void TheSameWorkHasTheSameFingerprint()
	{
		Assert.AreEqual(Project().Fingerprint(), Project().Fingerprint());
	}
}
