using System.Buffers.Binary;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Dji.Drones;

/// <summary>
///     A DJI flight log (DJI Fly's or GO 4's FlightRecord .txt): a 100-byte prefix, the details of the flight, and the records
///     the aircraft and the app wrote about 10 times a second. The details are readable as they are; from version 13 the records
///     are AES-encrypted and need the keychains DJI issues for the log (DjiKeychainClient). Layout after the open reference parser
///     dji-log-parser; the fields used here were checked against a real Mavic 2 log (v14) and a Mavic Air 2 log with a Flight Reader CSV of it.
/// </summary>
/// <remarks>
///     Offsets of the records used (checked as said above):
///     OSD (type 1): flight mode = byte 30 and 0x7F; motors on = bit 0x04 of byte 32; pitch, roll, yaw, z-speed = i16/10 at 24, 26, 28, 22
///     (pitch nose up +, roll right bank +, yaw clockwise from north); fly time = u16/10 at 42 (stands still with the motors off);
///     GPS level = (byte 34 >> 2) and 0x0F (0-5). Its "battery" byte is not a percentage.
///     Battery (type 22): magic 2: pack mV u32 at 2, mA i32 at 6 (negative while discharging), capacity mAh at 14, tenths of a degree
///     i16 at 18, percent at 21; magic 3: cell count at 2, cell mV u16 from 3.
///     App message (type 54): protobuf, field 3 = {2: code, 3: text} or field 2 = {1: text} with no code.
///     Home (type 2): return-to-home altitude u16 at 22 (m), altitude limit float at 37. Component (type 13): the aircraft's name,
///     32 bytes at 21. Camera (type 25, is_recording): where a recording sits on the log.
///     Sticks (type 4): u16 at 0, 2, 4, 6 = aileron, elevator, throttle, rudder (364-1684 around 1024); checked against the flight
///     (throttle vs climb rate r = 0.86, rudder vs yaw rate 0.84, elevator vs pitch -0.87, aileron vs roll 0.54).
///     Radio link (type 49, one-byte record): quality percent in the low seven bits, the high bit set for one link and clear for the
///     other - which is the uplink is NOT confirmed. The 15-byte records are not read.
///     Details block: float km at 115, ms at 119, then maximum height and speed (FlightLog.Totals).
///     Sample count equals the header's record_line_count; the maximum height, speed and path length equal the header's totals.
/// </remarks>
public sealed class FlightLog
{
	internal const int PrefixSize = 100;
	private const int EncryptedFromVersion = 13;

	private const byte OsdRecord = 1, HomeRecord = 2, RcRecord = 4, CustomRecord = 5, CameraRecord = 25, SmartBatteryGroupRecord = 22, AirLinkRecord = 49, ComponentRecord = 13, AppMessageRecord = 54, KeyStorageRecoverRecord = 50, KeyStorageRecord = 56;
	private const byte EndByte = 0xFF;
	private const long MinPlausibleMs = 1_262_304_000_000, MaxPlausibleMs = 4_102_444_800_000;
	private const int DetailsReadSize = 512;

	// The OSD record is written at 10 Hz; its own fly time stands still while the motors are off, so the sample index is the clock.
	private const double SampleSeconds = 0.1;
	private const int VerticalSpeedHalfWindow = 5;

	// The component record (type 13): the aircraft's serial number, then its name as its owner gave it, then the camera's, the remote's and the battery's serials.
	private const int AircraftNameOffset = 21, AircraftNameLength = 32;

	private readonly byte[] _file;
	private readonly int _recordsOffset, _recordsEnd;

	private FlightLog(byte[] file, int version, int recordsOffset, int recordsEnd, DateTime? startedUtc, byte department, ushort keychainVersion)
	{
		_file = file;
		Version = version;
		_recordsOffset = recordsOffset;
		_recordsEnd = recordsEnd;
		StartedUtc = startedUtc;
		Department = department;
		KeychainVersion = keychainVersion;
	}

	public int Version { get; }

	/// <summary>When the flight started by the clock of the app that wrote the log - which is not always right.</summary>
	public DateTime? StartedUtc { get; }

