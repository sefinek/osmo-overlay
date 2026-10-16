using System.Text.Json;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class OverlayPresetStoreTests
{
	[TestMethod]
	public void IsLocalFilePath_RejectsNetworkAndDevicePaths()
	{
		Assert.IsTrue(OverlayPresetStore.IsLocalFilePath(Path.Combine(Path.GetTempPath(), "logo.png")));
		Assert.IsFalse(OverlayPresetStore.IsLocalFilePath(@"\\host\share\logo.png"));
		Assert.IsFalse(OverlayPresetStore.IsLocalFilePath("//host/share/logo.png"));
		Assert.IsFalse(OverlayPresetStore.IsLocalFilePath(@"/\host\share\logo.png"));
		Assert.IsFalse(OverlayPresetStore.IsLocalFilePath(@"\\?\UNC\host\share\logo.png"));
		Assert.IsFalse(OverlayPresetStore.IsLocalFilePath("logo.png"));
	}

	[TestMethod]
	public void ImportFromFile_DropsImageLinksThatArentLocalFiles()
	{
		string local = Path.Combine(Path.GetTempPath(), "logo.png");
		var preset = new OverlayPreset("p", "Shared", [
			new ImageElement { Id = "local", X = 0, Y = 0, ImagePath = local },
			new ImageElement { Id = "unc", X = 0, Y = 0, ImagePath = @"\\host\share\logo.png" }
		]);
		string file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
		try
		{
			File.WriteAllText(file, JsonSerializer.Serialize(preset));
			OverlayPreset imported = OverlayPresetStore.ImportFromFile(file)!;

			Assert.AreEqual(local, ((ImageElement)imported.Elements.Single(e => e.Id == "local")).ImagePath);
			Assert.IsNull(((ImageElement)imported.Elements.Single(e => e.Id == "unc")).ImagePath);
		}
		finally
		{
			File.Delete(file);
		}
	}

	[TestMethod]
	public void Sanitize_KeepsTheMapCreditLegible()
	{
		OverlayPreset preset = OverlayPresetStore.Sanitize(new OverlayPreset("p", "Small", [
			new MapAttributionElement { Id = "credit", X = 0, Y = 0, Scale = 0.4f },
			new TextElement { Id = "text", X = 0, Y = 0, Scale = 0.4f }
		]));

		Assert.AreEqual(OverlayElementBounds.MinMapCreditScale, preset.Elements.Single(e => e.Id == "credit").Scale);
		Assert.AreEqual(0.4f, preset.Elements.Single(e => e.Id == "text").Scale);
	}
}
