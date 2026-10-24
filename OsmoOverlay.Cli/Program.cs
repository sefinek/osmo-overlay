using System.Globalization;
using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Cameras.GoPro;
using OsmoOverlay.Cameras.Insta360;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

UiLanguages.Apply(UiLanguages.English);

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
	if (e.ExceptionObject is Exception ex) AppLogger.Error(ex, $"Fatal error: {ex.Message}");
};
foreach (string line in AppBanner.BuildLines("CLI"))
	AppLogger.Info(line);

CameraFormats.Register(new DjiOsmoFormat(), new GoProFormat(), new Insta360Format());

string[] options =
[
	"-o", "--frames", "--from", "--to", "--cut", "--view", "--encoder", "--track", "--track-offset", "--track-anchor", "--volume", "--music",
	"--settings-preset", "--layout", "--profile", "--alpha", "--existing", "--export-telemetry", "--telemetry-interval", "--expect-lean"
];
string[] flags = ["--no-level", "--no-audio", "--each"];
const string usage = "Usage: OsmoOverlay.Cli <input1> [input2 ...] [-o <output.mp4>] [--frames N] [--from <time>] [--to <time>] [--cut <time>-<time> ...] [--view <yaw>,<pitch>,<roll>[,<fov>]] [--no-level] [--no-audio] [--encoder <name>] [--track <file.fit|file.gpx> [--track-offset <seconds> | --track-anchor <video-time>=<UTC> ...]]\n" +
                     "                         [--volume <time>:<dB>,...] [--music <file>[;start=..][;in=..][;len=..][;db=..][;fadein=..][;fadeout=..][;duck=..][;loop] ...]\n" +
                     "                         [--settings-preset <name>] [--layout <name>] [--profile <name>] [--alpha <prores|vp9>] [--existing <skip|overwrite|number>]\n" +
                     "  inputs are the camera's own recordings (DJI Osmo Action, GoPro or Insta360)\n" +
                     "  <time> is seconds (90, 90.5) or [h:]mm:ss[.fff] (1:30, 1:02:03.25) on the combined timeline of all inputs\n" +
                     "  --cut removes that part from the video and the telemetry; repeat it for several cuts. Add @fade or @white (optionally =seconds, e.g. 1:00-1:10@fade=1) to fade through black or white where the cut joins the kept parts, or @cross, @wipeleft, @wiperight, @slideleft, @slideright to blend them (the output gets shorter by that length)\n" +
                     "  --no-audio leaves the sound track out of the output\n" +
                     "  --volume sets the sound's level over the recording: points of <time>:<dB> joined by straight lines, 'off' for silence (0:off,2:0 fades in)\n" +
                     "  --music mixes a music file over the sound; times are on the output after cuts. Repeat it for several clips. 'len' is needed with 'loop'\n" +
                     "  --view <yaw>,<pitch>,<roll>[,<fov>] frames a 360 recording's flat picture, in degrees (default 0,0,0,100),\n" +
                     "         leveled by the camera's accelerometer unless --no-level\n" +
                     "  --encoder picks ffmpeg's video encoder (hevc_nvenc, hevc_amf, hevc_qsv, libx265, or h264_* / libx264 for H.264 output)\n" +
                     "         instead of the first one that works here; one that doesn't write the output's codec is ignored\n" +
                     "  --settings-preset, --layout and --profile (an export profile) take a name or id from --list-presets; they apply to this render only\n" +
                     "  --alpha renders the overlay alone on a transparent background (ProRes 4444 .mov or VP9 .webm) instead of the video\n" +
                     "  --existing says what to do when the output is already there (default: overwrite)\n" +
                     "  --track puts a FIT or GPX track from another device (a bike computer, a watch) on the telemetry: its position, altitude and\n" +
                     "         speed. --track-anchor <video-time>=<GPS-UTC> aligns an event, e.g. 18.5=2026-01-01T12:00:00Z. Repeat once to correct clock drift.\n" +
                     "         Anchors use the combined recording before cuts and work without the camera's date. --track-offset <seconds> instead adds to\n" +
                     "         the camera's creation time. Without either, motion is used for an approximate alignment that needs visual verification.\n" +
                     "         Timer pauses and GPS gaps leave position and speed unavailable; they do not shorten the timeline.\n" +
                     "       OsmoOverlay.Cli <project.ovproj> [<project2.ovproj> ...] [<recording> ...] [-o <output>] [--profile <name>] [--alpha <prores|vp9>] [--existing <skip|overwrite|number>]\n" +
                     "  renders saved projects one after another, as the GUI would: recordings, cuts, overlay, sound, music, track and settings preset come from\n" +
                     "  the project. A failed one doesn't stop the rest. Recordings given beside projects are rendered after them, with the options above\n" +
                     "  (--existing defaults to skip for projects). -o only when there is one thing to render\n" +
                     "  --each renders every recording by itself instead of joining them as one (not with --track)\n" +
                     "       OsmoOverlay.Cli <input1> [input2 ...] --export-telemetry <file.csv|file.gpx> [--telemetry-interval <seconds>]\n" +
                     "  writes the processed telemetry (speed, heading, slope, distance, tilt, G) of the whole recording instead of rendering\n" +
                     "       OsmoOverlay.Cli <input1> [input2 ...] --report [--expect-lean <right|left|nose-up|nose-down>]\n" +
                     "  prints what the telemetry says about itself (GPS fix, accelerometer scale, tilt range); with --expect-lean, for a recording\n" +
                     "  made leaning the camera that way, checks that roll or pitch has the right sign (exit code 1 when it doesn't)\n" +
                     "       OsmoOverlay.Cli --list-presets\n" +
                     "  lists the overlay layouts, settings presets and export profiles the options above take\n" +
                     "       OsmoOverlay.Cli --encoder-test\n" +
                     "  tries every video encoder (NVIDIA, AMD, Intel, CPU) the way a render would and prints what ffmpeg says about a failure\n" +
                     "       OsmoOverlay.Cli --install-dependencies [ffmpeg] [exiftool]\n" +
                     "  installs the listed tools (default: ffmpeg) through the system's package manager, if they're missing";

