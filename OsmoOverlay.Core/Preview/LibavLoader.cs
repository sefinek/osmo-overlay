using FFmpeg.AutoGen;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Finds and binds the FFmpeg shared libraries FFmpeg.AutoGen was generated against (avcodec-63 etc., FFmpeg 9)
///     - once per process. Windows: the Gyan "shared" build's bin folder on PATH (the static build has no DLLs),
///     or an "ffmpeg" folder next to the app. macOS: Homebrew's lib folder. Linux: the system loader's paths.
///     The dependency check counts FFmpeg without these libraries as missing (ExternalTool.NeedsSharedLibraries).
/// </summary>
public static class LibavLoader
{
	private static readonly Lock Gate = new();
	private static bool _loaded;

	/// <summary>Where the libraries were loaded from once TryLoad succeeded; null for the system loader's paths (Linux).</summary>
	public static string? LibraryDirectory { get; private set; }

	/// <summary>
	///     Null once the libraries are bound, else why they can't be. A success is final; a failure is looked at
	///     again on the next call, so installing the libraries while the app runs is picked up.
	/// </summary>
	public static string? TryLoad()
	{
		lock (Gate)
		{
			if (_loaded) return null;

			var failure = Load();
			_loaded = failure is null;
			return failure;
		}
	}

	private static string? Load()
	{
		var avcodec = LibraryFileName("avcodec", ffmpeg.LibraryVersionMap["avcodec"]);
		var directory = CandidateDirectories().FirstOrDefault(d => File.Exists(Path.Combine(d, avcodec)));
		if (directory is null && !OperatingSystem.IsLinux())
			return $"{avcodec} was not found - it comes with the FFmpeg {typeof(ffmpeg).Assembly.GetName().Version?.Major} shared build" +
			       (OperatingSystem.IsWindows() ? $" (winget install {RequiredTools.Ffmpeg.WingetId})" : "");

		try
		{
			ffmpeg.RootPath = directory ?? "";
			DynamicallyLoadedBindings.Initialize();
			var version = ffmpeg.avcodec_version() >> 16;
			if (version != ffmpeg.LIBAVCODEC_VERSION_MAJOR)
				return $"libavcodec {version} was found, {ffmpeg.LIBAVCODEC_VERSION_MAJOR} is needed";

			ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
			LibraryDirectory = directory;
			AppLogger.Info($"Preview decodes in-process with FFmpeg {ffmpeg.av_version_info()} ({directory ?? "system libraries"})");
			return null;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or BadImageFormatException)
		{
			return $"the FFmpeg libraries couldn't be loaded: {ex.Message}";
		}
	}

	private static string LibraryFileName(string name, int major)
	{
		if (OperatingSystem.IsWindows()) return $"{name}-{major}.dll";
		if (OperatingSystem.IsMacOS()) return $"lib{name}.{major}.dylib";
		return $"lib{name}.so.{major}";
	}

	private static IEnumerable<string> CandidateDirectories()
	{
		yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
		if (OperatingSystem.IsMacOS())
		{
			yield return "/opt/homebrew/lib";
			yield return "/usr/local/lib";
		}

		foreach (var directory in DependencyChecker.SearchPath())
			yield return directory;
	}
}
