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
		var local = Path.Combine(Path.GetTempPath(), "logo.png");
		var preset = new OverlayPreset("p", "Shared", [
			new ImageElement { Id = "local", X = 0, Y = 0, ImagePath = local },
			new ImageElement { Id = "unc", X = 0, Y = 0, ImagePath = @"\\host\share\logo.png" }
		]);
		var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
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
}