if (args.Length == 0)
{
	Console.WriteLine(usage);
	return 1;
}

switch (args[0])
{
	case "--help" or "-h":
		Console.WriteLine(usage);
		return 0;
	case "--install-dependencies":
		return await InstallDependenciesAsync(args[1..]);
	case "--list-presets":
		return ListPresets();
	case "--encoder-test":
		return EncoderTest();
}

var inputPaths = new List<string>();
string outputPath = "";
int? frameLimit = null;
double? rangeStart = null;
double? rangeEnd = null;
List<TimeRange> cutOuts = [];
ReframeView? view = null;
bool level = true;
string? encoder = null;
string? trackPath = null;
double? trackOffset = null;
List<TrackSyncAnchor> trackAnchors = [];
bool noAudio = false;
List<VolumePoint>? volume = null;
List<string> musicSpecs = [];
string? settingsPresetName = null;
string? layoutName = null;
string? profileName = null;
AlphaCodec? alpha = null;
ExistingOutput? existing = null;
string? exportTelemetryPath = null;
double telemetryInterval = 0;
bool report = false;
bool each = false;
Lean? expectLean = null;
List<string> recordingOnly = [];

int i = 0;
while (i < args.Length && !options.Contains(args[i]) && !flags.Contains(args[i]) && args[i] != "--report")
	inputPaths.Add(args[i++]);

if (inputPaths.Count == 0)
{
	Console.Error.WriteLine("Error: at least one input file is required");
	return 1;
}

