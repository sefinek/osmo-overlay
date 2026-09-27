using System.Text.Json.Nodes;
using OsmoOverlay.Cameras.Insta360;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Reframe;

[TestClass]
public sealed class HorizonLevelingTests
{
	private static TelemetryFrame Frame(int index, double seconds, double x, double y, double z)
	{
		return new TelemetryFrame(index, seconds, 0, 0, 0, null, x, y, z, HasGpsFix: false);
	}

	[TestMethod]
	public void OnlyForACameraThatKnowsWhereDownIs()
	{
		List<TelemetryFrame> frames = [Frame(0, 0, 0, 1, 0)];

		Assert.IsNull(HorizonLeveling.For(frames, new NoAxesFormat()));
		Assert.IsNull(HorizonLeveling.For([], new Insta360Format()));
		Assert.IsNotNull(HorizonLeveling.For(frames, new Insta360Format()));
	}

	[TestMethod]
	public void MapsTheX4sAxes_DownIsZMinusXY()
	{
		// The accelerometer reading (x, y, z) = (0, 0, 1): down in v360's space is (z, -x, y) = (1, 0, 0) - to the right.
		var leveling = HorizonLeveling.For([Frame(0, 0, 0, 0, 1)], new Insta360Format())!;

		Direction down = leveling.At(0).Apply(new Direction(0, 1, 0));
		Assert.AreEqual(1, down.X, 1e-9);
		Assert.AreEqual(0, down.Y, 1e-9);
		Assert.AreEqual(0, down.Z, 1e-9);
	}

	[TestMethod]
	public void AveragesASecondAroundEachFrame()
	{
		// Level (down = (0, 1, 0), i.e. accel (-1, 0, 0)) except one frame jolted sideways - the second around it outweighs it.
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, 120).Select(i => i == 60 ? Frame(i, i / 60.0, 0, 0, 5) : Frame(i, i / 60.0, -1, 0, 0))];
		var leveling = HorizonLeveling.For(frames, new Insta360Format())!;

		Direction down = leveling.At(1.0).Apply(new Direction(0, 1, 0));
		Assert.IsTrue(down.Y > 0.99, $"a single jolt barely tilts it: {down}");
	}

	private sealed class NoAxesFormat : ICameraFormat
	{
		public string Id => "no-axes";
		public string DisplayName => "No axes";

		public CameraRecording? Detect(string path, JsonArray streams)
		{
			return null;
		}

		public TelemetryExtractionResult ExtractTelemetry(string path, SourceInfo source)
		{
			throw new NotSupportedException();
		}

		public Direction? Gravity(TelemetryFrame frame)
		{
			return null;
		}

		public string DescribeTelemetry(IReadOnlyList<TelemetryFrame> frames)
		{
			return "";
		}
	}
}
