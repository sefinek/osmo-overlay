using OsmoOverlay.Core;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class TimeTextTests
{
	[TestMethod]
	[DataRow("90", 90.0)]
	[DataRow("90.5", 90.5)]
	[DataRow("90,5", 90.5)]
	[DataRow("1:30", 90.0)]
	[DataRow(" 1:30.250 ", 90.25)]
	[DataRow("0:05", 5.0)]
	[DataRow("1:02:03.25", 3723.25)]
	[DataRow("00:00", 0.0)]
	public void TryParse_Accepts(string text, double expected)
	{
		Assert.IsTrue(TimeText.TryParse(text, out var seconds));
		Assert.AreEqual(expected, seconds, 1e-9);
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("   ")]
	[DataRow(null)]
	[DataRow("abc")]
	[DataRow("-5")]
	[DataRow("1:60")]
	[DataRow("1.5:30")]
	[DataRow("1:2:3:4")]
	[DataRow("1::30")]
	public void TryParse_Rejects(string? text)
	{
		Assert.IsFalse(TimeText.TryParse(text, out _));
	}

	[TestMethod]
	[DataRow(0.0, "00:00.000")]
	[DataRow(90.25, "01:30.250")]
	[DataRow(3723.25, "1:02:03.250")]
	[DataRow(-3.0, "00:00.000")]
	public void Format(double seconds, string expected)
	{
		Assert.AreEqual(expected, TimeText.Format(seconds));
	}

	[TestMethod]
	public void FormatThenParse_StaysWithinAMillisecond()
	{
		// The GUI writes a range into its fields with Format and reads it back with TryParse.
		foreach (var seconds in new[] { 0.016683, 59.994, 196.9968, 4000.123456 })
		{
			Assert.IsTrue(TimeText.TryParse(TimeText.Format(seconds), out var back));
			Assert.AreEqual(seconds, back, 0.0005);
		}
	}
}
