namespace OsmoOverlay.Core.Dependencies;

public sealed record ExternalTool(
	string DisplayName,
	IReadOnlyList<string> Commands,
	string WingetId,
	string AptPackage,
	string DnfPackage,
	string PacmanPackage,
	string BrewPackage);

public static class RequiredTools
{
	public static readonly ExternalTool Ffmpeg = new(
		"FFmpeg",
		["ffmpeg", "ffprobe"],
		"Gyan.FFmpeg",
		"ffmpeg",
		"ffmpeg",
		"ffmpeg",
		"ffmpeg");

	public static readonly ExternalTool ExifTool = new(
		"ExifTool",
		["exiftool"],
		"OliverBetz.ExifTool",
		"libimage-exiftool-perl",
		"perl-Image-ExifTool",
		"perl-image-exiftool",
		"exiftool");

	public static IReadOnlyList<ExternalTool> All { get; } = [Ffmpeg, ExifTool];
}
