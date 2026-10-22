namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>A track file from another device - FIT or GPX, told apart by what is in the file, not by its name.</summary>
public static class ExternalTrackReader
{
	public static readonly string[] Extensions = [".fit", ".gpx"];

	public static bool IsTrackFile(string path)
	{
		return Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
	}

	public static ExternalTrack Read(string path)
	{
		byte[] start = new byte[12];
		int read;
		using (FileStream stream = File.OpenRead(path)) read = stream.Read(start);

		try
		{
			return FitReader.LooksLikeFit(start.AsSpan(0, read)) ? FitReader.Read(path) : GpxReader.Read(path);
		}
		catch (System.Xml.XmlException ex)
		{
			throw new InvalidDataException($"{Path.GetFileName(path)} is neither a FIT nor a GPX file: {ex.Message}", ex);
		}
	}
}
