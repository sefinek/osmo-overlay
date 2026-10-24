using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Tests.Preview;

[TestClass]
public sealed class CompareLayoutTests
{
	[TestMethod]
	[DataRow(1, 1, 1)]
	[DataRow(2, 2, 1)]
	[DataRow(3, 3, 1)]
	[DataRow(4, 2, 2)]
	[DataRow(5, 3, 2)]
	[DataRow(6, 3, 2)]
	public void TheVideosFillTheGrid(int count, int columns, int rows)
	{
		Assert.AreEqual(columns, CompareLayout.Columns(count));
		Assert.AreEqual(rows, CompareLayout.Rows(count));
		Assert.IsGreaterThanOrEqualTo(count, columns * rows);
	}

	[TestMethod]
	public void NoVideos_NoRows()
	{
		Assert.AreEqual(0, CompareLayout.Rows(0));
	}
}
