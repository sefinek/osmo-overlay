using System.Diagnostics.CodeAnalysis;
using System.Xml;
using Telemavix.Core.Logging;

namespace Telemavix.Core.Telemetry.External;

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
		(ITrackFormat? format, bool isFit) = Detect(path);
		return format is not null ? format.Read(path) : ReadBuiltIn(path, isFit);
	}

	public static bool TryRead(string path, [NotNullWhen(true)] out ExternalTrack? track, out string? problem)
	{
		track = null;
		problem = null;
		try
		{
			(ITrackFormat? format, bool isFit) = Detect(path);
			if (format is not null) return format.TryRead(path, out track, out problem);
			track = ReadBuiltIn(path, isFit);
			return true;
		}
		catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
		{
			problem = ex.Message;
			return false;
		}
	}

	private static (ITrackFormat? Format, bool IsFit) Detect(string path)
	{
		Span<byte> start = stackalloc byte[128];
		int read;
		using (FileStream stream = File.OpenRead(path)) read = stream.Read(start);
		foreach (ITrackFormat format in _formats)
		{
			if (format.Recognizes(path, start[..read])) return (format, false);
		}

		return (null, FitReader.LooksLikeFit(start[..read]));
	}

	private static ExternalTrack ReadBuiltIn(string path, bool isFit)
	{
		try
		{
			return isFit ? FitReader.Read(path) : GpxReader.Read(path);
		}
		catch (XmlException ex)
		{
			throw new InvalidDataException($"{Path.GetFileName(path)} is neither a FIT nor a GPX file: {ex.Message}", ex);
		}
	}
}
