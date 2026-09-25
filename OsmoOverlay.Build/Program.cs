using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

const string usage = """
                     Usage: dotnet run --project OsmoOverlay.Build -- [options]

                       --rid <rid>           Runtime identifier to build; repeatable or comma-separated.
                                             Default: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64
                       --flavor <flavor>     self-contained, framework-dependent or all (default: all)
                       --version <version>   Overrides <Version> from Directory.Build.props
                       --output <dir>        Output directory (default: artifacts)
                       --skip-tests          Don't run OsmoOverlay.Tests first
                       --no-archive          Keep the published folders instead of zip/tar.gz archives
                       --no-installer        Don't build the Windows installers
                       --iscc <path>         Inno Setup 7 compiler (default: found in Program Files or on PATH)
                     """;

string[] defaultRids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];

List<string> rids = [];
List<Flavor> flavors = [];
string? versionOverride = null;
string? outputArg = null;
var skipTests = false;
var archive = true;
var installer = true;
string? isccArg = null;

for (var i = 0; i < args.Length; i++)
{
	string Value()
	{
		return i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
	}

	switch (args[i])
	{
		case "--rid":
			rids.AddRange(Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
			break;
		case "--flavor":
			var flavor = Value();
			flavors.AddRange(flavor switch
			{
				"self-contained" => [Flavor.SelfContained],
				"framework-dependent" => [Flavor.FrameworkDependent],
				"all" => [Flavor.SelfContained, Flavor.FrameworkDependent],
				_ => throw new ArgumentException($"Unknown flavor '{flavor}'.")
			});
			break;
		case "--version":
			versionOverride = Value();
			break;
		case "--output":
			outputArg = Value();
			break;
		case "--skip-tests":
			skipTests = true;
			break;
		case "--no-archive":
			archive = false;
			break;
		case "--no-installer":
			installer = false;
			break;
		case "--iscc":
			isccArg = Value();
			break;
		case "-h" or "--help":
			Console.WriteLine(usage);
			return 0;
		default:
			Console.Error.WriteLine($"Unknown option '{args[i]}'.\n\n{usage}");
			return 1;
	}
}

if (rids.Count == 0) rids.AddRange(defaultRids);
if (flavors.Count == 0) flavors.AddRange([Flavor.SelfContained, Flavor.FrameworkDependent]);

var root = FindRepoRoot();
var guiProject = Path.Combine(root, "OsmoOverlay.Gui", "OsmoOverlay.Gui.csproj");
var cliProject = Path.Combine(root, "OsmoOverlay.Cli", "OsmoOverlay.Cli.csproj");
var version = versionOverride ?? XDocument.Load(Path.Combine(root, "Directory.Build.props")).Descendants("Version").First().Value;
var displayVersion = ShortVersion(version);
var outputDir = Path.GetFullPath(outputArg ?? Path.Combine(root, "artifacts"));
Directory.CreateDirectory(outputDir);

var iscc = installer && rids.Any(r => InstallerArchitecture(r) is not null) && flavors.Contains(Flavor.SelfContained) ? FindIscc() : null;
if (installer && iscc is null && rids.Any(r => InstallerArchitecture(r) is not null) && flavors.Contains(Flavor.SelfContained))
	Console.WriteLine("Inno Setup 7 (ISCC.exe) not found - skipping the Windows installers. Pass --iscc <path> or --no-installer.");

Console.WriteLine($"OsmoOverlay {displayVersion}: {string.Join(", ", rids)} ({string.Join(", ", flavors.Select(Suffix))}) -> {outputDir}");
var stopwatch = Stopwatch.StartNew();

try
{
	if (!skipTests) Run("dotnet", "test", "--project", Path.Combine(root, "OsmoOverlay.Tests"), "-c", "Release");

	List<string> produced = [];
	foreach (var rid in rids)
	foreach (Flavor flavor in flavors)
		produced.AddRange(Package(rid, flavor));

	List<string> files = [.. produced.Where(File.Exists)];
	if (files.Count > 0) WriteChecksums(files);

	Console.WriteLine($"\nDone in {stopwatch.Elapsed:mm\\:ss}:");
	foreach (var path in produced)
		Console.WriteLine($"  {Path.GetFileName(path)}{(File.Exists(path) ? $" ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB)" : "")}");
	return 0;
}
catch (Exception ex) when (ex is BuildFailedException or IOException or UnauthorizedAccessException)
{
	Console.Error.WriteLine($"\nBuild failed: {ex.Message}");
	return 1;
}

