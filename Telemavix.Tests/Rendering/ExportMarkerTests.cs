using Telemavix.Core;
using Telemavix.Core.Telemetry;
using Telemavix.Core.Updates;

namespace Telemavix.Tests.Rendering;

[TestClass]
public sealed class ExportMarkerTests
{
	[TestMethod]
	public void Comment_NamesTheProgramItsVersionsAndLinks()
	{
		string comment = ExportMarker.Comment(OrientationSource.Accelerometer);

		StringAssert.StartsWith(comment, "Telemavix Studio " + AppUpdates.CurrentVersion);
		StringAssert.Contains(comment, $"telemetry {FileSummaryCache.FormatVersion}");
		StringAssert.Contains(comment, "tilt from accelerometer");
		StringAssert.Contains(comment, AppUpdates.RepositoryUrl);
		StringAssert.Contains(comment, AppUpdates.WebsiteUrl);
	}

	[TestMethod]
	public void Comment_SaysWhichSourceTheTiltCameFrom()
	{
		StringAssert.Contains(ExportMarker.Comment(OrientationSource.Quaternion), "tilt from camera orientation");
	}

	[TestMethod]
	public void Comment_IsPlainAscii()
	{
		Assert.IsTrue(ExportMarker.Comment(OrientationSource.Quaternion).All(c => c is >= ' ' and < (char)127));
	}

	[TestMethod]
	[DataRow(OrientationSource.Accelerometer)]
	[DataRow(OrientationSource.Quaternion)]
	public void TryParse_ReadsBackWhatCommentWrote(OrientationSource source)
	{
		ExportMarkerInfo? info = ExportMarker.TryParse(ExportMarker.Comment(source));

		Assert.IsNotNull(info);
		Assert.AreEqual(AppUpdates.CurrentVersion.ToString(), info.Version);
		Assert.AreEqual(FileSummaryCache.FormatVersion, info.TelemetryVersion);
		Assert.AreEqual(source, info.Orientation);
	}

	[TestMethod]
	public void TryParse_AMarkerWrittenBeforeTheRename_IsStillRead()
	{
		ExportMarkerInfo? info = ExportMarker.TryParse("OsmoOverlay 0.4.0 (telemetry 12, tilt from camera orientation) - https://example.test");

		Assert.IsNotNull(info);
		Assert.AreEqual("0.4.0", info.Version);
		Assert.AreEqual(12, info.TelemetryVersion);
		Assert.AreEqual(OrientationSource.Quaternion, info.Orientation);
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("")]
	[DataRow("shot on a camera")]
	[DataRow("Telemavix Studio 1.0.0 (something else entirely)")]
	public void TryParse_AnythingElse_IsNull(string? comment)
	{
		Assert.IsNull(ExportMarker.TryParse(comment));
	}

	[TestMethod]
	public void TheComment_NamesTheProgramsLicence()
	{
		StringAssert.Contains(ExportMarker.Comment(OrientationSource.Accelerometer), "PolyForm Noncommercial 1.0.0");
	}
}
