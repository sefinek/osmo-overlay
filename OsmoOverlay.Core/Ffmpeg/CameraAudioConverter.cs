using System.Diagnostics;

namespace OsmoOverlay.Core.Ffmpeg;

public enum CameraAudioFormat
{
	/// <summary>32-bit float PCM - exactly what the AAC decoder outputs, opens in any editor.</summary>
	Wav,

	/// <summary>The same AAC stream, bit for bit, in an MP4 audio container instead of bare ADTS.</summary>
	M4a
}

/// <summary>
///     Osmo Action writes the built-in microphones as a separate .AAC file next to the video when an
///     external mic (DJI Mic) is recording into the MP4's audio track. That file is a bare ADTS stream (no
///     container), which some editors refuse (Vegas Pro, Audacity without its ffmpeg plugin). Neither
///     output loses anything relative to it - verified on a real recording: the source, the WAV and the
///     M4A decode to bit-identical samples. Convert checks exactly that every time before keeping the file.
///     MP3 is deliberately not offered: it would be a second lossy encode.
/// </summary>
public static class CameraAudioConverter
{
	public static string OutputPathFor(string inputPath, CameraAudioFormat format)
	{
		return Path.ChangeExtension(inputPath, format == CameraAudioFormat.Wav ? ".wav" : ".m4a");
	}

	public static void Convert(string inputPath, string outputPath, CameraAudioFormat format)
	{
		string[] formatArgs = format switch
		{
			// rf64 only kicks in past WAV's 4 GB limit (~3.1 h of 48 kHz stereo float).
			CameraAudioFormat.Wav => ["-c:a", "pcm_f32le", "-rf64", "auto", "-f", "wav"],
			CameraAudioFormat.M4a => ["-c:a", "copy", "-movflags", "+faststart", "-f", "mp4"],
			_ => throw new ArgumentOutOfRangeException(nameof(format))
		};

		var partialPath = outputPath + ".partial";
		try
		{
			Run("ffmpeg", [
				"-hide_banner", "-loglevel", "error", "-y", "-i", inputPath, "-map", "0:a:0", "-fflags", "+bitexact",
				.. formatArgs, partialPath
			]);

			if (DecodedHash(inputPath) != DecodedHash(partialPath))
				throw new InvalidOperationException("Verification failed: the converted audio doesn't decode to the same samples as the source.");

			File.Move(partialPath, outputPath, true);
		}
		finally
		{
			try
			{
				File.Delete(partialPath);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best-effort: a leftover .partial file is harmless.
			}
		}
	}

	private static string DecodedHash(string path)
	{
		return Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-i", path, "-map", "0:a:0", "-c:a", "pcm_f32le", "-f", "md5", "-"]).Trim();
	}

	private static string Run(string command, string[] args)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden(command, args);
		var (exitCode, stdout, stderr) = ProcessHelper.RunCaptured(psi);
		if (exitCode != 0) throw new InvalidOperationException($"{command} exited with an error ({exitCode}): {stderr}");
		return stdout;
	}
}
