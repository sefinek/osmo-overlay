namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     A track file format that lives outside Core (a drone's flight log), added next to FIT and GPX by registering it with
///     ExternalTrackReader at startup. Its file is a log of another device, not a camera's recording: the camera's own
///     telemetry (ICameraFormat) is left as it is and the track's position, altitude and speed are put onto it.
/// </summary>
public interface ITrackFormat
{
	string DisplayName { get; }

	/// <summary>Lower case, with the dot - what the file picker offers.</summary>
	IReadOnlyList<string> Extensions { get; }

	/// <summary>Whether the file is one of this format, from its name and its first bytes (at most 128 of them).</summary>
	bool Recognizes(string path, ReadOnlySpan<byte> start);

	/// <summary>Throws InvalidDataException for a file that can't be read as a track.</summary>
	ExternalTrack Read(string path);

	/// <summary>
	///     The log of the flight a recording belongs to, kept beside it, or null (the default). Only for a format whose files can be
	///     matched to a recording without reading them whole and without anything more than the recording's own start.
	/// </summary>
	string? FindFor(string recordingPath, DateTime recordingStartUtc)
	{
		return null;
	}
}