	/// <summary>What the details block says the flight came to - null where it is too short to say.</summary>
	public FlightTotals? Totals { get; private init; }

	internal byte Department { get; }
	internal ushort KeychainVersion { get; }

	public bool IsEncrypted => Version >= EncryptedFromVersion;

	public static bool LooksLikeFlightLog(ReadOnlySpan<byte> start)
	{
		if (start.Length < 12) return false;
		ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(start);
		return start[10] is >= 1 and <= 30 && offset is >= 12 and < 1 << 24;
	}

	public static FlightLog Parse(byte[] file)
	{
		if (file.Length < PrefixSize || !LooksLikeFlightLog(file)) throw new InvalidDataException("Not a DJI flight log.");

		int version = file[10];
		long prefixOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(file);
		int detailOffset = version < 12 ? (int)prefixOffset : PrefixSize;
		if (detailOffset >= file.Length || prefixOffset > file.Length) throw new InvalidDataException("The flight log is cut short.");

		DateTime? started = null;
		FlightTotals? totals = null;
		byte department = 0;
		ushort keychainVersion = 0;
		int recordsOffset, recordsEnd = file.Length;
		if (version >= EncryptedFromVersion)
		{
			int at = detailOffset;
			byte[] info = ReadAuxiliaryInfo(file, ref at) ?? throw new InvalidDataException("The flight log has no details block.");
			started = ReadStartTime(info);
			totals = ReadTotals(info);
			if (at + 6 > file.Length || file[at] != 1) throw new InvalidDataException("The flight log has no version block.");
			keychainVersion = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 3));
			department = file[at + 5];
			recordsOffset = (int)prefixOffset > 0 ? (int)prefixOffset : at + 3 + BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 1));
		}
		else
		{
			started = ReadStartTime(file.AsSpan(detailOffset));
			totals = ReadTotals(file.AsSpan(detailOffset));
			recordsOffset = version < 6 ? 12 : version == 12 ? PrefixSize + 436 : PrefixSize;
			if (version < 12) recordsEnd = detailOffset;
		}

		return new FlightLog(file, version, recordsOffset, Math.Min(recordsEnd, file.Length), started, department, keychainVersion) { Totals = totals };
	}

	public static FlightLog Read(string path)
	{
		return Parse(File.ReadAllBytes(path));
	}

	/// <summary>
	///     The start and the totals of a log's details block, read without the rest of the file (which is megabytes) - enough to tell
	///     which flight a log is. Throws InvalidDataException or IOException for a file that isn't a whole log.
	/// </summary>
	public static FlightLogHeader ReadHeader(string path)
	{
		using FileStream stream = File.OpenRead(path);
		byte[] prefix = new byte[PrefixSize];
		if (stream.Read(prefix) < PrefixSize || !LooksLikeFlightLog(prefix)) throw new InvalidDataException("Not a DJI flight log.");

		int version = prefix[10];
		if (version < EncryptedFromVersion)
		{
			if (version < 12) stream.Seek((long)BinaryPrimitives.ReadUInt64LittleEndian(prefix), SeekOrigin.Begin);
			byte[] details = new byte[DetailsReadSize];
			int read = stream.Read(details);
			return new FlightLogHeader(ReadStartTime(details.AsSpan(0, read)), ReadTotals(details.AsSpan(0, read)));
		}

		byte[] head = new byte[3];
		stream.ReadExactly(head);
		byte[] block = new byte[3 + BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(1))];
		head.CopyTo(block, 0);
		stream.ReadExactly(block.AsSpan(3));
		int at = 0;
		byte[] info = ReadAuxiliaryInfo(block, ref at) ?? throw new InvalidDataException("The flight log has no details block.");
		return new FlightLogHeader(ReadStartTime(info), ReadTotals(info));
	}

	private static byte[]? ReadAuxiliaryInfo(byte[] file, ref int at)
	{
		if (at + 4 > file.Length || file[at] != 0) return null;
		int length = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 1));
		int start = at + 3;
		if (length < 4 || start + length > file.Length) return null;

		byte[] data = file.AsSpan(start + 1, length - 1).ToArray();
		FlightLogCrypto.Xor(data, file[start], 0);
		at = start + length;

		int infoLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1));
		return 3 + infoLength <= data.Length ? data.AsSpan(3, infoLength).ToArray() : null;
	}

	/// <summary>The details' start time: 4 street/city names of 20 bytes, 3 flags, 2 counters, then milliseconds since 1970.</summary>
	private static DateTime? ReadStartTime(ReadOnlySpan<byte> details)
	{
		const int At = 4 * 20 + 3 + 2 * 4;
		if (details.Length < At + 8) return null;
		long milliseconds = BinaryPrimitives.ReadInt64LittleEndian(details[At..]);
		return ToUtc(milliseconds);
	}

	private static DateTime? ToUtc(long milliseconds)
	{
		return milliseconds is > MinPlausibleMs and < MaxPlausibleMs ? DateTime.UnixEpoch.AddMilliseconds(milliseconds) : null;
	}

	/// <summary>The details' totals after the start time: position (2 doubles), distance flown (float, km), time (ms), maximum height, horizontal and vertical speed (floats, metres and m/s).</summary>
	private static FlightTotals? ReadTotals(ReadOnlySpan<byte> details)
	{
		const int At = 115;
		if (details.Length < At + 20) return null;

		double kilometers = BitConverter.ToSingle(details[At..]), seconds = BinaryPrimitives.ReadUInt32LittleEndian(details[(At + 4)..]) / 1000.0;
		double height = BitConverter.ToSingle(details[(At + 8)..]), speed = BitConverter.ToSingle(details[(At + 12)..]);
		return double.IsFinite(kilometers) && double.IsFinite(height) && double.IsFinite(speed) && kilometers is >= 0 and < 5000 && seconds is > 0 and < 86400 && height is >= 0 and < 10000 && speed is >= 0 and < 200
			? new FlightTotals(kilometers, seconds, height, speed)
			: null;
	}

	/// <summary>What DJI needs to say which keys belong to this log: the key blocks the log carries, grouped by recording session.</summary>
	public KeychainsRequest BuildKeychainsRequest()
	{
		var request = new KeychainsRequest(KeychainVersion, Department == 0 ? (byte)3 : Department, []);
		if (!IsEncrypted) return request;

		List<(int FeaturePoint, byte[] Data)> current = [];
		foreach ((byte type, byte[] content) in ReadRecords(null, keyBlocksOnly: true))
		{
			if (type == KeyStorageRecord && content.Length >= 4)
			{
				int length = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(2)), content.Length - 4);
				current.Add((BinaryPrimitives.ReadUInt16LittleEndian(content), content.AsSpan(4, length).ToArray()));
			}
			else if (type == KeyStorageRecoverRecord)
			{
				request.Keychains.Add(current);
				current = [];
			}
		}

		request.Keychains.Add(current);
		return request;
	}

	/// <summary>The samples and the recording spans of the flight. `keychains` are required from version 13.</summary>
	public FlightLogData Decode(IReadOnlyList<Keychain>? keychains)
	{
		if (IsEncrypted && keychains is not { Count: > 0 }) throw new InvalidOperationException("The log is encrypted - its keychains are needed.");

		List<FlightSample> samples = [];
		List<FlightRecording> recordings = [];
		List<long> clockOffsets = [];
		double homeAltitude = 0;
		double? recordingSince = null;
		var tracker = new FlightEventTracker();
		string? aircraftName = null;
		FlightHome? home = null;
		int osdRecords = 0;

		foreach ((byte type, byte[] content) in ReadRecords(keychains))
		{
			double now = Math.Max(osdRecords - 1, 0) * SampleSeconds;
			switch (type)
			{
				case OsdRecord when content.Length >= OsdLayout.MinLength:
					samples.Add(ReadSample(content, osdRecords * SampleSeconds, homeAltitude));
					tracker.OnOsd(content, osdRecords * SampleSeconds);
					osdRecords++;
					break;
				case RcRecord when content.Length >= 10:
					tracker.OnSticks(content, now);
					break;
				case SmartBatteryGroupRecord:
					tracker.OnBattery(content, now);
					break;
				case ComponentRecord when content.Length >= AircraftNameOffset + AircraftNameLength && aircraftName is null:
					aircraftName = ReadName(content.AsSpan(AircraftNameOffset, AircraftNameLength));
					break;
				case AirLinkRecord when content.Length == 1:
					tracker.OnSignal(content[0], now);
					break;
				case AppMessageRecord when content.Length > 4:
					tracker.OnMessage(content, now);
					break;
				case HomeRecord when content.Length >= 20:
					homeAltitude = BitConverter.ToSingle(content, 16) / 10.0;
					tracker.OnHome(content, now);
					FlightHome position = new(AngleMath.RadToDeg(BitConverter.ToDouble(content, 8)), AngleMath.RadToDeg(BitConverter.ToDouble(content, 0)));
					if (home is null && Math.Abs(position.Latitude) is > 0 and <= 90 && Math.Abs(position.Longitude) <= 180) home = position;
					break;
				case CustomRecord when content.Length >= 18:
					if (ToUtc(BinaryPrimitives.ReadInt64LittleEndian(content.AsSpan(10))) is { } stamp && osdRecords > 0)
						clockOffsets.Add(stamp.Ticks - (long)Math.Round(now * TimeSpan.TicksPerSecond));
					break;
				case CameraRecord when content.Length >= 1:
					bool recording = (content[0] & 0xC0) != 0;
					if (recording && recordingSince is null) recordingSince = now;
					else if (!recording && recordingSince is { } since)
					{
						recordings.Add(new FlightRecording(since, now));
						recordingSince = null;
					}

					break;
			}
		}

		if (recordingSince is { } open && samples.Count > 0) recordings.Add(new FlightRecording(open, samples[^1].Seconds));

		DateTime? clock = null;
		if (clockOffsets.Count > 0)
		{
			clockOffsets.Sort();
			clock = new DateTime(clockOffsets[clockOffsets.Count / 2], DateTimeKind.Utc);
		}

		return new FlightLogData(WithVerticalSpeed(samples), recordings, clock, home, tracker.Batteries, tracker.Events, tracker.Signals, aircraftName, tracker.Sticks);
	}

	/// <summary>
	///     The climb rate from the height over a second around each sample: the height comes in steps of 0.1 m, so the difference of
	///     two neighbours (10 per second) would only ever be 0, +-1 or +-2 m/s.
	/// </summary>
	private static List<FlightSample> WithVerticalSpeed(List<FlightSample> samples)
	{
		var result = new List<FlightSample>(samples.Count);
		for (int i = 0; i < samples.Count; i++)
		{
			FlightSample from = samples[Math.Max(i - VerticalSpeedHalfWindow, 0)], to = samples[Math.Min(i + VerticalSpeedHalfWindow, samples.Count - 1)];
			double span = to.Seconds - from.Seconds;
			result.Add(span > 0 ? samples[i] with { VerticalSpeedMs = (to.HeightMeters - from.HeightMeters) / span } : samples[i]);
		}

		return result;
	}

	private static string? ReadName(ReadOnlySpan<byte> bytes)
	{
		int end = bytes.IndexOf((byte)0);
		string name = System.Text.Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]).Trim();
		return name.Length > 0 && name.All(c => !char.IsControl(c)) ? name : null;
	}

	/// <summary>One OSD record: radians and tenths of a unit, as the aircraft writes them (the battery is not in it, see FlightEventTracker.OnBattery).</summary>
	private static FlightSample ReadSample(byte[] c, double seconds, double homeAltitude)
	{
		double longitude = AngleMath.RadToDeg(BitConverter.ToDouble(c, 0));
		double latitude = AngleMath.RadToDeg(BitConverter.ToDouble(c, 8));
		double height = BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(16)) / 10.0;
		double speedX = BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(18)) / 10.0;
		double speedY = BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(20)) / 10.0;
		bool gpsValid = (c[OsdLayout.GpsFlags] & OsdLayout.GpsValidFlag) != 0;
		int satellites = c[OsdLayout.Satellites];
		return new FlightSample(seconds, latitude, longitude, height, height + homeAltitude, Math.Sqrt(speedX * speedX + speedY * speedY), satellites, gpsValid,
			GpsLevel: (c[OsdLayout.GpsLevel] >> 2) & 0x0F, FlightMode: FlightModes.Name(OsdLayout.FlightMode(c)), Attitude: ReadAttitude(c));
	}

	/// <summary>Pitch, roll and yaw: three i16 in tenths of a degree at 24, 26 and 28.</summary>
	private static DroneAttitude ReadAttitude(byte[] c)
	{
		return new DroneAttitude(BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(24)) / 10.0, BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(26)) / 10.0,
			BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(28)) / 10.0);
	}

	/// <summary>
	///     The records in file order, decoded: type and content. The first byte of a record's body is the xor's seed and is left
	///     out; the trailing byte is part of the content for the xor-only versions and removed for the AES ones. A damaged
	///     stretch is skipped to the next record end. `keyBlocksOnly` leaves out everything but the key blocks (and the markers between sessions).
	/// </summary>
	private IEnumerable<(byte Type, byte[] Content)> ReadRecords(IReadOnlyList<Keychain>? keychains, bool keyBlocksOnly = false)
	{
		int keychainIndex = 0;
		Keychain? keychain = keychains is { Count: > 0 } ? keychains[0] : null;
		var ivs = new Dictionary<int, byte[]>();
		bool wideLength = Version > 12;
		int header = wideLength ? 3 : 2;
		int at = _recordsOffset;

		while (at + header < _recordsEnd)
		{
			byte type = _file[at];
			if (type == EndByte && _file[at + 1] == 0xD8)
			{
				at = SkipJpeg(at);
				continue;
			}

			int length = wideLength ? BinaryPrimitives.ReadUInt16LittleEndian(_file.AsSpan(at + 1)) : _file[at + 1];
			int body = at + header;
			if (length < 3 && Version > 6 && type != KeyStorageRecoverRecord || body + length >= _recordsEnd || _file[body + length] != EndByte)
			{
				at = SkipDamaged(at);
				continue;
			}

			at = body + length + 1;
			if (type == KeyStorageRecoverRecord)
			{
				keychainIndex++;
				keychain = keychains is not null && keychainIndex < keychains.Count ? keychains[keychainIndex] : null;
				ivs.Clear();
				yield return (type, _file.AsSpan(body, length).ToArray());
				continue;
			}

			if (keyBlocksOnly && type != KeyStorageRecord) continue;

			if (Version <= 6)
			{
				yield return (type, _file.AsSpan(body, length).ToArray());
				continue;
			}

			byte seed = _file[body];
			int featurePoint = FlightLogFeaturePoints.FromRecordType(type, Version);
			if (Version > 12 && featurePoint != FlightLogFeaturePoints.Plaintext)
			{
				if (keychain?.Find(featurePoint) is not { } keys) continue;

				byte[] cipher = _file.AsSpan(body + 1, length - 2).ToArray();
				FlightLogCrypto.Xor(cipher, seed, type);
				byte[] iv = ivs.TryGetValue(featurePoint, out byte[]? next) ? next : keys.Iv;
				if (cipher.Length >= 16) ivs[featurePoint] = cipher.AsSpan(cipher.Length - 16).ToArray();
				yield return (type, FlightLogCrypto.AesDecrypt(cipher, keys.Key, iv));
				continue;
			}

			byte[] content = _file.AsSpan(body + 1, length - 1).ToArray();
			FlightLogCrypto.Xor(content, seed, type);
			yield return (type, content);
		}
	}

	private int SkipJpeg(int at)
	{
		for (int i = at + 2; i + 1 < _recordsEnd; i++)
		{
			if (_file[i] == 0xFF && _file[i + 1] == 0xD9) return i + 2;
		}

		return _recordsEnd;
	}

	private int SkipDamaged(int at)
	{
		for (int i = at + 1; i < _recordsEnd; i++)
		{
			if (_file[i] == EndByte) return i + 1 < _recordsEnd && _file[i + 1] == 0xD8 ? i : i + 1;
		}

		return _recordsEnd;
	}
}
