using System.Globalization;
using OsmoOverlay.Core.Localization;

namespace OsmoOverlay.Tests.Localization;

[TestClass]
public sealed class PluralTests
{
	[TestMethod]
	[DataRow(0, 1)]
	[DataRow(1, 0)]
	[DataRow(2, 1)]
	[DataRow(21, 1)]
	public void English_HasOneAndOther(int count, int expected)
	{
		Assert.AreEqual(expected, Plural.FormIndex(count, CultureInfo.GetCultureInfo("en")));
	}

	[TestMethod]
	[DataRow(0, 2)]
	[DataRow(1, 0)]
	[DataRow(2, 1)]
	[DataRow(4, 1)]
	[DataRow(5, 2)]
	[DataRow(11, 2)]
	[DataRow(12, 2)]
	[DataRow(14, 2)]
	[DataRow(21, 2)]
	[DataRow(22, 1)]
	[DataRow(25, 2)]
	[DataRow(104, 1)]
	[DataRow(112, 2)]
	[DataRow(-3, 1)]
	public void Polish_HasOneFewMany(int count, int expected)
	{
		Assert.AreEqual(expected, Plural.FormIndex(count, CultureInfo.GetCultureInfo("pl-PL")));
	}

	[TestMethod]
	public void Format_PicksTheFormAndFillsTheArguments()
	{
		Assert.AreEqual("3 cuts, 01:00 removed", Plural.Format("{0} cut, {1} removed|{0} cuts, {1} removed", 3, "01:00"));
		Assert.AreEqual("1 cut, 01:00 removed", Plural.Format("{0} cut, {1} removed|{0} cuts, {1} removed", 1, "01:00"));
	}

	[TestMethod]
	public void Format_FallsBackToTheLastForm()
	{
		Assert.AreEqual("only", Plural.Format("only", 5));
	}
}
