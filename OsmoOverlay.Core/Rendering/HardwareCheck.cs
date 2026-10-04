using System.Diagnostics;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Core;

public enum CheckStatus
{
	Ok,
	Warning,
	Problem,
	Unknown
}

/// <summary>
///     What the first-run window checks about this computer for 4K renders from an Osmo Action. Encoder: what a render of a
///     4K 10-bit HEVC recording would pick (FfmpegPipeline.SelectVideoEncoder) - null without FFmpeg; EncoderCard: the graphics
///     card it runs on (GpuAdapters.Pick, Windows only) - null when not known. HardwareDecode: whether
///     the GPU decoded a 10-bit HEVC clip through the platform's own method (HardwareDecodeMethod) - null without FFmpeg.
///     FreeBytes: free space where renders go - null when unreadable.
/// </summary>
public sealed record HardwareReport(
	BenchmarkSystem System,
	bool Is64Bit,
	string? Encoder,
	string? EncoderCard,
	bool? HardwareDecode,
	string HardwareDecodeMethod,
	string OutputFolder,
	long? FreeBytes)
{
	/// <summary>An hour of 4K at the Osmo Action's ~73 Mbps.</summary>
	public const long HourOf4KBytes = 73_000_000L * 3600 / 8;

	// 8 and 16 GB with room to spare: outside Windows the memory read is what the system can use, a few percent below what's
	// installed (BenchmarkSystemInfo), and a 16 GB computer mustn't come out as less.
	internal const long MinMemoryBytes = 15L << 29;
	internal const long RecommendedMemoryBytes = 15L << 30;

	public CheckStatus MemoryStatus => System.MemoryBytes switch
	{
		< MinMemoryBytes => CheckStatus.Problem,
		< RecommendedMemoryBytes => CheckStatus.Warning,
		_ => CheckStatus.Ok
	};

	public CheckStatus ProcessorStatus => System.Threads switch
	{
		< 4 => CheckStatus.Problem,
		< 8 => CheckStatus.Warning,
		_ => CheckStatus.Ok
	};

	public CheckStatus SystemStatus => Is64Bit ? CheckStatus.Ok : CheckStatus.Problem;

	public CheckStatus EncoderStatus => Encoder is null ? CheckStatus.Unknown : FfmpegPipeline.IsGpuEncoder(Encoder) ? CheckStatus.Ok : CheckStatus.Warning;

	public CheckStatus DecoderStatus => HardwareDecode switch
	{
		null => CheckStatus.Unknown,
		false => CheckStatus.Warning,
		true => CheckStatus.Ok
	};

	/// <summary>A render needs about twice its size (RenderDiskSpace): an hour of 4K takes ~66 GB while it's made.</summary>
	public CheckStatus DiskStatus => FreeBytes switch
	{
		null => CheckStatus.Unknown,
		< 20L << 30 => CheckStatus.Problem,
		< 2 * HourOf4KBytes => CheckStatus.Warning,
		_ => CheckStatus.Ok
	};

	/// <summary>The worst of every check that could be made.</summary>
	public CheckStatus Overall =>
		new[] { SystemStatus, MemoryStatus, ProcessorStatus, EncoderStatus, DecoderStatus, DiskStatus }
			.Where(s => s != CheckStatus.Unknown).DefaultIfEmpty(CheckStatus.Unknown).Max();
}

public static class HardwareCheck
{
	// What a render of the Osmo Action's own recordings asks the encoder for.
	private static readonly VideoInfo Osmo4K = new("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", "bt709", "bt709", "bt709", "tv", 73_000_000);

	/// <param name="outputFolder">Where renders go (Settings' default folder) - the user's Videos folder when null.</param>
	public static async Task<HardwareReport> RunAsync(string? outputFolder, CancellationToken ct)
	{
		BenchmarkSystem system = await BenchmarkSystemInfo.CollectAsync(ct);
		bool ffmpeg = DependencyChecker.IsAvailable(RequiredTools.Ffmpeg);
		string? encoder = ffmpeg ? await Task.Run(() => FfmpegPipeline.SelectVideoEncoder(Osmo4K), ct) : null;
		(string method, string frames) = HardwareDecodeMethod();
		bool? decode = ffmpeg ? await DecodesOnGpuAsync(method, frames, encoder, ct) : null;

		string folder = !string.IsNullOrWhiteSpace(outputFolder) && Directory.Exists(outputFolder)
			? outputFolder
			: Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is { Length: > 0 } videos
				? videos
				: Environment.CurrentDirectory;
		long? free = RenderDiskSpace.AvailableBytes(Path.Combine(folder, "render.mp4"));

		string? card = encoder is not null && FfmpegPipeline.GpuVendor(encoder) is { } vendor ? GpuAdapters.Pick(GpuAdapters.All, vendor)?.Name : null;
		return new HardwareReport(system, Environment.Is64BitOperatingSystem, encoder, string.IsNullOrEmpty(card) ? null : card, decode, method, folder, free);
	}

	/// <summary>The platform's own hardware decoding, as `-hwaccel` and the frames it hands over in GPU memory.</summary>
	private static (string Method, string Frames) HardwareDecodeMethod()
	{
		if (OperatingSystem.IsWindows()) return ("d3d11va", "d3d11");
		if (OperatingSystem.IsMacOS()) return ("videotoolbox", "videotoolbox_vld");
		return ("vaapi", "vaapi");
	}

	/// <summary>
	///     Whether the GPU decodes 10-bit HEVC (what the Osmo Action records): a tiny clip made with x265, decoded through
	///     `method` with its frames kept in GPU memory - `-hwaccel` alone falls back to the CPU silently, this way it fails. On the
	///     card a render with `encoder` decodes on (FfmpegPipeline.HwDecodeArgs: AMF's and Quick Sync's own). Null when the clip
	///     can't be made.
	/// </summary>
	private static async Task<bool?> DecodesOnGpuAsync(string method, string frames, string? encoder, CancellationToken ct)
	{
		string clip = Path.Combine(Path.GetTempPath(), $"osmooverlay-hwcheck-{Guid.NewGuid():N}.mp4");
		try
		{
			ProcessStartInfo make = ProcessHelper.CreateHiddenQuiet("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
				"testsrc2=s=320x240:r=30:d=0.5,format=yuv420p10le", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=none", clip);
			// Without x265 in this FFmpeg there's no clip to try - unknown, not "can't".
			if ((await ProcessHelper.TryRunCapturedAsync(make, ct)).ExitCode != 0) return null;

			string[] device = encoder is null ? [] : FfmpegPipeline.EncoderDeviceArgs(encoder);
			string[] hwaccel = device.Length > 0 ? [.. device, .. FfmpegPipeline.HwDecodeArgs(encoder!)] : ["-hwaccel", method];
			ProcessStartInfo decode = ProcessHelper.CreateHiddenQuiet("ffmpeg", [
				"-hide_banner", "-loglevel", "error", .. hwaccel,
				"-hwaccel_output_format", frames, "-i", clip, "-f", "null", "-"
			]);
			return (await ProcessHelper.TryRunCapturedAsync(decode, ct)).ExitCode == 0;
		}
		finally
		{
			try
			{
				File.Delete(clip);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// A tiny temporary file - the system cleans it up.
			}
		}
	}
}