for (; i < args.Length; i++)
{
	if (args[i] == "--no-level")
	{
		level = false;
		recordingOnly.Add("--no-level");
		continue;
	}

	if (args[i] == "--no-audio")
	{
		noAudio = true;
		recordingOnly.Add("--no-audio");
		continue;
	}

	if (args[i] == "--report")
	{
		report = true;
		continue;
	}

	if (args[i] == "--each")
	{
		each = true;
		continue;
	}

	if (!options.Contains(args[i])) continue;

	if (i + 1 >= args.Length)
	{
		Console.Error.WriteLine($"Error: missing value for {args[i]}");
		return 1;
	}

	string option = args[i];
	string value = args[++i];
	if (option is not ("-o" or "--profile" or "--alpha" or "--existing" or "--export-telemetry" or "--telemetry-interval" or "--expect-lean"))
		recordingOnly.Add(option);

	switch (option)
	{
		case "-o":
			outputPath = value;
			break;
		case "--frames" when int.TryParse(value, out int parsedFrameLimit) && parsedFrameLimit > 0:
			frameLimit = parsedFrameLimit;
			break;
		case "--from" when TimeText.TryParse(value, out double from):
			rangeStart = from;
			break;
		case "--to" when TimeText.TryParse(value, out double to):
			rangeEnd = to;
			break;
		case "--cut" when value.Split('@') is [var cutRange, .. var cutExtra] && cutExtra.Length <= 1 && cutRange.Split('-') is [var cutFrom, var cutTo] &&
		                  TimeText.TryParse(cutFrom, out double cutStart) && TimeText.TryParse(cutTo, out double cutEnd) && cutEnd > cutStart:
			CutTransition? transition = null;
			if (cutExtra.Length == 1)
			{
				if (!CutTransition.TryParse(cutExtra[0], out CutTransition parsedTransition))
				{
					Console.Error.WriteLine($"Error: invalid transition in --cut: '{cutExtra[0]}' (use fade, white, cross, wipeleft, wiperight, slideleft or slideright, optionally with =seconds)");
					return 1;
				}

				transition = parsedTransition;
			}

			cutOuts.Add(new TimeRange(cutStart, cutEnd, transition));
			break;
		case "--view" when ReframeView.TryParse(value, out ReframeView parsedView):
			view = parsedView;
			break;
		case "--encoder":
			encoder = value;
			break;
		case "--track":
			trackPath = value;
			break;
		case "--track-offset" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedOffset) &&
		                           double.IsFinite(parsedOffset):
			trackOffset = parsedOffset;
			break;
		case "--track-anchor" when TrackSyncAnchor.TryParse(value, out TrackSyncAnchor? anchor) && trackAnchors.Count < 2:
			trackAnchors.Add(anchor!);
			break;
		case "--volume":
			if (!VolumeSpec.TryParse(value, out List<VolumePoint> parsedVolume, out string volumeError))
			{
				Console.Error.WriteLine($"Error: invalid value for --volume: {volumeError}");
				return 1;
			}

			volume = parsedVolume;
			break;
		case "--music":
			musicSpecs.Add(value);
			break;
		case "--settings-preset":
			settingsPresetName = value;
			break;
		case "--layout":
			layoutName = value;
			break;
		case "--profile":
			profileName = value;
			break;
		case "--alpha" when AlphaOverlay.Parse(value) is { } parsedAlpha:
			alpha = parsedAlpha;
			break;
		case "--existing" when Enum.TryParse(value, true, out ExistingOutput parsedExisting) && Enum.IsDefined(parsedExisting):
			existing = parsedExisting;
			break;
		case "--export-telemetry":
			exportTelemetryPath = value;
			break;
		case "--telemetry-interval" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedInterval) &&
		                                 double.IsFinite(parsedInterval) && parsedInterval >= 0:
			telemetryInterval = parsedInterval;
			break;
		case "--expect-lean" when TelemetryReport.ParseLean(value) is { } parsedLean:
			expectLean = parsedLean;
			break;
		default:
			Console.Error.WriteLine($"Error: invalid value for {option}: '{value}'");
			return 1;
	}
}

if (expectLean is not null && !report)
{
	Console.Error.WriteLine("Error: --expect-lean needs --report");
	return 1;
}

ExportProfile? profile = null;
if (profileName is not null && (profile = ExportProfile.Find(profileName)) is null)
{
	Console.Error.WriteLine($"Error: unknown export profile '{profileName}' (see --list-presets)");
	return 1;
}

using var cts = new CancellationTokenSource();
// Ctrl+C goes through RenderJob's cancellation, which stops ffmpeg and removes the unfinished output - ending the
// process outright would leave both behind.
Console.CancelKeyPress += (_, e) =>
{
	e.Cancel = true;
	cts.Cancel();
};

// Projects (.ovproj) are renders of their own, each with everything it holds; the other arguments are the camera's recordings, which
// together are one recording (a camera's split files) - or, with --each, one render per file. Both can be given at once.
List<string> projectPaths = [.. inputPaths.Where(p => p.EndsWith(OverlayProject.Extension, StringComparison.OrdinalIgnoreCase))];
List<string> recordingPaths = [.. inputPaths.Except(projectPaths)];

