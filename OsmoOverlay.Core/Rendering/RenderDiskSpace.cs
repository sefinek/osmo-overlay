namespace OsmoOverlay.Core;

/// <summary>
///     Whether a render fits on the output's drive, checked before it starts. The render keeps the source's own bitrate
///     (constant), so its size follows the source's size per frame. Finishing it may copy the file once more next to it
///     (Mp4FastStart), so the space needed is twice the estimate.
/// </summary>
public static class RenderDiskSpace
{
	/// <summary>Room on top of the estimate: bitrate peaks, the file's index, and the drive not being filled to the last byte.</summary>
	public const double Headroom = 1.05;

	public static long EstimateOutputBytes(long sourceBytes, long sourceFrames, long outputFrames)
	{
		if (sourceBytes <= 0 || sourceFrames <= 0 || outputFrames <= 0) return 0;

		return (long)(sourceBytes * Math.Min(1.0, (double)outputFrames / sourceFrames) * Headroom);
	}

	/// <summary>The render itself plus the copy finishing it may make.</summary>
	public static long RequiredBytes(long estimatedOutputBytes)
	{
		return estimatedOutputBytes * 2;
	}

	/// <summary>
	///     Free bytes for a file at `outputPath`, counting an existing file there (it's overwritten); null when the drive
	///     can't be found or read. The drive is the mount point the path is deepest under, so it works on Linux and macOS too.
	/// </summary>
	public static long? AvailableBytes(string outputPath)
	{
		try
		{
			string fullPath = Path.GetFullPath(outputPath);
			DriveInfo? drive = DriveInfo.GetDrives()
				.Where(d => IsUnder(fullPath, d.RootDirectory.FullName))
				.MaxBy(d => d.RootDirectory.FullName.Length);
			if (drive is not { IsReady: true }) return null;

			long existing = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
			return drive.AvailableFreeSpace + existing;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	internal static bool IsUnder(string path, string root)
	{
		StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		if (!path.StartsWith(root, comparison)) return false;

		// "/mnt/data" isn't under "/mnt/d".
		return path.Length == root.Length || Path.EndsInDirectorySeparator(root) || path[root.Length] == Path.DirectorySeparatorChar ||
		       path[root.Length] == Path.AltDirectorySeparatorChar;
	}
}