List<string> Package(string rid, Flavor flavor)
{
	var name = $"OsmoOverlay-{displayVersion}-{rid}-{Suffix(flavor)}";
	var packageDir = Path.Combine(outputDir, name);
	var isWindows = rid.StartsWith("win-", StringComparison.Ordinal);
	var archivePath = Path.Combine(outputDir, name + (isWindows ? ".zip" : ".tar.gz"));
	var installerName = $"OsmoOverlay-{displayVersion}-{rid}-setup";
	var installerPath = Path.Combine(outputDir, installerName + ".exe");
	var buildInstaller = iscc is not null && flavor == Flavor.SelfContained && InstallerArchitecture(rid) is not null;

	Console.WriteLine($"\n=== {name}");
	DeleteIfExists(packageDir);
	DeleteIfExists(archivePath);
	if (buildInstaller) DeleteIfExists(installerPath);

	var isMac = rid.StartsWith("osx-", StringComparison.Ordinal);
	var publishDir = isMac ? Path.Combine(packageDir, "OsmoOverlay.app", "Contents", "MacOS") : packageDir;

	Publish(guiProject, rid, flavor, publishDir);
	Publish(cliProject, rid, flavor, publishDir);
	if (isMac) WriteAppBundle(Path.Combine(packageDir, "OsmoOverlay.app", "Contents"));
	foreach (var document in new[] { "README.md", "LICENSE" })
		File.Copy(Path.Combine(root, document), Path.Combine(packageDir, document));

	List<string> produced = [];
	if (buildInstaller)
	{
		Console.WriteLine($"=== {installerName}");
		Run(iscc!, "-q", Path.Combine(root, "OsmoOverlay.Build", "Installer", "OsmoOverlay.iss"),
			$"-DAppVersion={displayVersion}",
			$"-DFileVersion={(Version.TryParse(version, out _) ? version : "0.0.0.0")}",
			$"-DArchitecture={InstallerArchitecture(rid)}",
			$"-DSourceDir={packageDir}",
			$"-DRepoRoot={root}",
			$"-DOutputDir={outputDir}",
			$"-DOutputName={installerName}");
		produced.Add(installerPath);
	}

	if (!archive) return [packageDir, .. produced];

	if (isWindows) ZipFile.CreateFromDirectory(packageDir, archivePath, CompressionLevel.SmallestSize, true);
	else WriteTarGz(packageDir, archivePath);
	Directory.Delete(packageDir, true);
	return [archivePath, .. produced];
}

static string? InstallerArchitecture(string rid)
{
	return rid switch
	{
		"win-x64" => "x64compatible",
		"win-arm64" => "arm64",
		_ => null
	};
}

string? FindIscc()
{
	if (isccArg is not null) return File.Exists(isccArg) ? isccArg : throw new BuildFailedException($"ISCC not found at {isccArg}.");
	if (!OperatingSystem.IsWindows()) return null;

	IEnumerable<string> candidates =
	[
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 7", "ISCC.exe"),
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 7", "ISCC.exe"),
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Inno Setup 7", "ISCC.exe"),
		.. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Select(dir => Path.Combine(dir, "ISCC.exe"))
	];
	return candidates.FirstOrDefault(File.Exists);
}

void Publish(string project, string rid, Flavor flavor, string destination)
{
	List<string> arguments =
	[
		"publish", project, "-c", "Release", "-r", rid, "-o", destination,
		"--self-contained", flavor == Flavor.SelfContained ? "true" : "false",
		"-p:UseArtifactsOutput=true",
		$"-p:ArtifactsPath={Path.Combine(root, "artifacts", ".build")}",
		"-p:AppendRuntimeIdentifierToOutputPath=true",
		"-p:DebugType=none"
	];
	if (versionOverride is not null) arguments.Add($"-p:Version={versionOverride}");
	Run("dotnet", [.. arguments]);
}

