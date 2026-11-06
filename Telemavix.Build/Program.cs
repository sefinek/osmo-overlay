using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;

const string usage = """
                     Usage: dotnet run --project Telemavix.Build -- [options]

                       --rid <rid>           Runtime identifier to build; repeatable or comma-separated.
                                             Default: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64
                       --flavor <flavor>     self-contained, framework-dependent or all (default: all)
                       --version <version>   Overrides <Version> from Directory.Build.props
                       --output <dir>        Output directory (default: artifacts)
                       --skip-tests          Don't run Telemavix.Tests first
                       --no-archive          Keep the published folders instead of zip/tar.gz archives
                       --no-installer        Don't build the Windows installers
                       --iscc <path>         Inno Setup 7 compiler (default: found in Program Files or on PATH)
                     """;

string[] defaultRids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];

List<string> rids = [];
List<Flavor> flavors = [];
string? versionOverride = null;
string? outputArg = null;
bool skipTests = false;
bool archive = true;
bool installer = true;
string? isccArg = null;

for (int i = 0; i < args.Length; i++)
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
			string flavor = Value();
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

string root = FindRepoRoot();
string guiProject = Path.Combine(root, "Telemavix.Gui", "Telemavix.Gui.csproj");
string cliProject = Path.Combine(root, "Telemavix.Cli", "Telemavix.Cli.csproj");
string version = versionOverride ?? XDocument.Load(Path.Combine(root, "Directory.Build.props")).Descendants("Version").First().Value;
string displayVersion = ShortVersion(version);
string outputDir = Path.GetFullPath(outputArg ?? Path.Combine(root, "artifacts"));
string buildDir = Path.Combine(root, "artifacts", ".build");
Directory.CreateDirectory(outputDir);

string? iscc = installer && rids.Any(r => InstallerArchitecture(r) is not null) && flavors.Contains(Flavor.SelfContained) ? FindIscc() : null;
if (installer && iscc is null && rids.Any(r => InstallerArchitecture(r) is not null) && flavors.Contains(Flavor.SelfContained))
	Console.WriteLine("Inno Setup 7 (ISCC.exe) not found - skipping the Windows installers. Pass --iscc <path> or --no-installer.");

Console.WriteLine($"Telemavix Studio {displayVersion}: {string.Join(", ", rids)} ({string.Join(", ", flavors.Select(Suffix))}) -> {outputDir}");
var stopwatch = Stopwatch.StartNew();

try
{
	Clean();
	if (!skipTests) Run("dotnet", "test", "--project", Path.Combine(root, "Telemavix.Tests"), "-c", "Release");

	List<string> produced = [];
	foreach (string rid in rids)
	foreach (Flavor flavor in flavors)
		produced.AddRange(Package(rid, flavor));

	List<string> files = [.. produced.Where(File.Exists)];
	if (files.Count > 0) WriteChecksums(files);

	Console.WriteLine($"\nDone in {stopwatch.Elapsed:mm\\:ss}:");
	foreach (string path in produced)
		Console.WriteLine($"  {Path.GetFileName(path)}{(File.Exists(path) ? $" ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB)" : "")}");
	return 0;
}
catch (Exception ex) when (ex is BuildFailedException or IOException or UnauthorizedAccessException)
{
	Console.Error.WriteLine($"\nBuild failed: {ex.Message}");
	return 1;
}
finally
{
	TryDelete(buildDir);
}

