using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Cameras.Dji;

/// <summary>
///     Decodes the protobuf-encoded "DJI meta" stream (codec_tag djmd) embedded by DJI Osmo Action
///     cameras directly, as the primary telemetry source - GPS position/velocity, accelerometer, ISO,
///     shutter speed, color temperature and the device name all live in the same stream. exiftool does
///     not expose most of these, and reading two tools per file is slower than decoding these bytes
///     ourselves. Field mapping below was reverse-engineered and cross-checked against exiftool's own
///     per-frame output across an entire real recording (33654/33654 samples, zero mismatches) for a
///     DJI Osmo Action 6. exiftool remains the fallback (see TelemetryExtraction.Extract) in case a
///     different camera/firmware lays the stream out differently.
///     Wire format, one top-level field-3 message per sample:
///     f3
///     ├── f1 (message)              - sample header (not used: see note on SampleTimeSeconds below)
///     ├── f2 (message)              - camera settings
///     │   ├── f3 (message) → f1 (float)   - ISO
///     │   ├── f4 (message) → f1 (bytes: two raw varints [numerator, denominator]) - shutter speed
///     │   ├── f6 (message) → f1 (varint)  - color temperature, Kelvin
///     │   └── f10 (message)               - accelerometer: f2/f3/f4 (float) = X/Y/Z
///     └── f4 (message)              - GPS data
///     ├── f1 (message) → f4 (string)  - device name, DJI's internal model code, e.g. "DJI AC006" (see DjiModels)
///     ├── f2 (message)              - GPS fix
///     │   ├── f1 (message)
///     │   │   ├── f1 (varint)  - GPS fix type (0 = no fix)
///     │   │   ├── f2 (double)  - latitude
///     │   │   └── f3 (double)  - longitude
///     │   ├── f2 (varint)  - altitude in mm
///     │   └── f6 (message) → f1 (string) - timestamp "yyyy-MM-dd HH:mm:ss"
///     └── f3 (message) - 2D velocity: f1/f2 (float) = vx/vy (m/s)
///     SampleTimeSeconds is deliberately NOT derived from any raw per-sample clock field: exiftool's
///     own "SampleTime" was verified (against this file, at multiple indices) to be exactly
///     index / fps to floating-point rounding, not a hardware timestamp - so we compute it the same
///     way, using the video's real frame rate from ffprobe.
/// </summary>
internal static class DjiMetaTelemetryParser
{
	private const int ParallelReads = 8;

	/// <summary>
	///     The djmd track's samples in order, read straight from the file's sample table - a few MB of a recording of tens of
	///     GB, which ffmpeg demuxes whole to hand them over (~15 s for a 17 GB file). The same bytes: ffmpeg's data muxer
	///     writes the packets back to back. ffmpeg stays the fallback for a file whose boxes this can't read.
	/// </summary>
	public static ReadOnlyMemory<byte> ReadRawStream(string inputPath, int streamIndex)
	{
		try
		{
			if (ReadTrackSamples(inputPath) is { } samples) return samples;
			AppLogger.Info($"{Path.GetFileName(inputPath)}: no djmd track in the sample table - extracting it with ffmpeg");
		}
		catch (Exception ex)
		{
			AppLogger.Info($"{Path.GetFileName(inputPath)}: djmd samples not read from the file ({ex.GetType().Name}: {ex.Message}) - extracting them with ffmpeg");
		}

		return ExtractRawStream(inputPath, streamIndex);
	}

	internal static byte[]? ReadTrackSamples(string inputPath)
	{
		if (Mp4CameraMetadata.ReadSourceTracks(inputPath).FirstOrDefault(t => t.Format == "djmd") is not { } track) return null;

		long total = track.Sizes.Sum(size => (long)size);
		if (total > Array.MaxLength) throw new InvalidDataException($"djmd track of {total} bytes");

		int[] at = new int[track.Sizes.Length];
		for (int i = 1; i < at.Length; i++) at[i] = at[i - 1] + (int)track.Sizes[i - 1];

		byte[] data = new byte[total];
		using SafeFileHandle file = File.OpenHandle(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
		// The samples sit one per frame across the whole file, ~190 KB apart: tens of thousands of small reads, which an SSD
		// serves several at once - one after another they took ~14 s for a 17 GB file.
		Parallel.For(0, at.Length, new ParallelOptions { MaxDegreeOfParallelism = ParallelReads }, i =>
		{
			Span<byte> sample = data.AsSpan(at[i], (int)track.Sizes[i]);
			if (RandomAccess.Read(file, sample, (long)track.Offsets[i]) != sample.Length)
				throw new EndOfStreamException($"djmd sample {i} runs past the end of the file");
		});

		return data;
	}

	internal static ReadOnlyMemory<byte> ExtractRawStream(string inputPath, int streamIndex)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg",
			"-v", "error", "-i", inputPath, "-map", $"0:{streamIndex}", "-c", "copy", "-f", "data", "pipe:1");