if (recordingPaths.Count == 0)
{
	if (recordingOnly.Count > 0 || report || exportTelemetryPath is not null || each)
	{
		string used = string.Join(", ", recordingOnly.Concat(each ? ["--each"] : []).Concat(report ? ["--report"] : []).Concat(exportTelemetryPath is not null ? ["--export-telemetry"] : []).Distinct());
		Console.Error.WriteLine($"Error: {used} can't be used with a project alone - it holds those; give a recording");
		return 1;
	}
}
else if (projectPaths.Count > 0 && (report || exportTelemetryPath is not null))
{
	Console.Error.WriteLine("Error: --report and --export-telemetry read recordings; projects can't be given with them");
	return 1;
}

if (outputPath.Length > 0 && ((each && recordingPaths.Count > 1) || projectPaths.Count + (recordingPaths.Count > 0 ? 1 : 0) > 1))
{
	Console.Error.WriteLine("Error: -o names one output, but this renders more than one thing");
	return 1;
}

if (each && (report || exportTelemetryPath is not null || trackPath is not null))
{
	Console.Error.WriteLine("Error: --each renders each recording by itself; it can't be used with --report, --export-telemetry or --track (a track fits one recording)");
	return 1;
}

int exitCode = 0;
if (projectPaths.Count > 0)
{
	exitCode = await RenderProjectsAsync(projectPaths, outputPath.Length > 0 && recordingPaths.Count == 0 ? outputPath : null, profile, alpha,
		existing ?? ExistingOutput.Skip, cts.Token);
	if (recordingPaths.Count == 0 || cts.IsCancellationRequested) return exitCode;
}

if (report || exportTelemetryPath is not null)
{
	if (recordingOnly.Count > 0)
	{
		Console.Error.WriteLine($"Error: {string.Join(", ", recordingOnly.Distinct())} don't apply to --report/--export-telemetry");
		return 1;
	}

	return report ? Report(recordingPaths, expectLean) : ExportTelemetry(recordingPaths, exportTelemetryPath!, telemetryInterval);
}

if ((trackOffset is not null || trackAnchors.Count > 0) && trackPath is null)
{
	Console.Error.WriteLine("Error: --track-offset and --track-anchor need --track");
	return 1;
}

ExternalTrackSource? trackSource = trackPath is null
	? null
	: new ExternalTrackSource(trackPath, trackOffset,
		trackAnchors.Count > 0 ? new TrackSynchronization(trackAnchors[0], trackAnchors.Count > 1 ? trackAnchors[1] : null) : null);
try
{
	trackSource?.Validate();
}
catch (InvalidDataException ex)
{
	Console.Error.WriteLine($"Error: {ex.Message}");
	return 1;
}

if (trackPath is not null && !File.Exists(trackPath))
{
	Console.Error.WriteLine($"Error: track file not found: {trackPath}");
	return 1;
}

OverlaySettings? settings = null;
string? layoutId = null;
if (settingsPresetName is not null)
{
	SettingsPreset? settingsPreset = SettingsPresetStore.Load().Presets.FirstOrDefault(p => Named(p.Id, p.Name, settingsPresetName));
	if (settingsPreset is null)
	{
		Console.Error.WriteLine($"Error: unknown settings preset '{settingsPresetName}' (see --list-presets)");
		return 1;
	}

	settings = settingsPreset.Values.ApplyTo(OverlaySettingsStore.Load());
	layoutId = settingsPreset.LayoutPresetId;
}

if (profile is not null) settings = profile.ApplyTo(settings ?? OverlaySettingsStore.Load());

IReadOnlyList<OverlayElement>? layout = null;
if (layoutName is not null || layoutId is not null)
{
	OverlayPreset? layoutPreset = OverlayPresetStore.Load().Presets.FirstOrDefault(p => layoutName is not null ? Named(p.Id, p.Name, layoutName) : p.Id == layoutId);
	if (layoutPreset is null)
	{
		if (layoutName is not null)
		{
			Console.Error.WriteLine($"Error: unknown overlay layout '{layoutName}' (see --list-presets)");
			return 1;
		}
	}
	else
		layout = OverlayLayers.Drawn(layoutPreset.Elements, layoutPreset.Layers);
}

