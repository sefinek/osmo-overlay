using System.Security.Cryptography;
using System.Text;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     AudioWaveform's peaks kept on disk next to FileSummaryCache's entries (&lt;hash&gt;.waveform, cleared with them), so a
///     recording opened again shows its waveform at once instead of decoding its whole audio track again (~15 s for 25 min).
///     Keyed by the files' paths; valid while every file's size and modification time still match. Peaks are stored as
///     16-bit fractions of full scale - a step of 1.5e-5, far below the -72 dBFS the timeline still draws.
/// </summary>
internal static class WaveformCache
{
	private const int FormatVersion = 1;
	private const uint Magic = 0x46574F4F; // "OOWF"
	private const string Extension = ".waveform";

	private static readonly string DefaultDirectory = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "cache");

	public static (float[][] Peaks, float Loudest)? TryLoad(IReadOnlyList<PlaybackSegment> segments, string? directory = null)
	{
		var path = PathFor(segments, directory);
		if (Stamps(segments) is not { } stamps || !File.Exists(path)) return null;

		try
		{
			using var reader = new BinaryReader(File.OpenRead(path));
			if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion) return null;
			if (reader.ReadInt32() != stamps.Count) return null;
			foreach (var (size, ticks) in stamps)
				if (reader.ReadInt64() != size || reader.ReadInt64() != ticks)
					return null;

			var channels = reader.ReadInt32();
			var buckets = reader.ReadInt32();
			var loudest = reader.ReadSingle();
			// Checked against what's left of the file before allocating - a damaged header mustn't ask for gigabytes.
			if (channels is < 1 or > 16 || buckets < 1 ||
			    reader.BaseStream.Length - reader.BaseStream.Position != (long)channels * buckets * sizeof(ushort))
				return null;

			var peaks = new float[channels][];
			for (var c = 0; c < channels; c++)
			{
				peaks[c] = new float[buckets];
				for (var i = 0; i < buckets; i++) peaks[c][i] = reader.ReadUInt16() / (float)ushort.MaxValue;
			}

			return (peaks, loudest);
		}
		catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
		{
			AppLogger.Warn(ex, $"Waveform cache entry {path} is unreadable, decoding the audio again");
			return null;
		}
	}

	/// <summary>Best-effort: a cache that can't be written only means the next open decodes again.</summary>
	public static void Save(IReadOnlyList<PlaybackSegment> segments, float[][] peaks, float loudest, string? directory = null)
	{
		if (Stamps(segments) is not { } stamps) return;

		var path = PathFor(segments, directory);
		var temp = path + ".tmp";
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			using (var writer = new BinaryWriter(File.Create(temp)))
			{
				writer.Write(Magic);
				writer.Write(FormatVersion);
				writer.Write(stamps.Count);
				foreach (var (size, ticks) in stamps)
				{
					writer.Write(size);
					writer.Write(ticks);
				}

				writer.Write(peaks.Length);
				writer.Write(peaks[0].Length);
				writer.Write(loudest);
				foreach (var channel in peaks)
				foreach (var peak in channel)
					writer.Write((ushort)Math.Round(Math.Clamp(peak, 0f, 1f) * ushort.MaxValue));
			}

			File.Move(temp, path, true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLogger.Warn(ex, $"Couldn't write the waveform cache {path}");
			try
			{
				File.Delete(temp);
			}
			catch
			{
				// Left for the next ClearAll.
			}
		}
	}

	public static IEnumerable<string> Files(string directory)
	{
		return Directory.Exists(directory) ? Directory.GetFiles(directory, "*" + Extension) : [];
	}

	private static string PathFor(IReadOnlyList<PlaybackSegment> segments, string? directory)
	{
		var joined = string.Join("|", segments.Select(s => Path.GetFullPath(s.Path).ToLowerInvariant()));
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("waveform|" + joined)));
		return Path.Combine(directory ?? DefaultDirectory, hash + Extension);
	}

	/// <summary>Each file's size and modification time - null when one can't be read (moved, deleted).</summary>
	private static List<(long Size, long Ticks)>? Stamps(IReadOnlyList<PlaybackSegment> segments)
	{
		List<(long, long)> stamps = [];
		foreach (PlaybackSegment segment in segments)
		{
			var info = new FileInfo(segment.Path);
			if (!info.Exists) return null;
			stamps.Add((info.Length, info.LastWriteTimeUtc.Ticks));
		}

		return stamps;
	}
}