		using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");

		using var stdout = new MemoryStream();
		Task stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout);
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		Task.WaitAll(stdoutTask, stderrTask);
		process.WaitForExit();

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"ffmpeg exited with an error ({process.ExitCode}) extracting the djmd stream: {stderrTask.Result}");
		}

		// The underlying buffer instead of ToArray() - the djmd stream of a long recording runs to tens of
		// MB, no point copying all of it once more just to parse it.
		return stdout.GetBuffer().AsMemory(0, (int)stdout.Length);
	}

	public static TelemetryExtractionResult Parse(ReadOnlyMemory<byte> rawData, double fps)
	{
		List<RawRecord> records = ParseRawRecords(rawData);
		if (records.Count == 0)
			throw new InvalidOperationException("No djmd telemetry samples decoded from the raw stream.");

		var frames = new List<TelemetryFrame>(records.Count);
		var gpsFill = new GpsForwardFill();

		for (int i = 0; i < records.Count; i++)
		{
			RawRecord r = records[i];
			(double lat, double lon, double altitudeMeters, bool hasFix) = gpsFill.Apply(r.Lat, r.Lon, r.AltitudeMeters);

			frames.Add(new TelemetryFrame(
				i,
				i / fps,
				lat,
				lon,
				altitudeMeters,
				r.GpsTimestamp,
				r.AccelX,
				r.AccelY,
				r.AccelZ,
				r.SpeedMs,
				r.Iso,
				r.ShutterSeconds,
				r.ColorTemperatureKelvin,
				hasFix));
		}

		string? cameraModel = records.Select(r => r.DeviceName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
		return new TelemetryExtractionResult(frames, cameraModel);
	}

	private static List<RawRecord> ParseRawRecords(ReadOnlyMemory<byte> rawData)
	{
		var records = new List<RawRecord>();
		foreach ((int fieldNumber, int wireType, ReadOnlyMemory<byte> bytes, _) in IterFields(rawData))
		{
			if (fieldNumber == 3 && wireType == 2)
				records.Add(ParseSample(bytes));
		}

		return records;
	}

	private static RawRecord ParseSample(ReadOnlyMemory<byte> sample)
	{
		double accelX = 0, accelY = 0, accelZ = 0;
		float? iso = null;
		double? shutterSeconds = null;
		int? colorTemperatureKelvin = null;

		// Single pass over f2's/gpsMsg's own fields instead of one GetSubmessage rescan per field -
		// this runs once per telemetry sample (tens of thousands per recording).
		ReadOnlyMemory<byte>? f2 = GetSubmessage(sample, 2);
		if (f2 is not null)
		{
			foreach ((int fieldNumber, int wireType, ReadOnlyMemory<byte> bytes, _) in IterFields(f2.Value))
			{
				if (wireType != 2) continue;

				switch (fieldNumber)
				{
					case 3:
						iso = GetFloatField(bytes, 1);
						break;
					case 4:
						ReadOnlyMemory<byte>? shutterInner = GetSubmessage(bytes, 1);
						if (shutterInner is { Length: > 0 } inner)
						{
							int sp = 0;
							ulong numerator = ReadVarint(inner.Span, ref sp);
							if (sp < inner.Length)
							{
								ulong denominator = ReadVarint(inner.Span, ref sp);
								if (denominator > 0) shutterSeconds = (double)numerator / denominator;
							}
						}

						break;
					case 6:
						ulong? ct = GetVarintField(bytes, 1);
						if (ct is not null) colorTemperatureKelvin = (int)ct.Value;
						break;
					case 10:
						accelX = GetFloatField(bytes, 2) ?? 0;
						accelY = GetFloatField(bytes, 3) ?? 0;
						accelZ = GetFloatField(bytes, 4) ?? 0;
						break;
				}
			}
		}

		double? lat = null, lon = null, altitudeMeters = null, speedMs = null;
		DateTime? gpsTimestamp = null;
		string? deviceName = null;

		ReadOnlyMemory<byte>? gpsMsg = GetSubmessage(sample, 4);
		if (gpsMsg is not null)
		{
			foreach ((int fieldNumber, int wireType, ReadOnlyMemory<byte> bytes, _) in IterFields(gpsMsg.Value))
			{
				if (wireType != 2) continue;

				switch (fieldNumber)
				{
					case 1:
						deviceName = GetStringField(bytes, 4);
						break;
					case 2:
						ReadOnlyMemory<byte>? coordsMsg = GetSubmessage(bytes, 1);
						ulong? fixType = coordsMsg is not null ? GetVarintField(coordsMsg.Value, 1) : null;
						if (coordsMsg is not null && fixType is not (null or 0))
						{
							double? latVal = GetDoubleField(coordsMsg.Value, 2);
							double? lonVal = GetDoubleField(coordsMsg.Value, 3);
							if (latVal is not null && lonVal is not null && !(latVal == 0.0 && lonVal == 0.0))
							{
								lat = latVal;
								lon = lonVal;
							}
						}

						ulong? altMm = GetVarintField(bytes, 2);
						if (altMm is not null) altitudeMeters = altMm.Value / 1000.0;

						ReadOnlyMemory<byte>? timestampMsg = GetSubmessage(bytes, 6);
						if (timestampMsg is not null)
						{
							string? tsStr = GetStringField(timestampMsg.Value, 1);
							if (tsStr is not null && DateTime.TryParseExact(tsStr, "yyyy-MM-dd HH:mm:ss",
								    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
								gpsTimestamp = dt;
						}

						break;
					case 3:
						float vx = GetFloatField(bytes, 1) ?? 0;
						float vy = GetFloatField(bytes, 2) ?? 0;
						speedMs = Math.Sqrt(vx * vx + vy * vy);
						break;
				}
			}
		}

		return new RawRecord(lat, lon, altitudeMeters, gpsTimestamp, accelX, accelY, accelZ, speedMs, deviceName,
			iso, shutterSeconds, colorTemperatureKelvin);
	}

	private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
	{
		ulong result = 0;
		int shift = 0;
		while (pos < data.Length && shift < 70)
		{
			byte b = data[pos++];
			result |= (ulong)(b & 0x7F) << shift;
			shift += 7;
			if ((b & 0x80) == 0) break;
		}

		return result;
	}

	private static IEnumerable<(int FieldNumber, int WireType, ReadOnlyMemory<byte> Bytes, ulong Varint)> IterFields(
		ReadOnlyMemory<byte> data)
	{
		int pos = 0;
		while (pos < data.Length)
		{
			ulong tag = ReadVarint(data.Span, ref pos);
			int fieldNumber = (int)(tag >> 3);
			int wireType = (int)(tag & 0x07);

			switch (wireType)
			{
				case 0:
					yield return (fieldNumber, wireType, default, ReadVarint(data.Span, ref pos));
					break;
				case 1:
					if (pos + 8 > data.Length) yield break;
					yield return (fieldNumber, wireType, data.Slice(pos, 8), 0);
					pos += 8;
					break;
				case 2:
					ulong length = ReadVarint(data.Span, ref pos);
					if (length > (ulong)(data.Length - pos)) yield break;
					yield return (fieldNumber, wireType, data.Slice(pos, (int)length), 0);
					pos += (int)length;
					break;
				case 5:
					if (pos + 4 > data.Length) yield break;
					yield return (fieldNumber, wireType, data.Slice(pos, 4), 0);
					pos += 4;
					break;
				default:
					yield break;
			}
		}
	}

	private static ReadOnlyMemory<byte>? GetSubmessage(ReadOnlyMemory<byte> data, int targetField)
	{
		foreach ((int fn, int wt, ReadOnlyMemory<byte> bytes, _) in IterFields(data))
		{
			if (fn == targetField && wt == 2)
				return bytes;
		}

		return null;
	}

	private static ulong? GetVarintField(ReadOnlyMemory<byte> data, int targetField)
	{
		foreach ((int fn, int wt, ReadOnlyMemory<byte> _, ulong varint) in IterFields(data))
		{
			if (fn == targetField && wt == 0)
				return varint;
		}

		return null;
	}

	private static double? GetDoubleField(ReadOnlyMemory<byte> data, int targetField)
	{
		foreach ((int fn, int wt, ReadOnlyMemory<byte> bytes, _) in IterFields(data))
		{
			if (fn == targetField && wt == 1)
				return BitConverter.ToDouble(bytes.Span);
		}

		return null;
	}

	private static float? GetFloatField(ReadOnlyMemory<byte> data, int targetField)
	{
		foreach ((int fn, int wt, ReadOnlyMemory<byte> bytes, _) in IterFields(data))
		{
			if (fn == targetField && wt == 5)
				return BitConverter.ToSingle(bytes.Span);
		}

		return null;
	}

	private static string? GetStringField(ReadOnlyMemory<byte> data, int targetField)
	{
		ReadOnlyMemory<byte>? sub = GetSubmessage(data, targetField);
		if (sub is null) return null;

		try
		{
			return Encoding.UTF8.GetString(sub.Value.Span);
		}
		catch (DecoderFallbackException)
		{
			return null;
		}
	}

	private readonly record struct RawRecord(
		double? Lat,
		double? Lon,
		double? AltitudeMeters,
		DateTime? GpsTimestamp,
		double AccelX,
		double AccelY,
		double AccelZ,
		double? SpeedMs,
		string? DeviceName,
		float? Iso,
		double? ShutterSeconds,
		int? ColorTemperatureKelvin);
}
