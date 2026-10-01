using OsmoOverlay.Cameras.Dji;

namespace OsmoOverlay.Tests.Cameras.Dji;

[TestClass]
public sealed class DjiModelsTests
{
	[TestMethod]
	public void KnownCode_BecomesTheModelName()
	{
		Assert.AreEqual(DjiModels.OsmoAction5Pro, DjiModels.DisplayName("DJI AC004"));
		Assert.AreEqual(DjiModels.OsmoAction6, DjiModels.DisplayName(" dji ac006 "));
	}

	[TestMethod]
	public void UnknownNameOrNull_IsKeptAsItCame()
	{
		Assert.AreEqual("DJI AC999", DjiModels.DisplayName("DJI AC999"));
		Assert.IsNull(DjiModels.DisplayName(null));
	}

	[TestMethod]
	public void SupportNotice_OnlyForModelsThatAreNotSupported()
	{
		var format = new DjiOsmoFormat();

		Assert.IsNull(format.SupportNotice(DjiModels.OsmoAction6));
		Assert.IsNull(format.SupportNotice(DjiModels.OsmoAction5Pro));
		StringAssert.Contains(format.SupportNotice("DJI AC999"), "DJI AC999");
		Assert.IsNotNull(format.SupportNotice(null));
	}
}
