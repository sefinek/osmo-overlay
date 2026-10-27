using System.Globalization;
using System.Xml.Linq;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class TelemetryExportTests
{
	private static readonly DateTime Start = new(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);

	private static DerivedFrame Frame(int i, double hz = 10, bool fix = true, bool gap = false, DateTime? time = null, double speedKmh = 36)
	{
		var raw = new TelemetryFrame(i, i / hz, 50.1234567 + i * 1e-5, 19.5, 250.456, time ?? Start.AddSeconds(i / hz), 0, 1, 0,
			HasGpsFix: fix, StartsAfterGap: gap);
		return new DerivedFrame(raw, speedKmh, 90.44, 3.456, i * 1.5, 1.234, -0.5, default, 0, 0, 1, 0.1, 0.2, gap);
	}

	private static List<DerivedFrame> Frames(int count, double hz = 10)
	{
		return [.. Enumerable.Range(0, count).Select(i => Frame(i, hz))];
	}

	private static string Csv(IReadOnlyList<DerivedFrame> frames, double interval = 0, DateTime? fallback = null)
	{
		var writer = new StringWriter();
		TelemetryExport.WriteCsv(writer, frames, interval, fallback);
		return writer.ToString();
	}

	private static XDocument Gpx(IReadOnlyList<DerivedFrame> frames, double interval = 0, DateTime? fallback = null)
	{
		var writer = new StringWriter();
		TelemetryExport.WriteGpx(writer, frames, interval, fallback);
		return XDocument.Parse(writer.ToString());
	}

	[TestMethod]
	public void Csv_HasAHeaderAndARowForEveryFrame()
	{
		string[] lines = Csv(Frames(5)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

		Assert.AreEqual(6, lines.Length);
		Assert.AreEqual("time_s,utc,latitude,longitude,altitude_m,speed_kmh,heading_deg,gradient_pct,distance_m,roll_deg,pitch_deg,g_total,g_lateral,g_longitudinal,gps_fix", lines[0]);
		Assert.IsTrue(lines.Skip(1).All(l => l.Split(',').Length == lines[0].Split(',').Length));
	}

	[TestMethod]
	public void Csv_RowCarriesTheProcessedValues()
	{
		string row = Csv([Frame(10)]).Split('\n')[1];

		Assert.AreEqual("1,2026-05-01T10:00:01.000Z,50.1235567,19.5000000,250.46,36,90.4,3.46,15,1.23,-0.5,1,0.1,0.2,1", row);
	}

	[TestMethod]
	public void Csv_WritesADotWhateverTheCulture()
	{
		CultureInfo before = CultureInfo.CurrentCulture;
		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("pl-PL");

			string csv = Csv(Frames(2));

			Assert.IsFalse(csv.Contains("50,12"), "a comma would split the column");
			Assert.IsTrue(csv.Contains("50.12"));
		}
		finally
		{
			CultureInfo.CurrentCulture = before;
		}
	}

	[TestMethod]
	public void Csv_KeepsOneFrameInEveryInterval()
	{
		string[] lines = Csv(Frames(55), 1).Split('\n', StringSplitOptions.RemoveEmptyEntries);

		Assert.AreEqual(1 + 6, lines.Length);
		CollectionAssert.AreEqual(new[] { "0", "1", "2", "3", "4", "5" }, lines.Skip(1).Select(l => l.Split(',')[0]).ToArray());
	}

	[TestMethod]
	public void Csv_NeverDropsTheFirstFrameAfterAGap()
	{
		List<DerivedFrame> frames = [Frame(0), Frame(1), Frame(2, gap: true)];

		string[] rows = Csv(frames, 10).Split('\n', StringSplitOptions.RemoveEmptyEntries);

		Assert.AreEqual(1 + 2, rows.Length);
	}

	[TestMethod]
	public void Csv_DatesFramesWithoutAGpsTimeFromTheFallbackStartOrLeavesItEmpty()
	{
		DerivedFrame undated = Frame(0) with { Raw = Frame(0).Raw with { GpsTimestamp = null } };

		Assert.AreEqual("2026-01-01T00:00:00.000Z", Csv([undated], 0, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Split('\n')[1].Split(',')[1]);
		Assert.AreEqual("", Csv([undated]).Split('\n')[1].Split(',')[1]);
	}

	[TestMethod]
	public void Csv_MarksFramesWithoutAFixAndLeavesTheirPositionEmpty()
	{
		string[] columns = Csv([Frame(0, fix: false)]).Split('\n')[1].Split(',');

		Assert.AreEqual("0", columns[^1]);
		CollectionAssert.AreEqual(new[] { "", "", "" }, columns[2..5]);
	}

	[TestMethod]
	public void Gpx_IsATrackOfTheFixedFramesWithSpeedInMetresPerSecond()
	{
		XNamespace gpx = "http://www.topografix.com/GPX/1/1", tpx = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";

		XDocument doc = Gpx(Frames(3));

		List<XElement> points = [.. doc.Descendants(gpx + "trkpt")];
		Assert.AreEqual(3, points.Count);
		Assert.AreEqual("50.1234567", (string?)points[0].Attribute("lat"));
		Assert.AreEqual("250.46", (string?)points[0].Element(gpx + "ele"));
		Assert.AreEqual("2026-05-01T10:00:00.000Z", (string?)points[0].Element(gpx + "time"));
		Assert.AreEqual("10", (string?)points[0].Descendants(tpx + "speed").Single());
		Assert.AreEqual("90.4", (string?)points[0].Descendants(tpx + "course").Single());
	}

	[TestMethod]
	public void Gpx_LeavesOutFramesWithoutAFixAndSplitsTheSegment()
	{
		XNamespace gpx = "http://www.topografix.com/GPX/1/1";
		List<DerivedFrame> frames = [Frame(0), Frame(1), Frame(2, fix: false), Frame(3), Frame(4, gap: true), Frame(5)];

		XDocument doc = Gpx(frames);

		List<List<XElement>> segments = [.. doc.Descendants(gpx + "trkseg").Select(s => s.Elements(gpx + "trkpt").ToList())];
		CollectionAssert.AreEqual(new[] { 2, 1, 2 }, segments.Select(s => s.Count).ToArray());
	}

	[TestMethod]
	public void Gpx_OfARecordingWithoutAnyFixIsAnEmptyTrack()
	{
		XNamespace gpx = "http://www.topografix.com/GPX/1/1";

		XDocument doc = Gpx([Frame(0, fix: false)]);

		Assert.IsFalse(doc.Descendants(gpx + "trkpt").Any());
	}

	[TestMethod]
	public void Write_ChoosesTheFormatByTheExtensionAndRefusesOthers()
	{
		string dir = Path.Combine(Path.GetTempPath(), "ovl-export-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			Assert.AreEqual(2, TelemetryExport.Write(Path.Combine(dir, "t.CSV"), Frames(2)));
			Assert.AreEqual(1, TelemetryExport.Write(Path.Combine(dir, "t.gpx"), Frames(30), 5));

			Assert.IsTrue(File.ReadAllText(Path.Combine(dir, "t.CSV")).StartsWith("time_s,"));
			StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "t.gpx")), "<gpx");
			Assert.ThrowsExactly<ArgumentException>(() => TelemetryExport.Write(Path.Combine(dir, "t.json"), Frames(2)));
			Assert.IsTrue(TelemetryExport.IsSupported("a.gpx"));
			Assert.IsFalse(TelemetryExport.IsSupported("a.txt"));
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}
}