void WriteAppBundle(string contentsDir)
{
	var resourcesDir = Path.Combine(contentsDir, "Resources");
	Directory.CreateDirectory(resourcesDir);
	File.Copy(Path.Combine(root, "OsmoOverlay.Gui", "Assets", "OsmoOverlay.icns"), Path.Combine(resourcesDir, "OsmoOverlay.icns"));

	var plist = $"""
	             <?xml version="1.0" encoding="UTF-8"?>
	             <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
	             <plist version="1.0">
	             <dict>
	               <key>CFBundleName</key><string>OsmoOverlay</string>
	               <key>CFBundleDisplayName</key><string>OsmoOverlay</string>
	               <key>CFBundleIdentifier</key><string>com.sefinek.osmooverlay</string>
	               <key>CFBundleExecutable</key><string>OsmoOverlay</string>
	               <key>CFBundleIconFile</key><string>OsmoOverlay.icns</string>
	               <key>CFBundlePackageType</key><string>APPL</string>
	               <key>CFBundleShortVersionString</key><string>{displayVersion}</string>
	               <key>CFBundleVersion</key><string>{version}</string>
	               <key>NSHighResolutionCapable</key><true/>
	             </dict>
	             </plist>
	             """;
	File.WriteAllText(Path.Combine(contentsDir, "Info.plist"), plist);
}

static void WriteTarGz(string sourceDir, string archivePath)
{
	using FileStream file = File.Create(archivePath);
	using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
	using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
	AddDirectory(tar, sourceDir, Path.GetFileName(sourceDir));
}

static void AddDirectory(TarWriter tar, string directory, string entryName)
{
	const UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
	                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
	                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
	const UnixFileMode regular = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

	tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, entryName + "/") { Mode = executable });

	foreach (var subdirectory in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
		AddDirectory(tar, subdirectory, $"{entryName}/{Path.GetFileName(subdirectory)}");

	foreach (var path in Directory.GetFiles(directory).Order(StringComparer.Ordinal))
	{
		var fileName = Path.GetFileName(path);
		using FileStream data = File.OpenRead(path);
		tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{entryName}/{fileName}")
		{
			Mode = fileName is "OsmoOverlay" or "OsmoOverlay.Cli" or "createdump" ? executable : regular,
			ModificationTime = File.GetLastWriteTimeUtc(path),
			DataStream = data
		});
	}
}

void WriteChecksums(IEnumerable<string> archives)
{
	var checksumPath = Path.Combine(outputDir, $"OsmoOverlay-{displayVersion}-SHA256SUMS.txt");
	File.WriteAllLines(checksumPath, archives.Select(path =>
	{
		using FileStream stream = File.OpenRead(path);
		return $"{Convert.ToHexStringLower(SHA256.HashData(stream))}  {Path.GetFileName(path)}";
	}));
}

void Run(string command, params string[] arguments)
{
	var psi = new ProcessStartInfo(command) { UseShellExecute = false, WorkingDirectory = root };
	foreach (var argument in arguments) psi.ArgumentList.Add(argument);

	using Process process = Process.Start(psi) ?? throw new BuildFailedException($"Could not start {command}.");
	process.WaitForExit();
	if (process.ExitCode != 0)
		throw new BuildFailedException($"{Path.GetFileNameWithoutExtension(command)} {arguments[0]} exited with code {process.ExitCode}.");
}

static void DeleteIfExists(string path)
{
	if (Directory.Exists(path)) Directory.Delete(path, true);
	else if (File.Exists(path)) File.Delete(path);
}

static string FindRepoRoot()
{
	for (DirectoryInfo? dir = new(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
		if (File.Exists(Path.Combine(dir.FullName, "OsmoOverlay.slnx")))
			return dir.FullName;

	throw new BuildFailedException("OsmoOverlay.slnx not found - run from inside the repository.");
}

static string ShortVersion(string version)
{
	var parts = version.Split('.');
	return parts.Length == 4 && parts[3] == "0" ? string.Join('.', parts[..3]) : version;
}

static string Suffix(Flavor flavor)
{
	return flavor == Flavor.SelfContained ? "self-contained" : "framework-dependent";
}

internal enum Flavor
{
	SelfContained,
	FrameworkDependent
}

internal sealed class BuildFailedException(string message) : Exception(message);