List<MusicClip> music = [];
foreach (string spec in musicSpecs)
{
	string musicFile = spec.Split(';')[0].Trim();
	if (!File.Exists(musicFile))
	{
		Console.Error.WriteLine($"Error: music file not found: {musicFile}");
		return 1;
	}

	double length;
	try
	{
		length = SourceProbe.ProbeAudioDuration(musicFile);
	}
	catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
	{
		Console.Error.WriteLine($"Error: {musicFile} can't be read as sound: {ex.Message}");
		return 1;
	}

	if (!MusicSpec.TryParse(spec, length, out MusicClip? clip, out string musicError))
	{
		Console.Error.WriteLine($"Error: invalid value for --music '{spec}': {musicError}");
		return 1;
	}

	music.Add(clip!);
}

if (outputPath.Length > 0 && alpha is { } wanted && !Path.GetExtension(outputPath).Equals(AlphaOverlay.Extension(wanted), StringComparison.OrdinalIgnoreCase))
{
	Console.Error.WriteLine($"Error: a {wanted} overlay is written to a {AlphaOverlay.Extension(wanted)} file, not '{outputPath}'");
	return 1;
}

OverlaySettings effective = settings ?? OverlaySettingsStore.Load();
IEnumerable<IReadOnlyList<string>> renders = each ? recordingPaths.Select(p => (IReadOnlyList<string>)[p]) : [recordingPaths];
foreach (IReadOnlyList<string> recording in renders)
{
	string target = outputPath;
	if (target.Length == 0)
	{
		target = RenderOptions.DefaultOutputPath(recording, effective.DefaultOutputFolder);
		if (alpha is { } alphaCodec) target = RenderOptions.AlphaOutputPath(target, alphaCodec);
	}

	if (await RenderRecordingAsync(recording, target) != 0) exitCode = 1;
	if (cts.IsCancellationRequested) break;
}

return exitCode;

async Task<int> RenderRecordingAsync(IReadOnlyList<string> recording, string target)
{
	if (existing is { } policy)
	{
		string? resolved = OutputFiles.Resolve(target, policy);
		if (resolved is null)
		{
			Console.WriteLine($"Skipped: {target} already exists");
			return 0;
		}

		target = resolved;
	}

	var progress = new Progress<RenderStatus>(status =>
	{
		if (status is { Phase: RenderPhase.Rendering, TotalFrames: > 0 } &&
		    (status.Message.StartsWith("Frame") || status.Message.StartsWith("Fetching map tiles:")))
		{
			double pct = 100.0 * status.CurrentFrame / status.TotalFrames;
			Console.Write($"\r  {status.Message} ({pct:0.0}%) - {status.Elapsed:hh\\:mm\\:ss}   ");
		}
		else
		{
			Console.WriteLine(status.Message);
			AppLogger.Info(status.Message);
		}
	});

	RenderResult result = await RenderJob.RunAsync(
		new RenderOptions(recording, target, frameLimit, encoder, Overwrite: existing is null or ExistingOutput.Overwrite, Layout: layout, RangeStartSeconds: rangeStart,
			RangeEndSeconds: rangeEnd, CutOuts: cutOuts, Reframe: (view ?? new ReframeView()) with { Level = level }, ExternalTrack: trackSource,
			NoAudio: noAudio ? true : null, Volume: volume, Music: music.Count > 0 ? music : null, Settings: settings, Alpha: alpha), progress,
		cts.Token);
	Console.WriteLine();

	if (!result.Success)
	{
		// No AppLogger.Error call here - RenderJob already logged this failure to the file log.
		Console.Error.WriteLine($"Error: {result.ErrorMessage}");
		return 1;
	}

	string doneMessage = $"Done: {target} (render time: {result.Elapsed:hh\\:mm\\:ss})";
	Console.WriteLine(doneMessage);
	AppLogger.Info(doneMessage);
	return 0;
}

static bool Named(string id, string name, string wanted)
{
	return id.Equals(wanted, StringComparison.OrdinalIgnoreCase) || name.Equals(wanted, StringComparison.OrdinalIgnoreCase);
}

