using System.Globalization;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core;

/// <summary>
///     The performance test's own recording, made once by ffmpeg and kept (Folder, rebuilt when Version changes): three 4K
///     59.94 10-bit HEVC files the way a camera splits one, each with SyntheticTelemetry beside it (SyntheticCamera reads it,
///     so the app takes them like any camera's - summary, preview, render). The picture is made to be hard: film grain over
///     everything, a moving zone plate (fine detail the encoder can't predict), a fractal zoom, a fast pan, cellular automata,
///     scene cuts and black/white flashes; the stream too: B-frames, a long GOP with keyframes at odd moments, a variable
///     bitrate swinging with the picture, a frozen stretch, a dropped frame (a gap in the timestamps); and the joins as a
///     camera leaves them - the sound running on 26 ms and 7.5 ms past the picture, the third file after a stop. Still,
///     a camera's own recording decodes harder on the CPU (RenderBenchmark.Advise compares decoding only on that).
/// </summary>
public static class SyntheticRecording
{
	// Bumped whenever what's generated changes, the picture or SyntheticTelemetry - the old files are deleted and made again
	// under new paths, so no summary cached from the old telemetry is read for them.
	private const int Version = 5;
	private const string FrameRate = "60000/1001";
	internal const double Fps = 60000 / 1001.0;
	private const string DoneMarker = "done";

	private static readonly FilePlan[] Files =
	[
		new(600, 0.026, "0,0.4,3.1,3.2,7.9",
		[
			new("testsrc2=s=3840x2160", 2.5),
			new("color=c=white:s=3840x2160", 0.1),
			new("life=s=960x540:mold=10:ratio=0.12:death_color=#1a1a2a:life_color=#e8c070:seed=7", 2.4, Blocky: true),
			new("gradients=s=1920x1080:speed=0.08:n=7:seed=3", 2.0),
			new("color=c=black:s=3840x2160", 0.05),
			new("smptehdbars=s=3840x2160", 1.95),
			new("mandelbrot=s=960x540:end_scale=0.01:start_scale=1.5:end_pts=60", 1.0)
		], FreezeFrames: (420, 470)),
		new(600, 0.0075, "0,4.95,5.0,9.2",
		[
			new("zoneplate=s=1920x1080:kt2=4:kx2=24:ky2=20:kxt=12", 2.5),
			new("mandelbrot=s=960x540:end_scale=0.002:start_scale=2:end_pts=100", 2.5),
			new("testsrc2=s=1920x1080:r=1", 2.0, Pan: true),
			new("cellauto=s=960x540:rule=110:random_fill_ratio=0.5:scroll=1:seed=5", 2.0, Blocky: true),
			new("testsrc2=s=3840x2160", 1.0)
		], DroppedFrame: 300),
		new(600, 0, "0,1.0,1.1,1.2",
		[
			new("color=c=black:s=3840x2160", 0.2),
			new("testsrc2=s=3840x2160", 2.8),
			new("zoneplate=s=1920x1080:kt2=6:kx2=30:ky2=30:kxt=20", 1.8),
			new("color=c=white:s=3840x2160", 0.08),
			new("gradients=s=1920x1080:speed=0.12:n=5:seed=11", 3.0),
			new("life=s=960x540:mold=6:ratio=0.2:death_color=#10202a:life_color=#70d0f0:seed=13", 2.12, Blocky: true)
		])
	];

	public static string Folder { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "benchmark");

	private static string VersionFolder => Path.Combine(Folder, $"v{Version}");

	internal static IReadOnlyList<FilePlan> Plans => Files;

	private static List<string> Paths()
	{
		return [.. Files.Select((_, i) => Path.Combine(VersionFolder, $"TestRecording_{i + 1:00}.mp4"))];
	}

	public static double FileSeconds(int file)
	{
		return Files[file].Frames / Fps;
	}

	/// <summary>The files when they're all made (with their telemetry) - null while they aren't.</summary>
	public static IReadOnlyList<string>? Existing()
	{
		List<string> paths = Paths();
		return File.Exists(Path.Combine(VersionFolder, DoneMarker)) && paths.All(p => File.Exists(p) && File.Exists(SyntheticTelemetry.SidecarPath(p)))
			? paths
			: null;
	}

	/// <summary>The files, made first when they aren't there yet (progress 0..1). Every older version's are deleted.</summary>
	public static async Task<IReadOnlyList<string>> EnsureAsync(Action<double>? progress, CancellationToken ct)
	{
		if (Existing() is { } existing) return existing;

		string folder = VersionFolder;
		List<string> paths = Paths();

		DeleteOtherVersions();
		if (Directory.Exists(folder)) Directory.Delete(folder, true);
		Directory.CreateDirectory(folder);

		int totalFrames = Files.Sum(f => f.Frames);
		int framesBefore = 0;
		string? encoder = null;
		for (int i = 0; i < Files.Length; i++)
		{
			int done = framesBefore;
			encoder = await GenerateFileAsync(Files[i], i, paths[i], encoder, frame => progress?.Invoke(Math.Min(1, (done + frame) / (double)totalFrames)), ct);
			SyntheticTelemetry.Write(paths[i], SyntheticTelemetry.ForFile(i, Files[i].Frames, Fps));
			framesBefore += Files[i].Frames;
		}

		await File.WriteAllTextAsync(Path.Combine(folder, DoneMarker), encoder, ct);
		AppLogger.Info($"Made the performance test's recording with {encoder} in {folder}");
		return paths;
	}

