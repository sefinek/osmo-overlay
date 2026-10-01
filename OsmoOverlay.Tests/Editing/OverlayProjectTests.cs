using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class OverlayProjectTests
{
	[TestMethod]
	public void SaveAndLoad_RoundTripsEverything()
	{
		OverlayPreset preset = OverlayPreset.CreateDefault("p1", "Mine");
		var project = new OverlayProject([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")], Path.GetFullPath("out.mp4"), [new FrameRange(10, 20, new CutTransition(CutTransitionKind.FadeWhite, 0.8))], new ReframeView(10, 5, 0, 90, false), 42, "p1", preset);
		string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{OverlayProject.Extension}");

		try
		{
			project.Save(path);
			OverlayProject loaded = OverlayProject.Load(path);

			CollectionAssert.AreEqual(project.InputPaths, loaded.InputPaths);
			CollectionAssert.AreEqual(project.Cuts, loaded.Cuts);
			Assert.AreEqual(project.Reframe, loaded.Reframe);
			Assert.AreEqual(42, loaded.PreviewFrame);
			Assert.IsTrue(OverlayPresetStore.SameLayout(preset, OverlayPresetStore.Sanitize(loaded.Preset)));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[TestMethod]
	public void Load_RejectsNewerFormat()
	{
		string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{OverlayProject.Extension}");
		File.WriteAllText(path, """{"InputPaths":[],"Cuts":[],"Format":999}""");

		try
		{
			Assert.ThrowsExactly<InvalidDataException>(() => OverlayProject.Load(path));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[TestMethod]
	[DataRow("\\\\host\\share\\a.mp4", null)]
	[DataRow("//host/share/a.mp4", null)]
	[DataRow("relative.mp4", "\\\\host\\share\\out.mp4")]
	[DataRow("relative.mp4", null)]
	public void Load_RejectsPathsOffTheLocalDrives(string input, string? output)
	{
		string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{OverlayProject.Extension}");
		new OverlayProject([input], output, [], null, 0, null, null).Save(path);

		try
		{
			Assert.ThrowsExactly<InvalidDataException>(() => OverlayProject.Load(path));
		}
		finally
		{
			File.Delete(path);
		}
	}
}