List<string> Package(string rid, Flavor flavor)
{
	string name = $"Telemavix-{displayVersion}-{rid}-{Suffix(flavor)}";
	string packageDir = Path.Combine(outputDir, name);
	bool isWindows = rid.StartsWith("win-", StringComparison.Ordinal);
	string archivePath = Path.Combine(outputDir, name + (isWindows ? ".zip" : ".tar.gz"));
	string installerName = $"Telemavix-{displayVersion}-{rid}-setup";
	string installerPath = Path.Combine(outputDir, installerName + ".exe");
	bool buildInstaller = iscc is not null && flavor == Flavor.SelfContained && InstallerArchitecture(rid) is not null;

	Console.WriteLine($"\n=== {name}");

	bool isMac = rid.StartsWith("osx-", StringComparison.Ordinal);
	string publishDir = isMac ? Path.Combine(packageDir, "Telemavix.app", "Contents", "MacOS") : packageDir;

	Publish(guiProject, rid, flavor, publishDir);
	Publish(cliProject, rid, flavor, publishDir);
	if (isMac) WriteAppBundle(Path.Combine(packageDir, "Telemavix.app", "Contents"));
	if (rid.StartsWith("linux-", StringComparison.Ordinal)) WriteDesktopEntryInstaller(packageDir);
	foreach (string document in new[] { "README.md", "LICENSE", "LICENSING.md" })
		File.Copy(Path.Combine(root, document), Path.Combine(packageDir, document));

	List<string> produced = [];
	if (buildInstaller)
	{
		Console.WriteLine($"=== {installerName}");
		Run(iscc!, "-q", Path.Combine(root, "Telemavix.Build", "Installer", "Telemavix.iss"),
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
		$"-p:ArtifactsPath={buildDir}",
		"-p:AppendRuntimeIdentifierToOutputPath=true",
		"-p:DebugType=none"
	];
	if (versionOverride is not null) arguments.Add($"-p:Version={versionOverride}");
	Run("dotnet", [.. arguments]);
}

// A .desktop file needs absolute paths, and where the archive gets extracted isn't known - so the package carries the
// icon and a script that writes the menu entry for wherever the folder is.
void WriteDesktopEntryInstaller(string packageDir)
{
	File.Copy(Path.Combine(root, "Telemavix.Gui", "Assets", "Telemavix.png"), Path.Combine(packageDir, "Telemavix.png"));

	const string script = """
	                      #!/bin/sh
	                      # Adds Telemavix Studio to the application menu for this folder; --remove takes it out again.
	                      set -e
	                      entry="${XDG_DATA_HOME:-$HOME/.local/share}/applications/telemavix.desktop"

	                      if [ "$1" = "--remove" ]; then
	                        rm -f "$entry"
	                        echo "Removed $entry"
	                        exit 0
	                      fi

	                      dir=$(cd "$(dirname "$0")" && pwd)
	                      mkdir -p "$(dirname "$entry")"
	                      cat > "$entry" <<EOF
	                      [Desktop Entry]
	                      Type=Application
	                      Name=Telemavix Studio
	                      Comment=Telemetry HUD for DJI Osmo Action footage
	                      Exec="$dir/Telemavix"
	                      Path=$dir
	                      Icon=$dir/Telemavix.png
	                      Terminal=false
	                      Categories=AudioVideo;Video;
	                      EOF
	                      echo "Added Telemavix Studio to the application menu: $entry"

	                      """;
	File.WriteAllText(Path.Combine(packageDir, "install-desktop-entry.sh"), script.ReplaceLineEndings("\n"));
}

void WriteAppBundle(string contentsDir)
{
	string resourcesDir = Path.Combine(contentsDir, "Resources");
	Directory.CreateDirectory(resourcesDir);
	File.Copy(Path.Combine(root, "Telemavix.Gui", "Assets", "Telemavix.icns"), Path.Combine(resourcesDir, "Telemavix.icns"));

	string plist = $"""
	                <?xml version="1.0" encoding="UTF-8"?>
	                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
	                <plist version="1.0">
	                <dict>
	                  <key>CFBundleName</key><string>Telemavix Studio</string>
	                  <key>CFBundleDisplayName</key><string>Telemavix Studio</string>
	                  <key>CFBundleIdentifier</key><string>net.sefinek.telemavix</string>
	                  <key>CFBundleExecutable</key><string>Telemavix</string>
	                  <key>CFBundleIconFile</key><string>Telemavix.icns</string>
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

	foreach (string subdirectory in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
		AddDirectory(tar, subdirectory, $"{entryName}/{Path.GetFileName(subdirectory)}");

	foreach (string path in Directory.GetFiles(directory).Order(StringComparer.Ordinal))
	{
		string fileName = Path.GetFileName(path);
		using FileStream data = File.OpenRead(path);
		tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{entryName}/{fileName}")
		{
			Mode = fileName is "Telemavix" or "Telemavix.Cli" or "createdump" or "install-desktop-entry.sh" ? executable : regular,
			ModificationTime = File.GetLastWriteTimeUtc(path),
			DataStream = data
		});
	}
}

void WriteChecksums(IEnumerable<string> archives)
{
	string checksumPath = Path.Combine(outputDir, $"Telemavix-{displayVersion}-SHA256SUMS.txt");
	File.WriteAllLines(checksumPath, archives.Select(path =>
	{
		using FileStream stream = File.OpenRead(path);
		return $"{Convert.ToHexStringLower(SHA256.HashData(stream))}  {Path.GetFileName(path)}";
	}));
}

void Run(string command, params string[] arguments)
{
	var psi = new ProcessStartInfo(command) { UseShellExecute = false, WorkingDirectory = root };
	foreach (string argument in arguments) psi.ArgumentList.Add(argument);

	using Process process = Process.Start(psi) ?? throw new BuildFailedException($"Could not start {command}.");
	process.WaitForExit();
	if (process.ExitCode != 0)
		throw new BuildFailedException($"{Path.GetFileNameWithoutExtension(command)} {arguments[0]} exited with code {process.ExitCode}.");
}

// Every build starts from nothing: the Release outputs (Debug is left alone - a build or a running copy from the IDE
// isn't touched), the packaging's own bin/obj, and every package an earlier run left in the output folder, of any
// version. Only Telemavix-* is removed there, since --output can point at a folder holding anything else.
void Clean()
{
	Console.WriteLine("Cleaning...");
	Run("dotnet", "clean", Path.Combine(root, "Telemavix.slnx"), "-c", "Release", "-v", "q", "-nologo");

	DeleteIfExists(buildDir);
	foreach (string path in Directory.EnumerateFileSystemEntries(outputDir, "Telemavix-*"))
		DeleteIfExists(path);
}

// After the build the intermediate folder is only dead weight (hundreds of MB across the RIDs); a leftover file still in
// use shouldn't turn a finished build into a failed one.
static void TryDelete(string path)
{
	try
	{
		if (Directory.Exists(path)) Directory.Delete(path, true);
	}
	catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
	{
		Console.WriteLine($"Could not remove {path}: {ex.Message}");
	}
}

static void DeleteIfExists(string path)
{
	try
	{
		if (Directory.Exists(path)) Directory.Delete(path, true);
		else if (File.Exists(path)) File.Delete(path);
	}
	catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
	{
		throw new BuildFailedException($"Can't delete {path} - it's in use (a running setup or app?). Close it and try again.");
	}
}

static string FindRepoRoot()
{
	for (DirectoryInfo? dir = new(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
	{
		if (File.Exists(Path.Combine(dir.FullName, "Telemavix.slnx")))
			return dir.FullName;
	}

	throw new BuildFailedException("Telemavix.slnx not found - run from inside the repository.");
}

static string ShortVersion(string version)
{
	string[] parts = version.Split('.');
	return parts is [_, _, _, "0"] ? string.Join('.', parts[..3]) : version;
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
