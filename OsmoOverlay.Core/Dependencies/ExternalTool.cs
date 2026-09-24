using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Core.Dependencies;

public sealed record ExternalTool(
	string DisplayName,
	IReadOnlyList<string> Commands,
	string WingetId,
	string AptPackage,
	string DnfPackage,
	string PacmanPackage,
	string BrewPackage,
	string VersionCommand,
	IReadOnlyList<string> VersionArgs,
	// The FFmpeg libraries the preview decodes with (LibavLoader) must load too - the commands alone aren't enough.
	bool NeedsSharedLibraries = false,
	// Install/update never goes past this major - a newer one wouldn't load (LibavLoader.SupportedMajorVersion).
	int? SupportedMajorVersion = null);

public static class RequiredTools
{
	// Gyan's full build in its shared flavour: every encoder (NVENC, x265...) like the static Gyan.FFmpeg, split
	// into DLLs - which the preview loads in-process (LibavLoader), and a 0.6 MB ffmpeg.exe that starts in
	// ~25 ms where the 217 MB static one took ~800 ms (Defender scans the whole exe on every launch). Not
	// .Essentials (fewer libraries). A static build alone counts as missing: it has no libraries to load.
	public static readonly ExternalTool Ffmpeg = new(
		"FFmpeg",
		["ffmpeg", "ffprobe"],
		"Gyan.FFmpeg.Shared",
		"ffmpeg",
		"ffmpeg",
		"ffmpeg",
		"ffmpeg",
		"ffmpeg",
		["-version"],
		true,
		LibavLoader.SupportedMajorVersion);

	public static readonly ExternalTool ExifTool = new(
		"ExifTool",
		["exiftool"],
		"OliverBetz.ExifTool",
		"libimage-exiftool-perl",
		"perl-Image-ExifTool",
		"perl-image-exiftool",
		"exiftool",
		"exiftool",
		["-ver"]);

	public static IReadOnlyList<ExternalTool> All { get; } = [Ffmpeg, ExifTool];
}
