using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class FitReaderTests
{
	private static readonly DateTime T0 = new(2026, 10, 21, 14, 20, 49, DateTimeKind.Utc);

	[TestMethod]
	public void Read_FixesWithPositionAltitudeAndSpeed()
	{
		byte[] file = new FitBuilder().DefineRecord()
			.Record(T0, 53.154664, 18.900202, 87.4, 0.681)
			.Record(T0.AddSeconds(1), 53.154674, 18.900222, 87.6, 1.101)
			.ToArray();

		ExternalTrack track = FitReader.Read(file);

		Assert.AreEqual(2, track.Points.Count);
		Assert.AreEqual(T0, track.Points[0].Utc);
		Assert.AreEqual(53.154664, track.Points[0].Latitude, 1e-6);
		Assert.AreEqual(18.900202, track.Points[0].Longitude, 1e-6);
		Assert.AreEqual(87.4, track.Points[0].AltitudeMeters!.Value, 1e-9);
		Assert.AreEqual(0.681, track.Points[0].SpeedMs!.Value, 1e-9);
		Assert.AreEqual(T0.AddSeconds(1), track.Points[1].Utc);
	}

	[TestMethod]
	public void Read_BigEndianDefinitionsAndDeveloperFields()
	{
		byte[] file = new FitBuilder().DefineRecord(true, true)
			.Record(T0, 53.1, 18.9, 80, 2, true, true)
			.Record(T0.AddSeconds(1), 53.2, 18.8, 81, 3, true, true)
			.ToArray();

		ExternalTrack track = FitReader.Read(file);

		Assert.AreEqual(53.2, track.Points[1].Latitude, 1e-6);
		Assert.AreEqual(18.8, track.Points[1].Longitude, 1e-6);
		Assert.AreEqual(3, track.Points[1].SpeedMs!.Value, 1e-9);
	}

	[TestMethod]
	public void Read_CompressedTimestampsRollOverTheirFiveBits()
	{
		DateTime start = T0.AddSeconds(30 - FitBuilder.Seconds(T0) % 32);
		byte[] file = new FitBuilder().DefineRecord().DefineCompressedRecord()
			.Record(start, 53.1, 18.9, 80, 2)
			.CompressedRecord((int)((FitBuilder.Seconds(start) + 1) % 32), 53.1001, 18.9)
			.CompressedRecord((int)((FitBuilder.Seconds(start) + 3) % 32), 53.1002, 18.9)
			.ToArray();

		ExternalTrack track = FitReader.Read(file);

		CollectionAssert.AreEqual(new[] { start, start.AddSeconds(1), start.AddSeconds(3) }, track.Points.Select(p => p.Utc).ToArray());
	}

	[TestMethod]
	public void Read_AFixWithoutAPositionIsLeftOut()
	{
		byte[] file = new FitBuilder().DefineRecord()
			.Record(T0, 53.1, 18.9, 80, 2)
			.RecordWithoutPosition(T0.AddSeconds(1))
			.Record(T0.AddSeconds(2), 53.2, 18.9, 80, 2)
			.ToArray();

		Assert.AreEqual(2, FitReader.Read(file).Points.Count);
	}

	[TestMethod]
	public void Read_TheTimerStoppedIsAPauseUntilTheNextStart()
	{
		FitBuilder builder = new FitBuilder().DefineRecord().DefineEvent()
			.TimerEvent(T0, 0)
			.Record(T0, 53.1, 18.9, 80, 2)
			.Record(T0.AddSeconds(1), 53.1001, 18.9, 80, 2)
			.TimerEvent(T0.AddSeconds(2), 4)
			.TimerEvent(T0.AddSeconds(5), 4)
			.TimerEvent(T0.AddSeconds(6), 0)
			.Record(T0.AddSeconds(6), 53.1002, 18.9, 80, 2)
			.TimerEvent(T0.AddSeconds(9), 4);

		ExternalTrack track = FitReader.Read(builder.ToArray());

		ExternalTrackPause pause = track.Pauses.Single();
		Assert.AreEqual(T0.AddSeconds(2), pause.FromUtc, "from the first stop");
		Assert.AreEqual(T0.AddSeconds(6), pause.ToUtc);
	}

	[TestMethod]
	public void Read_OnlyTheTimersEventsCount()
	{
		FitBuilder builder = new FitBuilder().DefineRecord().DefineEvent()
			.Record(T0, 53.1, 18.9, 80, 2)
			.Record(T0.AddSeconds(1), 53.1001, 18.9, 80, 2)
			.TimerEvent(T0.AddSeconds(1), 4, 3)
			.TimerEvent(T0.AddSeconds(2), 0, 3);

		Assert.AreEqual(0, FitReader.Read(builder.ToArray()).Pauses.Count);
	}

	[TestMethod]
	public void Read_ADamagedFileIsAnError()
	{
		byte[] file = new FitBuilder().DefineRecord().Record(T0, 53.1, 18.9, 80, 2).Record(T0.AddSeconds(1), 53.2, 18.9, 80, 2).ToArray(true);

		Assert.ThrowsExactly<InvalidDataException>(() => FitReader.Read(file));
		Assert.ThrowsExactly<InvalidDataException>(() => FitReader.Read(file[..^8]));
	}

	[TestMethod]
	public void Read_ChainedFilesAreOneTrack()
	{
		byte[] first = new FitBuilder().DefineRecord().Record(T0, 53.1, 18.9, 80, 2).Record(T0.AddSeconds(1), 53.1001, 18.9, 80, 2).ToArray();
		byte[] second = new FitBuilder().DefineRecord().Record(T0.AddSeconds(2), 53.1002, 18.9, 80, 2).Record(T0.AddSeconds(3), 53.1003, 18.9, 80, 2).ToArray();

		Assert.AreEqual(4, FitReader.Read([.. first, .. second]).Points.Count);
	}

	[TestMethod]
	public void Read_NotAFitFile()
	{
		Assert.ThrowsExactly<InvalidDataException>(() => FitReader.Read("<gpx></gpx> and more text"u8.ToArray()));
		Assert.IsFalse(FitReader.LooksLikeFit("<?xml version=\"1.0\"?>"u8));
		Assert.IsTrue(FitReader.LooksLikeFit(new FitBuilder().DefineRecord().ToArray()));
	}
}
