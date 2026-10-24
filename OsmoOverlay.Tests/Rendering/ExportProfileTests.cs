using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class ExportProfileTests
{
	private static readonly double[] BitrateChoices = [1.0, 1.25, 1.5, 2.0];

	[TestMethod]
	public void SourceIsTheDefaultSettingsExactly()
	{
		Assert.IsTrue(ExportProfile.Source.Matches(new OverlaySettings()));
		Assert.AreSame(ExportProfile.Source, ExportProfile.Of(new OverlaySettings()));
	}

	[TestMethod]
	public void EveryProfileUsesOnlyWhatSettingsOffers()
	{
		foreach (ExportProfile profile in ExportProfile.All)
		{
			Assert.IsTrue(profile.Resolution is null || OutputVideo.Resolutions.Contains(profile.Resolution.Value), profile.Id);
			Assert.IsTrue(profile.Codec is null or "h264" or "hevc", profile.Id);
			Assert.IsTrue(BitrateChoices.Contains(profile.BitrateMultiplier), profile.Id);
		}
	}

	[TestMethod]
	public void ProfilesAreDistinctAndNamed()
	{
		Assert.AreEqual(ExportProfile.All.Count, ExportProfile.All.Select(p => p.Id).Distinct().Count());
		Assert.AreEqual(ExportProfile.All.Count, ExportProfile.All.Select(p => p.Name).Distinct().Count());
		Assert.IsTrue(ExportProfile.All.All(p => !string.IsNullOrWhiteSpace(p.Name) && p.Name != p.Id));
	}

	[TestMethod]
	public void EveryProfileIsRecognisedOnceItIsApplied()
	{
		foreach (ExportProfile profile in ExportProfile.All)
			Assert.AreSame(profile, ExportProfile.Of(profile.ApplyTo(new OverlaySettings())), profile.Id);
	}

	[TestMethod]
	public void ApplyTo_ChangesOnlyTheExportOptions()
	{
		var settings = new OverlaySettings { SpeedCorrectionPercent = 3, DefaultOutputFolder = "out-folder", OutputResolution = 720, OutputCodec = "h264" };

		OverlaySettings applied = ExportProfile.Find("quality")!.ApplyTo(settings);

		Assert.AreEqual(3, applied.SpeedCorrectionPercent);
		Assert.AreEqual("out-folder", applied.DefaultOutputFolder);
		Assert.IsNull(applied.OutputResolution);
		Assert.IsNull(applied.OutputCodec);
		Assert.AreEqual(1.5, applied.OutputBitrateMultiplier);
	}

	[TestMethod]
	public void OptionsOfYourOwnAreNoProfile()
	{
		Assert.IsNull(ExportProfile.Of(new OverlaySettings { OutputResolution = 1440 }));
	}

	[TestMethod]
	public void Find_TakesIdOrNameInAnyCase()
	{
		ExportProfile web = ExportProfile.All.Single(p => p.Id == "web1080");

		Assert.AreSame(web, ExportProfile.Find("WEB1080"));
		Assert.AreSame(web, ExportProfile.Find(web.Name.ToUpperInvariant()));
		Assert.IsNull(ExportProfile.Find("nope"));
	}

	[TestMethod]
	public void Web1080_ShrinksA4KSourceToFullHdH264()
	{
		var source = new VideoInfo("hevc", "Main 10", 3840, 2160, "30", "yuv420p10le", null, null, null, null, 100_000_000);

		VideoInfo output = OutputVideo.For(source, ExportProfile.Find("web1080")!.ApplyTo(new OverlaySettings()));

		Assert.AreEqual((1920, 1080), (output.Width, output.Height));
		Assert.AreEqual("h264", output.CodecName);
		Assert.AreEqual("yuv420p", output.PixFmt);
	}
}
