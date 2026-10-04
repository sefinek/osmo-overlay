using OsmoOverlay.Core.Dependencies;

namespace OsmoOverlay.Tests.Dependencies;

[TestClass]
public sealed class DependencyVersionsTests
{
	private const string ShowVersionsOutput = """
	                                          Found FFmpeg (Shared) [Gyan.FFmpeg.Shared]
	                                          Version
	                                          -------
	                                          10.0
	                                          9.0.2
	                                          9.0.1
	                                          9.0
	                                          8.1.2
	                                          """;

	[TestMethod]
	public void ParseVersionList_TakesOnlyVersionLines()
	{
		CollectionAssert.AreEqual(new[] { "10.0", "9.0.2", "9.0.1", "9.0", "8.1.2" },
			Winget.ParseVersionList(ShowVersionsOutput.ReplaceLineEndings("\r\n")).ToArray());
	}

	[TestMethod]
	public void ParseVersionList_LocalizedHeader_Ignored()
	{
		CollectionAssert.AreEqual(new[] { "13.36" },
			Winget.ParseVersionList("Znaleziono ExifTool [OliverBetz.ExifTool]\nWersja\n------\n13.36\n").ToArray());
	}

	[TestMethod]
	[DataRow(9, "9.0.2")]
	[DataRow(8, "8.1.2")]
	[DataRow(10, "10.0")]
	[DataRow(null, "10.0")]
	[DataRow(7, null)]
	public void PickLatest_StaysWithinMajor(int? major, string? expected)
	{
		Assert.AreEqual(expected, DependencyVersionChecker.PickLatest(Winget.ParseVersionList(ShowVersionsOutput), major));
	}

	[TestMethod]
	public void PickLatest_ComparesNumerically_NotAsText()
	{
		Assert.AreEqual("9.10", DependencyVersionChecker.PickLatest(["9.2", "9.10", "9.9.1", "not-a-version"], 9));
	}

	[TestMethod]
	public void PickLatest_Empty_IsNull()
	{
		Assert.IsNull(DependencyVersionChecker.PickLatest([], null));
	}
}
