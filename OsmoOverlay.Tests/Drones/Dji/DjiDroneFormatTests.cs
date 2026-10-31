using System.Text.Json.Nodes;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Drones.Dji;

namespace OsmoOverlay.Tests.Drones.Dji;

[TestClass]
public sealed class DjiDroneFormatTests
{
	private static JsonArray Streams(params string[] types)
	{
		return [.. types.Select(t => (JsonNode)new JsonObject { ["codec_type"] = t })];
	}

	[TestMethod]
	public void Detect_AVideoOnlyFileOfTheDjiNaming()
	{
		var format = new DjiDroneFormat();

		Assert.IsNotNull(format.Detect(@"C:\flights\DJI_0992.MP4", Streams("video")));
		Assert.IsNotNull(format.Detect("/sd/dji_0001.mov", Streams("video")));
		Assert.IsNull(format.Detect("DJI_0992.MP4", Streams("video", "data")), "a telemetry stream is another DJI camera's");
		Assert.IsNull(format.Detect("DJI_0992.MP4", Streams("video", "video")));
		Assert.IsNull(format.Detect("holiday.mp4", Streams("video")));
		Assert.IsNull(format.Detect("DJI_20260101_0001_D.MP4", Streams("video")), "the Osmo/Mini naming is not matched");
	}

	[TestMethod]
	public void PreferredPreset_IsTheDroneLayout()
	{
		Assert.AreEqual(OverlayPresetStore.DronePresetId, ((ICameraFormat)new DjiDroneFormat()).PreferredPresetId);
		Assert.IsNull(((ICameraFormat)new OsmoOverlay.Cameras.Dji.DjiOsmoFormat()).PreferredPresetId);
	}

	[TestMethod]
	public void ExtractTelemetry_GivesOneEmptySampleForEveryVideoFrame()
	{
		var video = new VideoInfo("hevc", "Main", 3840, 2160, "30/1", "yuv420p", null, null, null, null, 100_000_000);

		TelemetryExtractionResult result = new DjiDroneFormat().ExtractTelemetry("DJI_0001.MP4", new SourceInfo(video, null, 2, null));

		Assert.AreEqual(60, result.Frames.Count);
		Assert.IsFalse(result.Frames.Any(f => f.HasGpsFix));
		Assert.AreEqual(2.0 - 1 / 30.0, result.Frames[^1].SampleTimeSeconds, 1e-9);
	}

	[TestMethod]
	public void Track_WithOneRecordingAsLongAsTheVideoPlacesTheVideoAtItsStart()
	{
		DateTime start = new(2026, 10, 29, 13, 54, 54, DateTimeKind.Utc);
		var points = Enumerable.Range(0, 600).Select(i => new ExternalTrackPoint(start.AddSeconds(i), 50 + i * 0.0001, 20, 100, 5));
		var track = new ExternalTrack(points, recordings: [new ExternalTrackRecording(start.AddSeconds(73), start.AddSeconds(373))]);
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, 300 * 30).Select(i => new TelemetryFrame(i, i / 30.0, 0, 0, 0, null, 0, 0, 0, HasGpsFix: false))];

		TrackSynchronization sync = new ExternalTrackSource("x.txt").Resolve(frames, 30, null, track);

		Assert.AreEqual(start.AddSeconds(73), sync.At(0));
	}

	[TestMethod]
	public void Track_RecordingsOfAnotherLengthAreNotUsed()
	{
		DateTime start = new(2026, 10, 29, 13, 54, 54, DateTimeKind.Utc);
		var points = Enumerable.Range(0, 600).Select(i => new ExternalTrackPoint(start.AddSeconds(i), 50 + i * 0.0001, 20, 100, 5));
		var track = new ExternalTrack(points, recordings: [new ExternalTrackRecording(start.AddSeconds(73), start.AddSeconds(100))]);
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, 300 * 30).Select(i => new TelemetryFrame(i, i / 30.0, 0, 0, 0, null, 0, 0, 0, HasGpsFix: false))];

		Assert.ThrowsExactly<InvalidOperationException>(() => new ExternalTrackSource("x.txt").Resolve(frames, 30, null, track));
	}
}
