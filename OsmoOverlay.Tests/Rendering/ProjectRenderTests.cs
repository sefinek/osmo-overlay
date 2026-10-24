using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class ProjectRenderTests
{
	private static readonly ProjectRender.Timeline Timeline = new(30, 3000);

	private static OverlayProject Project(List<FrameRange>? cuts = null, List<MusicClip>? music = null, OverlayPreset? preset = null, string? output = "out.mp4")
	{
		return new OverlayProject(["a.mp4", "b.mp4"], output, cuts ?? [], null, 0, preset?.Id, preset) { Music = music ?? [] };
	}

	[TestMethod]
	public void Options_CarriesTheProjectsRecordingsAndOutput()
	{
		RenderOptions options = ProjectRender.Options(Project(), Timeline, "x.mp4", new OverlaySettings(), _ => true);

		CollectionAssert.AreEqual(new[] { "a.mp4", "b.mp4" }, options.InputPaths.ToArray());
		Assert.AreEqual("x.mp4", options.OutputPath);
		Assert.IsNull(options.CutOuts);
		Assert.IsNull(options.Layout);
	}

	[TestMethod]
	public void Options_TurnsCutFramesIntoTimesWithTheirTransitions()
	{
		var cut = new FrameRange(300, 600, new CutTransition(CutTransitionKind.FadeBlack, 0.5));

		RenderOptions options = ProjectRender.Options(Project([cut]), Timeline, "x.mp4", new OverlaySettings(), _ => true);

		TimeRange range = options.CutOuts!.Single();
		Assert.AreEqual(10, range.StartSeconds, 1e-9);
		Assert.AreEqual(20, range.EndSeconds, 1e-9);
		Assert.AreEqual(cut.Transition, range.Transition);
	}

	[TestMethod]
	public void Options_ClipsACutPastTheRecordingsEnd()
	{
		RenderOptions options = ProjectRender.Options(Project([new FrameRange(2900, 9000)]), Timeline, "x.mp4", new OverlaySettings(), _ => true);

		Assert.AreEqual(3000 / 30.0, options.CutOuts!.Single().EndSeconds, 1e-9);
	}

	[TestMethod]
	public void Options_LeavesOutMusicFilesThatAreMissing()
	{
		MusicClip here = new("here.mp3", 0, 0, 10, 60), gone = new("gone.mp3", 0, 0, 10, 60);

		RenderOptions options = ProjectRender.Options(Project(music: [here, gone]), Timeline, "x.mp4", new OverlaySettings(), path => path != "gone.mp3");

		CollectionAssert.AreEqual(new[] { here }, options.Music!.ToArray());
	}

	[TestMethod]
	public void Options_UsesTheEmbeddedLayoutWithoutItsMutedLayers()
	{
		OverlayPreset preset = OverlayPreset.CreateDefault("p", "Project's");

		RenderOptions options = ProjectRender.Options(Project(preset: preset), Timeline, "x.mp4", new OverlaySettings(), _ => true);

		CollectionAssert.AreEqual(OverlayLayers.Drawn(preset.Elements, preset.Layers).ToArray(), options.Layout!.ToArray());
	}

	[TestMethod]
	public void Options_RendersWithTheGivenSettingsAndLeavesTheFileAlone()
	{
		var settings = new OverlaySettings { SpeedCorrectionPercent = 7 };

		RenderOptions options = ProjectRender.Options(Project(), Timeline, "x.mp4", settings, _ => true);

		Assert.AreSame(settings, options.Settings);
	}

	[TestMethod]
	public void SettingsFor_PutsTheProjectsSettingsPresetOverTheCurrentSettings()
	{
		var preset = new SettingsPreset("bike", "Bike", new SettingsPresetValues(SpeedCorrectionPercent: 4));
		OverlaySettings current = new() { DefaultOutputFolder = "D:\\Out", SpeedCorrectionPercent = 0 };

		OverlaySettings settings = ProjectRender.SettingsFor(Project().WithSettingsPreset("bike"), current, [preset]);

		Assert.AreEqual(4, settings.SpeedCorrectionPercent);
		Assert.AreEqual("D:\\Out", settings.DefaultOutputFolder, "what belongs to the computer stays");
	}

	[TestMethod]
	public void SettingsFor_KeepsTheCurrentSettingsWhenThePresetIsGone()
	{
		OverlaySettings current = new() { SpeedCorrectionPercent = 2 };

		OverlaySettings settings = ProjectRender.SettingsFor(Project().WithSettingsPreset("deleted"), current, []);

		Assert.AreSame(current, settings);
	}

	[TestMethod]
	public void OutputPathFor_FallsBackToTheDefaultNextToTheFirstRecording()
	{
		string path = ProjectRender.OutputPathFor(Project(output: null), new OverlaySettings());

		Assert.AreEqual("a_overlay.mp4", Path.GetFileName(path));
		Assert.AreEqual("kept.mp4", ProjectRender.OutputPathFor(Project(output: "kept.mp4"), new OverlaySettings()));
	}

	[TestMethod]
	public void MissingFiles_ListsRecordingsAndMusicThatAreNotThere()
	{
		OverlayProject project = Project(music: [new MusicClip("song.mp3", 0, 0, 5, 30)]);

		List<string> missing = ProjectRender.MissingFiles(project, path => path == "a.mp4");

		CollectionAssert.AreEqual(new[] { "b.mp4", "song.mp3" }, missing);
	}
}

internal static class ProjectTestExtensions
{
	public static OverlayProject WithSettingsPreset(this OverlayProject project, string id)
	{
		return project with { SettingsPresetId = id };
	}
}

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
		Assert.AreNotEqual(before, (Project() with { Preset = OsmoOverlay.Core.Overlay.OverlayPreset.CreateDefault("p", "P") }).Fingerprint());
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