	/// <summary>Encoded on the graphics card when it can (seconds), else with x265 - once, so its minutes on a CPU are paid only the first time. The encoder that worked is tried first for the next file.</summary>
	private static async Task<string> GenerateFileAsync(FilePlan plan, int index, string path, string? encoder, Action<long> progress,
		CancellationToken ct)
	{
		string[] candidates = encoder is null ? ["hevc_nvenc", "libx265"] : [encoder];
		foreach (string candidate in candidates)
		{
			(bool ok, _) = await RenderBenchmark.RunFfmpegAsync(Arguments(plan, index, path, candidate), ct, (frame, _) => progress(frame));
			if (ok) return candidate;
		}

		throw new InvalidOperationException("ffmpeg couldn't make the performance test's recording.");
	}

	internal static List<string> Arguments(FilePlan plan, int index, string path, string encoder)
	{
		List<string> args = [];
		foreach (Scene scene in plan.Scenes)
			args.AddRange(["-f", "lavfi", "-i", scene.Pan ? $"{scene.Source}:d=1" : $"{scene.Source}:r={FrameRate},trim=duration={Invariant(scene.Seconds)}"]);

		double seconds = plan.Frames / Fps;
		args.AddRange(["-f", "lavfi", "-i", Sound(index, seconds + plan.SoundOverrunSeconds)]);

		const string normalize = "fps=" + FrameRate + ",format=yuv420p10le,setsar=1";
		List<string> chains = [];
		for (int i = 0; i < plan.Scenes.Count; i++)
		{
			Scene scene = plan.Scenes[i];
			string chain = scene.Pan
				? $"scale=7680:2160,loop=loop=-1:size=1,fps={FrameRate},trim=duration={Invariant(scene.Seconds)},crop=3840:2160:x='mod(t*2400\\,3840)':y=0"
				: $"scale=3840:2160:flags={(scene.Blocky ? "neighbor" : "bicubic")}";
			chains.Add($"[{i}:v]{chain},{normalize}[s{i}]");
		}

		string joined = string.Concat(Enumerable.Range(0, plan.Scenes.Count).Select(i => $"[s{i}]"));
		string tail = $"concat=n={plan.Scenes.Count}:v=1:a=0,trim=end_frame={plan.Frames},noise=alls=20:allf=t";
		if (plan.FreezeFrames is { } freeze)
			tail += $",split[f1][f2];[f1][f2]freezeframes=first={freeze.First}:last={freeze.Last}:replace={freeze.First - 1}";
		if (plan.DroppedFrame is { } dropped)
			tail += $",select='not(eq(n\\,{dropped}))'";
		chains.Add($"{joined}{tail}[v]");

		args.AddRange(["-filter_complex", string.Join(';', chains), "-map", "[v]", "-map", $"{plan.Scenes.Count}:a"]);
		if (plan.DroppedFrame is not null) args.AddRange(["-fps_mode", "passthrough"]);
		args.AddRange(encoder == "hevc_nvenc"
			? ["-c:v", "hevc_nvenc", "-preset", "p4", "-profile:v", "main10", "-rc", "vbr", "-b:v", "90M", "-maxrate", "150M", "-bufsize", "180M",
				"-bf", "3", "-g", "600", "-forced-idr", "1"]
			: ["-c:v", "libx265", "-preset", "ultrafast", "-profile:v", "main10", "-b:v", "90M", "-maxrate", "150M", "-bufsize", "180M",
				"-x265-params", "keyint=600:bframes=3:log-level=error"]);
		args.AddRange(
		[
			"-force_key_frames", plan.Keyframes, "-tag:v", "hvc1",
			"-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv",
			"-c:a", "aac", "-b:a", "192k",
			"-metadata", $"creation_time={SyntheticTelemetry.CreationTimeUtc(index):yyyy-MM-ddTHH:mm:ss.000000Z}",
			"-metadata", "comment=OsmoOverlay performance test recording",
			path
		]);
		return args;
	}

	/// <summary>Two sweeping tones with a little noise - something to hear, and audio for the preview's clock to follow.</summary>
	private static string Sound(int index, double seconds)
	{
		int low = 180 + index * 70, high = 260 + index * 90;
		return $"aevalsrc=0.18*sin(2*PI*({low}+260*t)*t)+0.04*(random(0)-0.5)|0.18*sin(2*PI*({high}+140*t)*t)+0.04*(random(1)-0.5)" +
		       $":s=48000:d={Invariant(seconds)}";
	}

	private static void DeleteOtherVersions()
	{
		if (!Directory.Exists(Folder)) return;

		foreach (string directory in Directory.EnumerateDirectories(Folder, "v*"))
		{
			if (string.Equals(directory, VersionFolder, StringComparison.OrdinalIgnoreCase)) continue;
			try
			{
				Directory.Delete(directory, true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				AppLogger.Warn(ex, $"Couldn't delete an older performance test recording in {directory}");
			}
		}
	}

	private static string Invariant(double value)
	{
		return value.ToString("0.######", CultureInfo.InvariantCulture);
	}

	/// <summary>Pan: a still twice the frame's width, panned across at 2400 px a second.</summary>
	internal sealed record Scene(string Source, double Seconds, bool Blocky = false, bool Pan = false);

	/// <summary>
	///     One file: its frames (before a dropped one), how far its sound runs past the picture, the keyframes forced on top of
	///     a 600-frame GOP, its scenes in order, a stretch shown frozen and a frame left out of the timestamps.
	/// </summary>
	internal sealed record FilePlan(
		int Frames,
		double SoundOverrunSeconds,
		string Keyframes,
		IReadOnlyList<Scene> Scenes,
		(int First, int Last)? FreezeFrames = null,
		int? DroppedFrame = null);
}