// Saved projects through the render queue (the GUI's own), so a CLI render and the GUI's come out the same.
static async Task<int> RenderProjectsAsync(IReadOnlyList<string> projects, string? output, ExportProfile? profile, AlphaCodec? alpha, ExistingOutput existing,
	CancellationToken ct)
{
	var queue = new RenderQueue();
	List<QueueItem> items = [.. projects.Select(p => queue.Add(new QueueRequest(Path.GetFullPath(p), output, profile?.Id, alpha)))];
	var shown = new Dictionary<QueueItem, QueueState>();
	object gate = new();
	queue.ItemChanged += item =>
	{
		lock (gate)
		{
			if (item.State == QueueState.Rendering)
			{
				if (item.Progress > 0) Console.Write($"\r  [{items.IndexOf(item) + 1}/{items.Count}] {item.Message} ({item.Progress * 100:0.0}%)   ");
				if (shown.TryAdd(item, QueueState.Rendering)) Console.WriteLine($"[{items.IndexOf(item) + 1}/{items.Count}] {Path.GetFileName(item.Request.ProjectPath)}");
				return;
			}

			if (shown.TryGetValue(item, out QueueState before) && before == item.State) return;

			shown[item] = item.State;
			Console.WriteLine();
			Console.WriteLine(item.State switch
			{
				QueueState.Done => $"  Done: {item.OutputPath} (render time: {item.Elapsed:hh\\:mm\\:ss})",
				QueueState.Skipped => $"  Skipped: {item.Message}",
				QueueState.Cancelled => "  Cancelled",
				_ => $"  Error: {item.Message}"
			});
		}
	};

	await queue.RunAsync(existing, ct);
	int failed = items.Count(i => i.State is QueueState.Failed or QueueState.Cancelled or QueueState.Waiting);
	Console.WriteLine($"{items.Count(i => i.State == QueueState.Done)} done, {items.Count(i => i.State == QueueState.Skipped)} skipped, {failed} failed or not run");
	return failed > 0 ? 1 : 0;
}

static (FileSummary Summary, List<DerivedFrame> Frames, OverlaySettings Settings)? ReadTelemetry(IReadOnlyList<string> paths)
{
	FileSummary? summary;
	string? problem;
	try
	{
		(summary, problem) = FileSummaryReader.Read(paths);
	}
	catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
	{
		Console.Error.WriteLine($"Error: {ex.Message}");
		return null;
	}

	if (summary is null)
	{
		Console.Error.WriteLine($"Error: {problem}");
		return null;
	}

	if (!summary.HasTelemetry || summary.TelemetryFrames is not { Count: > 0 } raw)
	{
		Console.Error.WriteLine("Error: the recording has no telemetry");
		return null;
	}

	OverlaySettings settings = OverlaySettingsStore.Load();
	List<DerivedFrame> frames = TelemetryProcessor.Process(raw, summary.CameraFormat, settings.SmoothGpsMotion, settings.SpeedCorrectionPercent, settings.OrientationSource);
	return (summary, frames, settings);
}

