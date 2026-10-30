using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Dji.Drones;

/// <summary>
///     What happened during the flight, from the records in file order: flight mode and motor changes (OSD), the battery's charge
///     and cell voltages (SmartBatteryGroup) and the messages the app wrote (type 54). Offsets checked against a real Mavic Air 2
///     log (version 14) and a Flight Reader export of it. Battery levels and low cell voltages are counted only once the motors
///     have run - a pack at rest says nothing about the flight.
/// </summary>
internal sealed class FlightEventTracker
{
	private const int ModeOffset = 30, FlagsOffset = 32;
	private const byte ModeMask = 0x7F, MotorsOnFlag = 0x04;

	// Cell voltage, lowest to highest severity: each is announced once on the way down and again after the voltage has come back 0.1 V above it.
	private static readonly (double Volts, FlightEventKind Kind)[] VoltageSteps =
		[(DroneBatteryHealth.LowCellVolts, FlightEventKind.LowVoltage), (3.35, FlightEventKind.LowVoltage), (DroneBatteryHealth.CriticalCellVolts, FlightEventKind.CriticalVoltage)];

	private const double VoltageRearm = 0.1;
	private const int BatteryStep = 10;

	// A message code from this one up is the app's own notice (the maximum altitude), not something to be careful about.
	private const long TipCodeFrom = 20_000_000;

	private readonly bool[] _disarmed = new bool[VoltageSteps.Length];
	private int? _mode;
	private bool? _motorsOn;
	private bool _flying, _homeSeen;
	private int? _nextLevel;
	private double _percent = -1, _voltage, _current, _temperature, _capacity;
	private double[] _cells = [];
	private double? _uplink, _downlink;

	public List<FlightLogEvent> Events { get; } = [];
	public List<FlightBatterySample> Batteries { get; } = [];
	public List<FlightSignalSample> Signals { get; } = [];
	public List<FlightSticksSample> Sticks { get; } = [];

	// Type 4: five u16 (aileron, elevator, throttle, rudder, gimbal dial) from 364 to 1684 around 1024, as the remote's channels run.
	private const double StickCenter = 1024, StickRange = 660;

	/// <summary>The sticks in the remote's order; a record that holds anything outside the channels' range is not a reading.</summary>
	public void OnSticks(byte[] c, double seconds)
	{
		double[] channels = new double[4];
		for (int i = 0; i < channels.Length; i++)
		{
			int raw = BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(i * 2));
			if (raw is < 200 or > 1850) return;

			channels[i] = Math.Clamp((raw - StickCenter) / StickRange, -1, 1);
		}

