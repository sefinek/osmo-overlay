using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class ExportMarkerTests
{
	[TestMethod]
	public void Comment_NamesTheProgramItsVersionsAndLinks()
	{
		string comment = ExportMarker.Comment(OrientationSource.Accelerometer);

		StringAssert.StartsWith(comment, "OsmoOverlay " + AppUpdates.CurrentVersion);
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
	[DataRow(null)]
	[DataRow("")]
	[DataRow("shot on a camera")]
	[DataRow("OsmoOverlay 1.0.0 (something else entirely)")]
	public void TryParse_AnythingElse_IsNull(string? comment)
	{
		Assert.IsNull(ExportMarker.TryParse(comment));
	}

	[TestMethod]
	public void TheTag_IsOnUnlessTurnedOff()
	{
		Assert.IsTrue(new OverlaySettings().TagExportedVideo);
	}
}