static int ExportTelemetry(IReadOnlyList<string> paths, string target, double interval)
{
	if (!TelemetryExport.IsSupported(target))
	{
		Console.Error.WriteLine("Error: --export-telemetry writes a .csv or a .gpx file");
		return 1;
	}

	if (ReadTelemetry(paths) is not { } read) return 1;

	int written;
	try
	{
		written = TelemetryExport.Write(target, read.Frames, interval, read.Summary.ContainerRecordingStartUtc);
	}
	catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
	{
		Console.Error.WriteLine($"Error: {ex.Message}");
		return 1;
	}

	Console.WriteLine($"Done: {target} ({written} frames{(interval > 0 ? $", one in {interval:0.###} s" : "")})");
	return 0;
}

static int Report(IReadOnlyList<string> paths, Lean? expected)
{
	if (ReadTelemetry(paths) is not { } read) return 1;

	bool tiltKnown = read.Summary.CameraFormat?.Gravity(read.Summary.TelemetryFrames![0]) is not null;
	var telemetry = TelemetryReport.Create(read.Frames, tiltKnown);
	Console.WriteLine($"Camera: {read.Summary.CameraFormat?.Id ?? "unknown"}{(read.Summary.CameraModel is { } model ? $" ({model})" : "")}");
	Console.WriteLine(telemetry.ToText());
	if (expected is not { } lean) return 0;

	bool ok = telemetry.CheckLean(lean);
	Console.WriteLine(ok
		? $"PASS: the tilt reads as leaning {lean}"
		: $"FAIL: the tilt doesn't read as leaning {lean} (a sign may be wrong, the camera may not have been held that way, or its axes aren't known)");
	return ok ? 0 : 1;
}

static int ListPresets()
{
	(List<OverlayPreset> layouts, string activeLayout) = OverlayPresetStore.Load();
	Console.WriteLine("Overlay layouts (--layout):");
	foreach (OverlayPreset preset in layouts)
		Console.WriteLine($"  {preset.Name}  [{preset.Id}]{(preset.Id == activeLayout ? "  (active)" : "")}");

	(List<SettingsPreset> presets, string activePreset) = SettingsPresetStore.Load();
	Console.WriteLine("Settings presets (--settings-preset):");
	foreach (SettingsPreset preset in presets)
		Console.WriteLine($"  {preset.Name}  [{preset.Id}]{(preset.Id == activePreset ? "  (active)" : "")}");

	Console.WriteLine("Export profiles (--profile):");
	foreach (ExportProfile profile in ExportProfile.All)
		Console.WriteLine($"  {profile.Name}  [{profile.Id}]");

	Console.WriteLine("Transparent overlay (--alpha): prores (.mov, ProRes 4444), vp9 (.webm)");
	return 0;
}

static int EncoderTest()
{
	IReadOnlyList<string> gpus = FfmpegPipeline.GpuNames;
	Console.WriteLine(gpus.Count == 0 ? "Graphics cards: none found" : "Graphics cards: " + string.Join("; ", gpus));

	VideoInfo[] outputs =
	[
		new("hevc", "Main 10", 3840, 2160, "30", "yuv420p10le", null, null, null, null, 100_000_000),
		new("h264", "High", 1920, 1080, "30", "yuv420p", null, null, null, null, 30_000_000)
	];
	bool anyGpu = false;
	foreach (VideoInfo output in outputs)
	{
		Console.WriteLine($"{output.CodecName} {output.Width}x{output.Height} {output.PixFmt}:");
		foreach (EncoderProbe probe in FfmpegPipeline.ProbeEncoders(output))
		{
			Console.WriteLine($"  {probe.Encoder,-12} {(probe.Works ? "works" : "FAILED")}{(probe.Detail.Length > 0 ? "  " + probe.Detail : "")}");
			anyGpu |= probe.Works && FfmpegPipeline.IsGpuEncoder(probe.Encoder);
		}
	}

	Console.WriteLine(anyGpu ? "A graphics card encodes here - a render uses it." : "No graphics card encoder works here - a render uses the CPU's.");
	return 0;
}

// The same install path the GUI's dependency prompt takes, so it only installs what's missing and keeps FFmpeg on the
// major the preview supports.
static async Task<int> InstallDependenciesAsync(string[] names)
{
	List<ExternalTool> tools = names.Length == 0
		? [RequiredTools.Ffmpeg]
		: [.. RequiredTools.All.Where(t => names.Contains(t.DisplayName, StringComparer.OrdinalIgnoreCase))];
	if (tools.Count != Math.Max(1, names.Length))
	{
		Console.Error.WriteLine($"Error: unknown tool - expected {string.Join(" or ", RequiredTools.All.Select(t => t.DisplayName.ToLowerInvariant()))}");
		return 1;
	}

	IReadOnlyList<ExternalTool> missing = DependencyChecker.FindMissing(tools);
	if (missing.Count == 0)
	{
		Console.WriteLine("All dependencies are already installed");
		return 0;
	}

	bool failed = false;
	foreach (ExternalTool tool in missing)
	{
		Console.WriteLine($"Installing {tool.DisplayName}...");
		InstallResult result = await DependencyInstaller.InstallAsync(tool, Console.WriteLine, CancellationToken.None);
		Console.WriteLine(result.Message);
		failed |= !result.Success;
	}

	return failed ? 1 : 0;
}