		Sticks.Add(new FlightSticksSample(seconds, new DroneSticks(channels[0], channels[1], channels[2], channels[3])));
	}

	public void OnOsd(byte[] c, double seconds)
	{
		int mode = c[ModeOffset] & ModeMask;
		if (_mode != mode) Events.Add(new FlightLogEvent(seconds, new FlightEvent(FlightEventKind.FlightMode, FlightModes.Name(mode), mode)));
		_mode = mode;

		bool motors = (c[FlagsOffset] & MotorsOnFlag) != 0;
		if (_motorsOn is { } before && before != motors)
		{
			Events.Add(new FlightLogEvent(seconds, new FlightEvent(motors ? FlightEventKind.MotorsOn : FlightEventKind.MotorsOff)));
			if (motors) StartFlight(seconds);
			else EndFlight(seconds);
		}

		_motorsOn = motors;
	}

	/// <summary>
	///     The first home record's return-to-home altitude (u16 at 22, metres) and altitude limit (float at 37, metres), announced once
	///     at the start like the app does.
	/// </summary>
	public void OnHome(byte[] c, double seconds)
	{
		if (_homeSeen || c.Length < 41) return;

		_homeSeen = true;
		int returnAltitude = BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(22));
		float limit = BitConverter.ToSingle(c, 37);
		if (returnAltitude is > 0 and <= 1000) Events.Add(new FlightLogEvent(seconds, new FlightEvent(FlightEventKind.ReturnAltitude, Value: returnAltitude)));
		if (limit is > 0 and <= 1000) Events.Add(new FlightLogEvent(seconds, new FlightEvent(FlightEventKind.AltitudeLimit, Value: Math.Round(limit))));
	}

	/// <summary>Type 22: record 2 holds the pack (voltage, current, charge, temperature), record 3 the cells.</summary>
	public void OnBattery(byte[] c, double seconds)
	{
		if (c.Length >= 22 && c[0] == 2 && c[21] <= 100)
		{
			_voltage = BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(2)) / 1000.0;
			_current = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(6))) / 1000.0;
			_capacity = BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(14));
			_temperature = BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(18)) / 10.0;
			_percent = c[21];
		}
		else if (c.Length >= 3 && c[0] == 3)
		{
			int count = Math.Min(c[2], (c.Length - 3) / 2);
			_cells = [.. Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(c.AsSpan(3 + i * 2)) / 1000.0)];
		}
		else return;

		if (_percent < 0 || _voltage <= 0) return;

		var battery = new DroneBattery(_percent, _voltage, _current, _temperature, _cells.Length > 0 ? _cells : null, _capacity);
		Batteries.Add(new FlightBatterySample(seconds, battery));
		if (!_flying) return;

		while (_nextLevel is { } level && _percent <= level)
		{
			Events.Add(new FlightLogEvent(seconds, new FlightEvent(FlightEventKind.BatteryLevel, Value: level)));
			_nextLevel = level - BatteryStep >= 0 ? level - BatteryStep : null;
		}

		if (battery.MinCellVoltage is { } cell) CheckVoltage(cell, seconds);
	}

	/// <summary>
	///     Type 49 carries one byte per link: the quality in percent in the low seven bits, and the high bit says which link (set:
	///     uplink). Both streams were seen to run together in one log; which is which has not been confirmed against the app.
	/// </summary>
	public void OnSignal(byte value, double seconds)
	{
		int percent = value & 0x7F;
		if (percent > 100) return;

		if ((value & 0x80) != 0) _uplink = percent;
		else _downlink = percent;
		Signals.Add(new FlightSignalSample(seconds, new DroneSignal(_uplink, _downlink)));
	}

	/// <summary>Type 54: a message of the app - protobuf, with the code and text in one of two shapes (see ReadMessage).</summary>
	public void OnMessage(byte[] c, double seconds)
	{
		if (ReadMessage(c) is not { } message || message.Text.Length == 0) return;
		(string? code, string text) = message;

		bool tip = code is null || !long.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number >= TipCodeFrom;
		Events.Add(new FlightLogEvent(seconds, new FlightEvent(tip ? FlightEventKind.Tip : FlightEventKind.Warning, text, Code: code)));
	}

	private void StartFlight(double seconds)
	{
		_flying = true;
		Array.Clear(_disarmed);
		if (_percent < 0) return;

		Events.Add(new FlightLogEvent(seconds, new FlightEvent(FlightEventKind.BatteryStart, Value: _percent)));
		_nextLevel = _percent > 0 ? ((int)_percent - 1) / BatteryStep * BatteryStep : null;
	}

	private void EndFlight(double seconds)
	{
		if (_flying && _percent >= 0) Events.Add(new FlightLogEvent(seconds, new FlightEvent(FlightEventKind.BatteryEnd, Value: _percent)));
		_flying = false;
	}

	private void CheckVoltage(double cell, double seconds)
	{
		int crossed = -1;
		for (int i = 0; i < VoltageSteps.Length; i++)
		{
			if (cell > VoltageSteps[i].Volts + VoltageRearm) _disarmed[i] = false;
			else if (cell < VoltageSteps[i].Volts && !_disarmed[i])
			{
				_disarmed[i] = true;
				crossed = i;
			}
		}

		if (crossed >= 0) Events.Add(new FlightLogEvent(seconds, new FlightEvent(VoltageSteps[crossed].Kind, Value: cell)));
	}

	/// <summary>
	///     The message's code and text. Either field 3 holds {2: code, 3: text} (a warning or a notice), or field 2 holds {1: text}
	///     with no code ("In flight..."); a record with neither (the 4-byte ones that clear a message) gives null.
	/// </summary>
	internal static (string? Code, string Text)? ReadMessage(ReadOnlySpan<byte> record)
	{
		string? code = null, text = null;
		int at = 0;
		while (NextField(record, ref at, out int field, out ReadOnlySpan<byte> body))
		{
			if (field == 3)
			{
				int inner = 0;
				while (NextField(body, ref inner, out int innerField, out ReadOnlySpan<byte> value))
				{
					if (innerField == 2) code = Encoding.UTF8.GetString(value);
					else if (innerField == 3) text = Encoding.UTF8.GetString(value);
				}
			}
			else if (field == 2)
			{
				int inner = 0;
				while (NextField(body, ref inner, out int innerField, out ReadOnlySpan<byte> value))
				{
					if (innerField == 1) text = Encoding.UTF8.GetString(value);
				}
			}
		}

		return text is null ? null : (code, text);
	}

	/// <summary>The next length-delimited field's number and bytes - other wire types are stepped over; false at the end or on damage.</summary>
	private static bool NextField(ReadOnlySpan<byte> data, ref int at, out int field, out ReadOnlySpan<byte> body)
	{
		while (at < data.Length)
		{
			if (!ReadVarint(data, ref at, out ulong tag)) break;

			field = (int)(tag >> 3);
			switch (tag & 7)
			{
				case 0:
					if (!ReadVarint(data, ref at, out _)) goto done;
					break;
				case 1:
					at += 8;
					break;
				case 5:
					at += 4;
					break;
				case 2:
					if (!ReadVarint(data, ref at, out ulong length) || length > (ulong)(data.Length - at)) goto done;
					body = data.Slice(at, (int)length);
					at += (int)length;
					return true;
				default:
					goto done;
			}
		}

		done:
		at = data.Length;
		field = 0;
		body = default;
		return false;
	}

	private static bool ReadVarint(ReadOnlySpan<byte> data, ref int at, out ulong value)
	{
		value = 0;
		for (int shift = 0; shift < 64 && at < data.Length; shift += 7)
		{
			byte b = data[at++];
			value |= (ulong)(b & 0x7F) << shift;
			if ((b & 0x80) == 0) return true;
		}

		return false;
	}
}

/// <summary>
///     The flight mode's name by the OSD's state number. 6, 10, 31, 33 and 41 were checked against a real log; the rest come from
///     the open dji-log-parser and are not yet confirmed.
/// </summary>
internal static class FlightModes
{
	public static string Name(int mode)
	{
		return mode switch
		{
			1 => "ATTI",
			6 => "P-GPS",
			10 => "Manual Takeoff",
			11 => "Auto Takeoff",
			12 => "Auto Landing",
			15 => "Go Home",
			31 => "Sport",
			33 => "Confirm Landing",
			41 => "Starting Motors",
			_ => "Mode " + mode.ToString(CultureInfo.InvariantCulture)
		};
	}
}
