using Microsoft.Win32.SafeHandles;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Cameras.GoPro;

/// <summary>One sample of the gpmd track: what the camera's devices reported for the second (or so) from `StartSeconds` on.</summary>
internal sealed record GpmfPacket(double StartSeconds, double DurationSeconds, IReadOnlyList<GpmfDevice> Devices);

/// <summary>
///     The MP4's gpmd track read straight from its sample table: a few packets a second of a recording of many GB, which
///     ffmpeg would demux whole to hand over. The packets' own times (stts) are the file's - the sensors' own clock is
///     in each stream's STMP (GoProTelemetryParser).
/// </summary>
internal static class GoProTrack
{
	public const string SampleFormat = "gpmd";

	public static List<GpmfPacket> Read(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		Mp4TopLevelBox moovBox = Mp4File.ReadTopLevel(stream).FirstOrDefault(b => b.Type == "moov");
		if (moovBox.Type is null) throw new InvalidDataException($"{Path.GetFileName(path)} has no moov box.");

		Mp4Box moov = Mp4File.ReadMoov(stream, moovBox);
		foreach (Mp4Box trak in moov.Children!.Where(c => c.Type == "trak"))
		{
			Mp4Box? stbl = trak.Find("mdia", "minf", "stbl");
			if (stbl?.Child("stsd") is not { } stsd || Mp4Fields.SampleEntryFormat(stsd) != SampleFormat) continue;
			if (trak.Find("mdia", "mdhd") is not { } mdhd || stbl.Child("stts") is not { } stts || stbl.Child("stsz") is not { } stsz ||
			    stbl.Child("stsc") is not { } stsc || (stbl.Child("stco") ?? stbl.Child("co64")) is not { } chunkOffsets)
				throw new InvalidDataException($"{Path.GetFileName(path)}'s gpmd track has an incomplete sample table.");

			uint timescale = Mp4Fields.MediaTiming(mdhd).Timescale;
			if (timescale == 0) throw new InvalidDataException($"{Path.GetFileName(path)}'s gpmd track has no time scale.");

			uint[] sizes = Mp4Fields.ReadSampleSizes(stsz);
			ulong[] offsets = Mp4Fields.SampleOffsets(Mp4Fields.ReadStsc(stsc), Mp4Fields.ReadChunkOffsets(chunkOffsets), sizes);
			return ReadPackets(stream.SafeFileHandle, sizes, offsets, Durations(Mp4Fields.ReadStts(stts), sizes.Length, timescale));
		}

		throw new InvalidDataException($"{Path.GetFileName(path)} has no gpmd track.");
	}

	private static List<GpmfPacket> ReadPackets(SafeFileHandle file, uint[] sizes, ulong[] offsets, double[] durations)
	{
		List<GpmfPacket> packets = new(sizes.Length);
		double start = 0;
		for (int i = 0; i < sizes.Length; i++)
		{
			byte[] data = new byte[sizes[i]];
			if (RandomAccess.Read(file, data, (long)offsets[i]) != data.Length)
				throw new EndOfStreamException($"gpmd sample {i} runs past the end of the file");

			packets.Add(new GpmfPacket(start, durations[i], GpmfReader.Read(data)));
			start += durations[i];
		}

		return packets;
	}

	/// <summary>Each sample's length in seconds from the time-to-sample table.</summary>
	internal static double[] Durations(List<(uint Count, uint Delta)> stts, int samples, uint timescale)
	{
		double[] durations = new double[samples];
		int at = 0;
		foreach ((uint count, uint delta) in stts)
		{
			for (uint i = 0; i < count && at < samples; i++) durations[at++] = delta / (double)timescale;
		}

		return durations;
	}
}
