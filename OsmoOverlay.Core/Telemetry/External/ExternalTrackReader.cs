using System.Xml;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     A track file from another device - FIT or GPX, told apart by what is in the file, not by its name - or one of the
///     formats registered at startup (ITrackFormat), tried first.
/// </summary>
public static class ExternalTrackReader
{
	private static readonly string[] BuiltInExtensions = [".fit", ".gpx"];

	private static ITrackFormat[] _formats = [];

	public static IReadOnlyList<string> Extensions => [.. BuiltInExtensions, .. _formats.SelectMany(f => f.Extensions)];

	public static void Register(params ITrackFormat[] formats)
	{
		_formats = [.. _formats, .. formats.Where(f => _formats.All(known => known.GetType() != f.GetType()))];
	}

	public static bool IsTrackFile(string path)
	{
		return Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>The track of a registered format that was kept beside the recording and covers its start - the first one found - or null.</summary>
	public static string? FindFor(string recordingPath, DateTime recordingStartUtc)
	{
		foreach (ITrackFormat format in _formats)
		{
			try
			{
				if (format.FindFor(recordingPath, recordingStartUtc) is { } found) return found;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				AppLogger.Warn(ex, $"Looking for the {format.DisplayName} of {Path.GetFileName(recordingPath)} failed.");
			}
		}

		return null;
	}

	public static ExternalTrack Read(string path)
	{
		byte[] start = new byte[128];
		int read;
		using (FileStream stream = File.OpenRead(path)) read = stream.Read(start);

		foreach (ITrackFormat format in _formats)
		{
			if (format.Recognizes(path, start.AsSpan(0, read))) return format.Read(path);
		}

		try
		{
			return FitReader.LooksLikeFit(start.AsSpan(0, read)) ? FitReader.Read(path) : GpxReader.Read(path);
		}
		catch (XmlException ex)
		{
			throw new InvalidDataException($"{Path.GetFileName(path)} is neither a FIT nor a GPX file: {ex.Message}", ex);
		}
	}
}
