using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Core.Dependencies;

public static class DependencyChecker
{
	/// <summary>
	///     Resolves a command to the executable Process.Start would launch, without running anything - the
	///     same lookup order it uses (app directory, then PATH; on Windows only ".exe" is appended, CreateProcess
	///     ignores PATHEXT). Null when it isn't found.
	/// </summary>
	public static string? FindExecutable(string command)
	{
		var fileName = OperatingSystem.IsWindows() && !Path.HasExtension(command) ? command + ".exe" : command;
		if (FromFfmpegLibraryFolder(command, fileName) is { } sameInstall) return sameInstall;

		IEnumerable<string> directories = new[] { AppContext.BaseDirectory }
			.Concat(SplitPath(Environment.GetEnvironmentVariable("PATH")));

		foreach (var directory in directories)
		{
			string candidate;
			try
			{
				candidate = Path.Combine(directory, fileName);
			}
			catch (ArgumentException)
			{
				continue;
			}

			if (IsExecutableFile(candidate)) return candidate;
		}

		return null;
	}

	/// <summary>What ProcessHelper starts for a command: FFmpeg's resolved to one install (FromFfmpegLibraryFolder), anything else left to Process.Start's own lookup.</summary>
	public static string ResolveCommand(string command)
	{
		return RequiredTools.Ffmpeg.Commands.Contains(command) ? FindExecutable(command) ?? command : command;
	}

	/// <summary>
	///     FFmpeg's own commands come from the install its shared libraries are loaded from (LibavLoader) - the
	///     same folder (Windows builds keep the DLLs next to the exe) or, for a lib folder, the bin folder beside it
	///     (Homebrew's /opt/homebrew/lib and /bin) - so the render and probing run the same FFmpeg the preview
	///     decodes with, whatever order PATH lists several builds in. On Windows Gyan's static build listed ahead of
	///     the shared one would otherwise win: another version, and ~800 ms per start instead of ~25 ms. Only looks
	///     the folder up rather than loading the libraries, so the CLI doesn't bind them just to start ffmpeg.
	///     On Linux the libraries come from the system loader's paths, and PATH decides.
	/// </summary>
	private static string? FromFfmpegLibraryFolder(string command, string fileName)
	{
		if (!RequiredTools.Ffmpeg.Commands.Contains(command)) return null;

		var directory = LibavLoader.IsLoaded ? LibavLoader.LibraryDirectory : LibavLoader.FindLibraryDirectory();
		if (directory is null) return null;

		var sameFolder = Path.Combine(directory, fileName);
		if (IsExecutableFile(sameFolder)) return sameFolder;

		directory = Path.TrimEndingDirectorySeparator(directory);
		if (Path.GetFileName(directory) != "lib" || Path.GetDirectoryName(directory) is not { } prefix) return null;

		var besideLib = Path.Combine(prefix, "bin", fileName);
		return IsExecutableFile(besideLib) ? besideLib : null;
	}

	/// <summary>The directories on this process's PATH, in order.</summary>
	public static IEnumerable<string> SearchPath()
	{
		return SplitPath(Environment.GetEnvironmentVariable("PATH"));
	}

	public static bool IsCommandAvailable(string command)
	{
		return FindExecutable(command) is not null;
	}

	public static bool IsAvailable(ExternalTool tool)
	{
		return tool.Commands.All(IsCommandAvailable) && (!tool.NeedsSharedLibraries || LibavLoader.TryLoad() is null);
	}

	public static IReadOnlyList<ExternalTool> FindMissing(IEnumerable<ExternalTool> tools)
	{
		RefreshProcessPath();
		return tools.Where(t => !IsAvailable(t)).ToList();
	}

	/// <summary>
	///     A process gets its PATH once, from its parent - so if a tool was installed (and PATH updated in the
	///     registry) after the parent started (Explorer, VS, a terminal), this app would report the tool as
	///     missing until the parent itself restarts. Windows: re-reads machine + user PATH from the registry
	///     the way a fresh login would, keeping any process-only entries after them. macOS: an app launched
	///     from Finder gets only /usr/bin:/bin:/usr/sbin:/sbin, without Homebrew's prefix. Child processes
	///     (ffmpeg, exiftool) inherit the result.
	/// </summary>
	public static void RefreshProcessPath()
	{
		IEnumerable<string> current = SplitPath(Environment.GetEnvironmentVariable("PATH"));
		IEnumerable<string> merged;

		if (OperatingSystem.IsWindows())
			merged = SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine))
				.Concat(SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)))
				.Concat(current);
		else if (OperatingSystem.IsMacOS())
			merged = current.Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin" }.Where(Directory.Exists));
		else
			return;

		List<string> distinct = merged.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
		if (distinct.Count > 0) Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, distinct));
	}

	private static IEnumerable<string> SplitPath(string? path)
	{
		return (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(p => p.Trim('"'));
	}

	private static bool IsExecutableFile(string path)
	{
		if (!File.Exists(path)) return false;
		if (OperatingSystem.IsWindows()) return true;

		const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
		return (File.GetUnixFileMode(path) & anyExecute) != 0;
	}
}
