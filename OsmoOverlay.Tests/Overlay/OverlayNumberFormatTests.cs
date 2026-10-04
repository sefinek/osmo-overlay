using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class OverlayNumberFormatTests
{
	[TestMethod]
	[DataRow(-0.3, "0", "0")]
	[DataRow(-0.0, "0", "0")]
	[DataRow(-0.004, "0.00", "0.00")]
	[DataRow(-0.04, "0.#", "0")]
	[DataRow(-0.6, "0", "-1")]
	[DataRow(-0.05, "0.0", "-0.1")]
	[DataRow(12.34, "0.0", "12.3")]
	public void F_NeverShowsNegativeZero(double value, string format, string expected)
	{
		Assert.AreEqual(expected, OverlayRenderer.F(value, format));
	}
}
