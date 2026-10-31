using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Drones.Dji;
using SkiaSharp;
using static OsmoOverlay.Tests.Drones.Dji.FlightLogBuilder;

namespace OsmoOverlay.Tests.Drones.Dji;

[TestClass]
public sealed class FlightEventTests
{
	private static readonly DateTime Started = new(2026, 10, 29, 13, 0, 0, DateTimeKind.Utc);

	private static IReadOnlyList<FlightLogEvent> EventsOf(FlightLogBuilder builder)
	{
		return FlightLog.Parse(builder.Build()).Decode(null).Events!;
	}

	[TestMethod]
	public void Decode_ModeAndMotorChangesAreEvents()
	{
		var builder = new FlightLogBuilder(12, Started);
		for (int i = 0; i < 6; i++) builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true, mode: i < 3 ? 6 : 31, motorsOn: i is >= 2 and < 5));

		IReadOnlyList<FlightLogEvent> events = EventsOf(builder);

		CollectionAssert.AreEqual(
			new[] { FlightEventKind.FlightMode, FlightEventKind.MotorsOn, FlightEventKind.FlightMode, FlightEventKind.MotorsOff },
			events.Select(e => e.Event.Kind).ToArray());
		Assert.AreEqual("P-GPS", events[0].Event.Text);
		Assert.AreEqual("Sport", events[2].Event.Text);
		Assert.AreEqual(0.2, events[1].Seconds, 1e-9);
		Assert.AreEqual(0.5, events[3].Seconds, 1e-9);
	}

	[TestMethod]
	public void Decode_AnUnknownModeIsNamedByItsNumber()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true, mode: 99));

		Assert.AreEqual("Mode 99", EventsOf(builder).Single().Event.Text);
	}

	[TestMethod]
	public void Decode_BatteryLevelsAreAnnouncedPerTenPercentOnceTheMotorsRun()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Battery, BatteryRecord(60));
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true, motorsOn: true));
		foreach (int percent in new[] { 51, 50, 45, 39 })
		{
			builder.Add(Battery, BatteryRecord(percent));
			builder.Add(Osd, OsdRecord(50, 20, 1, 0, 0, 10, true, motorsOn: true));
		}

		FlightEvent[] battery = [.. EventsOf(builder).Select(e => e.Event).Where(e => e.Kind is FlightEventKind.BatteryStart or FlightEventKind.BatteryLevel)];

		CollectionAssert.AreEqual(
			new[] { (FlightEventKind.BatteryStart, 60.0), (FlightEventKind.BatteryLevel, 50.0), (FlightEventKind.BatteryLevel, 40.0) },
			battery.Select(e => (e.Kind, e.Value!.Value)).ToArray());
	}

	[TestMethod]
	public void Decode_TheLowestCellIsAnnouncedAsLowThenCritical()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true, motorsOn: true));
		foreach (int cell in new[] { 3700, 3480, 3470, 3340, 3290, 3290 })
		{
			builder.Add(Battery, BatteryRecord(60));
			builder.Add(Battery, CellsRecord(3800, cell, 3800));
			builder.Add(Osd, OsdRecord(50, 20, 1, 0, 0, 10, true, motorsOn: true));
		}

		FlightEvent[] voltage = [.. EventsOf(builder).Select(e => e.Event).Where(e => e.Kind is FlightEventKind.LowVoltage or FlightEventKind.CriticalVoltage)];

		CollectionAssert.AreEqual(
			new[] { FlightEventKind.LowVoltage, FlightEventKind.CriticalVoltage }, voltage.Select(e => e.Kind).ToArray());
		Assert.AreEqual(3.48, voltage[0].Value!.Value, 1e-9);
		Assert.AreEqual(3.29, voltage[1].Value!.Value, 1e-9);
	}

	private static FlightEventTracker FlyingBatteryTracker()
	{
		var tracker = new FlightEventTracker();
		tracker.OnBattery(BatteryRecord(60), 0);
		tracker.OnOsd(OsdRecord(50, 20, 0, 0, 0, 10, true), 0);
		tracker.OnOsd(OsdRecord(50, 20, 0, 0, 0, 10, true, motorsOn: true), 0.1);
		return tracker;
	}

	private static FlightEvent[] VoltageEvents(FlightEventTracker tracker) =>
		[.. tracker.Events.Select(e => e.Event).Where(e => e.Kind is FlightEventKind.LowVoltage or FlightEventKind.CriticalVoltage)];

	[TestMethod]
	[DataRow(3501, 0, FlightEventKind.LowVoltage)]
	[DataRow(3500, 1, FlightEventKind.LowVoltage)]
	[DataRow(3499, 1, FlightEventKind.LowVoltage)]
	[DataRow(3350, 1, FlightEventKind.LowVoltage)]
	[DataRow(3300, 1, FlightEventKind.CriticalVoltage)]
	[DataRow(3280, 1, FlightEventKind.CriticalVoltage)]
	public void VoltageBoundariesMatchTheWidgetAndReportTheActualMinimum(int millivolts, int count, FlightEventKind kind)
	{
		FlightEventTracker tracker = FlyingBatteryTracker();
		tracker.OnBattery(CellsRecord(3800, millivolts, 3800), 1);
		FlightEvent[] events = VoltageEvents(tracker);
		Assert.HasCount(count, events);
		if (count == 0) return;
		Assert.AreEqual(kind, events[0].Kind);
		Assert.AreEqual(millivolts / 1000.0, events[0].Value);
		Assert.AreEqual(kind == FlightEventKind.CriticalVoltage ? BatteryCondition.Critical : BatteryCondition.Caution,
			DroneBatteryHealth.Cells(new DroneBattery(60, CellVoltages: [3.8, millivolts / 1000.0, 3.8])));
	}

	[TestMethod]
	public void VoltageAlertsRepeatAfterEachRecoveryButNotWhileVoltageRemainsLow()
	{
		FlightEventTracker tracker = FlyingBatteryTracker();
		tracker.OnBattery(CellsRecord(3490), 1);
		tracker.OnBattery(CellsRecord(3700), 2);
		tracker.OnBattery(CellsRecord(3700), 2.5);
		tracker.OnBattery(CellsRecord(3490), 3);
		tracker.OnBattery(CellsRecord(3350), 3.1);
		tracker.OnBattery(CellsRecord(3280), 3.2);
		tracker.OnBattery(CellsRecord(3500), 4);
		tracker.OnBattery(CellsRecord(3280), 4.2);
		CollectionAssert.AreEqual(new[] { FlightEventKind.LowVoltage, FlightEventKind.LowVoltage, FlightEventKind.CriticalVoltage, FlightEventKind.CriticalVoltage },
			VoltageEvents(tracker).Select(e => e.Kind).ToArray());
	}

	[TestMethod]
	public void CriticalVoltageRearmsImmediatelyAboveItsOwnThreshold()
	{
		FlightEventTracker tracker = FlyingBatteryTracker();
		tracker.OnBattery(CellsRecord(3280), 1);
		tracker.OnBattery(CellsRecord(3301), 1.1);
		tracker.OnBattery(CellsRecord(3300), 1.2);
		Assert.HasCount(2, VoltageEvents(tracker));
		Assert.IsTrue(VoltageEvents(tracker).All(e => e.Kind == FlightEventKind.CriticalVoltage));
	}

	[TestMethod]
	public void PackReadingsCannotProveCellRecovery()
	{
		FlightEventTracker tracker = FlyingBatteryTracker();
		tracker.OnBattery(CellsRecord(3280), 1);
		for (int i = 3; i < 8; i++) tracker.OnBattery(BatteryRecord(60), i);
		tracker.OnBattery(CellsRecord(3280), 8.1);
		Assert.HasCount(1, VoltageEvents(tracker));
	}

	[TestMethod]
	[DataRow(0.0)]
	[DataRow(-1.0)]
	[DataRow(double.NaN)]
	[DataRow(double.PositiveInfinity)]
	[DataRow(65.535)]
	public void InvalidCellReadingsAreUnknownRatherThanCritical(double invalid)
	{
		var battery = new DroneBattery(60, CellVoltages: [3.8, invalid, 3.8]);
		Assert.IsNull(battery.MinCellVoltage);
		Assert.AreEqual(BatteryCondition.Normal, DroneBatteryHealth.Cells(battery));
	}

	[TestMethod]
	public void InvalidCellsDoNotCountAsRecoveryOrCreateAnAlert()
	{
		FlightEventTracker tracker = FlyingBatteryTracker();
		tracker.OnBattery(CellsRecord(0, 3800), 0.5);
		Assert.IsEmpty(VoltageEvents(tracker));
		tracker.OnBattery(CellsRecord(3280), 1);
		tracker.OnBattery(CellsRecord(0), 3);
		tracker.OnBattery(CellsRecord(3280), 4);
		Assert.HasCount(1, VoltageEvents(tracker));
		tracker.OnBattery(CellsRecord(3700), 5);
		tracker.OnBattery(CellsRecord(3280), 6);
		Assert.HasCount(2, VoltageEvents(tracker));
	}

	[TestMethod]
	public void Decode_ReadsTheBatteryOfThePackAndItsCells()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));
		builder.Add(Battery, CellsRecord(3500, 3510, 3520));
		builder.Add(Battery, BatteryRecord(21, 10600, -7000, 389));

		DroneBattery battery = FlightLog.Parse(builder.Build()).Decode(null).Batteries!.Last().Battery;

		Assert.AreEqual(21, battery.Percent);
		Assert.AreEqual(10.6, battery.VoltageV!.Value, 1e-9);
		Assert.AreEqual(7, battery.CurrentA!.Value, 1e-9, "the current drawn is positive");
		Assert.AreEqual(38.9, battery.TemperatureCelsius!.Value, 1e-9);
		CollectionAssert.AreEqual(new[] { 3.5, 3.51, 3.52 }, battery.CellVoltages!.ToArray());
		Assert.AreEqual(3.5, battery.MinCellVoltage!.Value, 1e-9);
	}

	[TestMethod]
	public void Decode_TheAircraftNameAndTheModeOfEverySampleAreRead()
	{
		byte[] component = new byte[110];
		System.Text.Encoding.UTF8.GetBytes("Mój Mavic").CopyTo(component, 21);
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(13, component);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true, mode: 31));
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true, mode: 6));
		FlightLog log = FlightLog.Parse(builder.Build());

		FlightLogData data = log.Decode(null);
		ExternalTrack track = DjiFlightLogFormat.ToTrack(log, data);

		Assert.AreEqual("Mój Mavic", data.AircraftName);
		Assert.AreEqual("Mój Mavic", track.DeviceName);
		CollectionAssert.AreEqual(new[] { "Sport", "P-GPS" }, data.Samples.Select(x => x.FlightMode).ToArray());
	}

	[TestMethod]
	public void Decode_TheHomeRecordGivesTheReturnAltitudeAndTheLimitOnce()
	{
		byte[] home = HomeRecord(100);
		Array.Resize(ref home, 76);
		BitConverter.GetBytes((ushort)85).CopyTo(home, 22);
		BitConverter.GetBytes(120f).CopyTo(home, 37);
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));
		builder.Add(Home, home);
		builder.Add(Home, home);

		FlightEvent[] events = [.. EventsOf(builder).Select(e => e.Event).Where(e => e.Kind is FlightEventKind.ReturnAltitude or FlightEventKind.AltitudeLimit)];

		CollectionAssert.AreEqual(new[] { (FlightEventKind.ReturnAltitude, 85.0), (FlightEventKind.AltitudeLimit, 120.0) }, events.Select(e => (e.Kind, e.Value!.Value)).ToArray());
	}

	[TestMethod]
	public void ToTrack_TheFinalPositionIsMeasuredFromTakeOffAtTheMotorsStop()
	{
		byte[] home = HomeRecord(0);
		Array.Resize(ref home, 76);
		BitConverter.GetBytes(20 * Math.PI / 180).CopyTo(home, 0);
		BitConverter.GetBytes(50 * Math.PI / 180).CopyTo(home, 8);
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Home, home);
		builder.Add(Custom, CustomRecord(Started));
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));
		builder.Add(Osd, OsdRecord(50.0005, 20, 5, 0, 0, 10, true, motorsOn: true));
		builder.Add(Osd, OsdRecord(50.001, 20, 5, 0, 0, 10, true, motorsOn: true));
		builder.Add(Osd, OsdRecord(50.0002, 20, 0, 0, 0, 10, true));
		FlightLog log = FlightLog.Parse(builder.Build());

		ExternalTrack track = DjiFlightLogFormat.ToTrack(log, log.Decode(null));

		FlightEvent final = track.Events.Select(e => e.Event).Single(e => e.Kind == FlightEventKind.FinalPosition);
		Assert.AreEqual(22.2, final.Value!.Value, 0.5);
	}

	[TestMethod]
	public void Decode_TheBatteryAtMotorShutdownIsAnEvent()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true, motorsOn: true));
		builder.Add(Battery, BatteryRecord(19));
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));

		FlightEvent end = EventsOf(builder).Select(e => e.Event).Single(e => e.Kind == FlightEventKind.BatteryEnd);

		Assert.AreEqual(19, end.Value);
		Assert.AreEqual("Battery at motor shutdown: 19%", OverlayRenderer.FlightAlertText(end).Detail);
	}

	[TestMethod]
	public void Decode_TheGpsLevelIsTheOsdBytesBitsTwoToFive()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true, gpsLevel: 5));
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true, gpsLevel: 4));

		CollectionAssert.AreEqual(new int?[] { 5, 4 }, FlightLog.Parse(builder.Build()).Decode(null).Samples.Select(x => x.GpsLevel).ToArray());
	}

	[TestMethod]
	public void Decode_TheRadioLinksAreTheLowSevenBitsOfType49AndTheHighBitSaysWhich()
	{
		var builder = new FlightLogBuilder(14, Started);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));
		builder.Add(49, [0x5A]);
		builder.Add(49, [0xC6]);
		builder.Add(49, [0xFF]);

		IReadOnlyList<FlightSignalSample> signals = FlightLog.Parse(builder.Build()).Decode([new Keychain(builder.Keys)]).Signals!;

		Assert.AreEqual(new DroneSignal(null, 90), signals[0].Signal);
		Assert.AreEqual(new DroneSignal(70, 90), signals[1].Signal);
		Assert.AreEqual(2, signals.Count, "a quality over 100 is not one");
	}

	[TestMethod]
	public void Apply_TheSignalAndTheGpsLevelReachTheFrames()
	{
		var t0 = new DateTime(2026, 10, 29, 13, 0, 0, DateTimeKind.Utc);
		var track = new ExternalTrack(
			[new ExternalTrackPoint(t0, 50, 20, GpsLevel: 5), new ExternalTrackPoint(t0.AddSeconds(10), 50.001, 20, GpsLevel: 4)],
			droneSignals: [new ExternalDroneSignalReading(t0, new DroneSignal(90, 80)), new ExternalDroneSignalReading(t0.AddSeconds(5), new DroneSignal(60, 50))]);
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, 100).Select(i => new TelemetryFrame(i, i / 10.0, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false))];

		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(frames, track, t0);

		Assert.AreEqual(new DroneSignal(90, 80), merged[20].DroneSignal);
		Assert.AreEqual(new DroneSignal(60, 50), merged[60].DroneSignal);
		Assert.AreEqual(5, merged[20].GpsLevel);
	}

	[TestMethod]
	public void ReadMessage_ReadsAWarningWithItsCodeFromARealRecord()
	{
		byte[] record = Convert.FromHexString(
			"08011A49120533303231331A375A616B7475616C697A6F77616E6F2062617AC4992E205779736F6B6FC59BC4872052544820646F73746F736F77616E6120646F2038356D29000000000070A7402801");

		(string? code, string text) = FlightEventTracker.ReadMessage(record)!.Value;

		Assert.AreEqual("30213", code);
		Assert.AreEqual("Zaktualizowano bazę. Wysokość RTH dostosowana do 85m", text);
	}

	[TestMethod]
	public void ReadMessage_ReadsANoticeWithoutACodeAndIgnoresTheOnesThatClearIt()
	{
		(string? code, string text) = FlightEventTracker.ReadMessage(Convert.FromHexString("0801120E0A0A57206C6F636965E280A618013001"))!.Value;

		Assert.IsNull(code);
		Assert.AreEqual("W locie…", text);
		Assert.IsNull(FlightEventTracker.ReadMessage(Convert.FromHexString("08012801")));
		Assert.IsNull(FlightEventTracker.ReadMessage(Convert.FromHexString("0801120E0A0A57")), "a cut record");
	}

	[TestMethod]
	public void Decode_TheAppsMessagesAreWarningsUnlessTheirCodeIsTheAppsOwnNotice()
	{
		string Hex(string code, string text) => $"08011A{(7 + code.Length + text.Length):X2}0801" + $"12{code.Length:X2}{Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(code))}" +
		                                          $"1A{text.Length:X2}{Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(text))}" + "2801";
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdRecord(50, 20, 0, 0, 0, 10, true));
		builder.Add(54, Convert.FromHexString(Hex("180038", "Vision sensor blocked")));
		builder.Add(54, Convert.FromHexString(Hex("20000001", "Max altitude reached")));

		FlightEvent[] events = [.. EventsOf(builder).Select(e => e.Event).Where(e => e.Kind is FlightEventKind.Warning or FlightEventKind.Tip)];

		Assert.AreEqual(FlightEventKind.Warning, events[0].Kind);
		Assert.AreEqual("180038", events[0].Code);
		Assert.AreEqual(FlightEventKind.Tip, events[1].Kind);
	}

	[TestMethod]
	public void Apply_EventsLandOnTheirFramesAndOnesOutsideTheRecordingAreLeftOut()
	{
		var t0 = new DateTime(2026, 10, 29, 13, 0, 0, DateTimeKind.Utc);
		var track = new ExternalTrack(
			[new ExternalTrackPoint(t0, 50, 20), new ExternalTrackPoint(t0.AddSeconds(10), 50.001, 20)],
			droneBatteries: [new ExternalDroneBatteryReading(t0, new DroneBattery(80, 11)), new ExternalDroneBatteryReading(t0.AddSeconds(5), new DroneBattery(79, 10.9))],
			events:
			[
				new ExternalTrackEvent(t0.AddSeconds(1), new FlightEvent(FlightEventKind.MotorsOn)),
				new ExternalTrackEvent(t0.AddSeconds(1), new FlightEvent(FlightEventKind.FlightMode, "Sport")),
				new ExternalTrackEvent(t0.AddSeconds(50), new FlightEvent(FlightEventKind.MotorsOff))
			]);
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, 100).Select(i => new TelemetryFrame(i, i / 10.0, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false))];

		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(frames, track, t0);

		Assert.AreEqual(2, merged[10].FlightEvents!.Count);
		Assert.AreEqual(2, merged.Count(f => f.FlightEvents is not null) + 1, "both events on one frame, the one at 50 s dropped");
		Assert.AreEqual(80, merged[20].DroneBattery!.Percent);
		Assert.AreEqual(79, merged[60].DroneBattery!.Percent, "the latest reading is held");
	}

	[TestMethod]
	public void TheWidgetsNeedTheirOwnReadings()
	{
		TelemetryFrame frame = new(0, 0, 50, 20, 100, null, 0, 0, 1);

		Assert.IsFalse(OverlayAvailability.Of([frame], false, null).DroneBattery);
		Assert.IsFalse(OverlayAvailability.Of([frame], false, null).FlightAlerts);
		Assert.IsTrue(OverlayAvailability.Of([frame with { DroneBattery = new DroneBattery(0) }], false, null).DroneBattery);
		Assert.IsTrue(OverlayAvailability.Of([frame with { FlightEvents = [new FlightEvent(FlightEventKind.MotorsOn)] }], false, null).FlightAlerts);
		Assert.IsFalse(OverlayAvailability.Of([frame with { FlightEvents = [] }], false, null).FlightAlerts);
	}

	[TestMethod]
	public void TheBatteryColorRunsFromGreenThroughYellowAndOrangeToRedAtTheElementsOwnLevels()
	{
		var battery = new DroneBatteryElement { X = 0, Y = 0, LowPercent = 40, CriticalPercent = 10 };

		Assert.AreEqual(SKColor.Parse("#4CAF50"), OverlayRenderer.DroneBatteryColor(battery, 100));
		Assert.AreEqual(SKColor.Parse("#FFC107"), OverlayRenderer.DroneBatteryColor(battery, 70));
		Assert.AreEqual(SKColor.Parse("#FF9800"), OverlayRenderer.DroneBatteryColor(battery, 40));
		Assert.AreEqual(SKColor.Parse("#F44336"), OverlayRenderer.DroneBatteryColor(battery, 10));
		Assert.AreEqual(SKColor.Parse("#F44336"), OverlayRenderer.DroneBatteryColor(battery, 0));
		SKColor between = OverlayRenderer.DroneBatteryColor(battery, 85);
		Assert.IsTrue(between.Green > 0xAF && between.Red > 0x4C && between.Red < 0xFF, "between green and yellow");
		Assert.AreEqual(SKColor.Parse("#F44336"), OverlayRenderer.DroneBatteryColor(new DroneBatteryElement { X = 0, Y = 0, LowPercent = 10, CriticalPercent = 30 }, 20), "levels the wrong way round read as one");
	}

	[TestMethod]
	public void TheBatteryLinesTurnOrangeOrRedOnlyWhenTheirReadingIsPoor()
	{
		var element = new DroneBatteryElement { X = 0, Y = 0, ShowTemperature = true, ShowCurrent = true, ShowCells = true };
		BatteryCondition[] Conditions(DroneBattery battery) => [.. OverlayRenderer.DroneBatteryRows(element, battery).Select(r => r.Condition)];

		CollectionAssert.AreEqual(new[] { BatteryCondition.Normal, BatteryCondition.Normal, BatteryCondition.Normal, BatteryCondition.Normal },
			Conditions(new DroneBattery(60, 11.2, 7, 31, [3.74, 3.73, 3.72])));
		CollectionAssert.AreEqual(new[] { BatteryCondition.Caution, BatteryCondition.Caution, BatteryCondition.Normal, BatteryCondition.Caution },
			Conditions(new DroneBattery(20, 10.1, 7, 42, [3.41, 3.37, 3.35])), "a low cell and a warm pack; the current has no limit");
		CollectionAssert.AreEqual(new[] { BatteryCondition.Critical, BatteryCondition.Critical, BatteryCondition.Normal, BatteryCondition.Critical },
			Conditions(new DroneBattery(5, 9.7, 7, 55, [3.3, 3.2, 3.2])));
		Assert.AreEqual(BatteryCondition.Caution, DroneBatteryHealth.Temperature(5), "cold");
		Assert.AreEqual(BatteryCondition.Normal, DroneBatteryHealth.Temperature(null));
		Assert.AreEqual(BatteryCondition.Normal, DroneBatteryHealth.Cells(new DroneBattery(50)), "no cells, nothing to judge");
	}

	[TestMethod]
	public void TheBatteryLimitsAreTheElementsOwn()
	{
		var battery = new DroneBattery(50, 11.1, 5, 36, [3.55, 3.54, 3.53]);
		var element = new DroneBatteryElement { X = 0, Y = 0, ShowTemperature = true, ShowCells = true };

		Assert.AreEqual(BatteryCondition.Normal, OverlayRenderer.DroneBatteryRows(element, battery)[0].Condition);
		Assert.AreEqual(BatteryCondition.Caution, OverlayRenderer.DroneBatteryRows(element with { LowCellVolts = 3.6 }, battery)[0].Condition, "a stricter low cell");
		Assert.AreEqual(BatteryCondition.Critical, OverlayRenderer.DroneBatteryRows(element with { CriticalCellVolts = 3.55 }, battery)[0].Condition);
		Assert.AreEqual(BatteryCondition.Caution, OverlayRenderer.DroneBatteryRows(element with { HotCautionCelsius = 35 }, battery)[1].Condition, "a lower hot limit");
		Assert.AreEqual(BatteryCondition.Critical, OverlayRenderer.DroneBatteryRows(element with { ColdCriticalCelsius = 36 }, battery)[1].Condition);
	}

	[TestMethod]
	public void TheBatteryLinesFollowWhatIsSwitchedOn()
	{
		var battery = new DroneBattery(21, 10.6, 7, 38.9, [3.55, 3.52, 3.52]);
		var element = new DroneBatteryElement { X = 0, Y = 0, ShowTemperature = true, ShowCurrent = true, ShowCells = true };

		CollectionAssert.AreEqual(new[] { "10.6 V", "38.9 °C", "7.0 A", "3.55 / 3.52 / 3.52 V" }, OverlayRenderer.DroneBatteryLines(element, battery));
		CollectionAssert.AreEqual(new[] { "102.0 °F" }, OverlayRenderer.DroneBatteryLines(new DroneBatteryElement { X = 0, Y = 0, ShowVoltage = false, ShowTemperature = true, Units = UnitSystem.Imperial }, battery));
		CollectionAssert.AreEqual(new[] { "-- V" }, OverlayRenderer.DroneBatteryLines(new DroneBatteryElement { X = 0, Y = 0 }, null));
		Assert.AreEqual(0, OverlayRenderer.DroneBatteryLines(new DroneBatteryElement { X = 0, Y = 0, ShowVoltage = false }, battery).Length);
	}

	[TestMethod]
	public void FlightAlertsShowAnEventForTheirTimeAndOnlyTheKindsPicked()
	{
		FlightEvent[] events = [new(FlightEventKind.FlightMode, "Sport"), new(FlightEventKind.Warning, "Careful", Code: "180038")];
		List<TelemetryFrame> raw =
		[
			.. Enumerable.Range(0, 200).Select(i => new TelemetryFrame(i, i / 10.0, 50, 20, 100, null, 0, 0, 1,
				FlightEvents: i == 20 ? [events[0]] : i == 40 ? [events[1]] : null))
		];
		List<DerivedFrame> frames = TelemetryProcessor.Process(raw, null);
		using var renderer = new OverlayRenderer(1280, 720, 0, [], frames, showWatermark: false);
		var element = new FlightAlertsElement { X = 640, Y = 20, ShowSeconds = 3 };

		Assert.IsNull(renderer.MeasureElement(element, frames[10]), "nothing before the first event");
		Assert.IsNotNull(renderer.MeasureElement(element, frames[30]), "the mode change is up");
		Assert.IsNull(renderer.MeasureElement(element with { ShowFlightModes = false }, frames[30]));
		Assert.IsNull(renderer.MeasureElement(element, frames[100]), "and gone after its time");
		Assert.IsNotNull(renderer.MeasureElement(element, frames[50]));
		Assert.IsNull(renderer.MeasureElement(element with { ShowWarnings = false }, frames[50]));
		Assert.AreEqual(renderer.MeasureElement(element, frames[45])!.Value.Height, renderer.MeasureElement(element, frames[50])!.Value.Height, 1, "one line shows only the newest");
		float one = renderer.MeasureElement(element with { MaxLines = 2 }, frames[50])!.Value.Height;
		float two = renderer.MeasureElement(element with { MaxLines = 2 }, frames[45])!.Value.Height;
		Assert.IsTrue(two > one + OverlayElementBounds.GpsAlertHeight / 2, "two events up at once are stacked");
	}

	[TestMethod]
	public void FlightAlerts_NewFramesDropTheEventsOfTheOldOnes()
	{
		static List<DerivedFrame> Frames(bool withEvent) => TelemetryProcessor.Process(
		[
			.. Enumerable.Range(0, 100).Select(i => new TelemetryFrame(i, i / 10.0, 50, 20, 100, null, 0, 0, 1,
				FlightEvents: withEvent && i == 20 ? [new FlightEvent(FlightEventKind.FlightMode, "Sport")] : null))
		], null);

		List<DerivedFrame> before = Frames(true), after = Frames(false);
		using var renderer = new OverlayRenderer(1280, 720, 0, [], before, showWatermark: false);
		var element = new FlightAlertsElement { X = 640, Y = 20, ShowSeconds = 3 };
		Assert.IsNotNull(renderer.MeasureElement(element, before[30]));

		renderer.SetFrames(after, 0, 0);

		Assert.IsNull(renderer.MeasureElement(element, after[30]));
	}

	[TestMethod]
	public void FlightAlertTextsNameTheKindAndTheDetail()
	{
		Assert.AreEqual("Sport", OverlayRenderer.FlightAlertText(new FlightEvent(FlightEventKind.FlightMode, "Sport")).Detail);
		Assert.AreEqual("Battery reached 40%", OverlayRenderer.FlightAlertText(new FlightEvent(FlightEventKind.BatteryLevel, Value: 40)).Detail);
		Assert.AreEqual("Lowest cell voltage: 3.47 V", OverlayRenderer.FlightAlertText(new FlightEvent(FlightEventKind.LowVoltage, Value: 3.466)).Detail);
		Assert.AreEqual("Careful (code 180038)", OverlayRenderer.FlightAlertText(new FlightEvent(FlightEventKind.Warning, "Careful", Code: "180038")).Detail);
		Assert.AreEqual("In flight", OverlayRenderer.FlightAlertText(new FlightEvent(FlightEventKind.Tip, "In flight")).Detail);
	}

	[TestMethod]
	public void TheBatteryWidgetDrawsWithAndWithoutAReading()
	{
		TelemetryFrame raw = new(0, 0, 50, 20, 100, null, 0, 0, 1, DroneBattery: new DroneBattery(21, 10.6));
		List<DerivedFrame> frames = TelemetryProcessor.Process([raw, raw with { FrameNumber = 1, SampleTimeSeconds = 1, DroneBattery = null }], null);
		using var renderer = new OverlayRenderer(1280, 720, 0, [], frames, showWatermark: false);
		var element = new DroneBatteryElement { X = 100, Y = 100 };

		SKRectProbe(renderer.MeasureElement(element, frames[0]));
		SKRectProbe(renderer.MeasureElement(element, frames[1]));
		Assert.IsTrue(renderer.MeasureElement(element with { ShowIcon = false }, frames[0])!.Value.Width < renderer.MeasureElement(element, frames[0])!.Value.Width);

		static void SKRectProbe(SkiaSharp.SKRect? box) => Assert.IsTrue(box is { Width: > 0, Height: > 0 });
	}
}
