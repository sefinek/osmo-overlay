using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Gui;

/// <summary>
///     Puts an external track on the recording in three steps: the file, the alignment (the app's own proposal, or the user's anchors) and
///     a summary to confirm. Nothing is applied before the last step - a proposal is only ever offered.
/// </summary>
public partial class ExternalTrackWindow : Window
{
	private enum Step
	{
		File,
		Sync,
		Summary
	}

	private static readonly string[] StepTitles = [Strings.Track_StepFile, Strings.Track_StepSync, Strings.Track_StepSummary];

	private readonly FileSummary? _summary;
	private readonly Control[] _pages;
	private readonly Border[] _dots;
	private ExternalTrack? _track;
	private TrackAlignment? _proposal;
	private TrackSynchronization? _sync;
	private Step _step;
	private bool _proposalReady;
	private bool _modeChosen;
	private bool _busy;
	private bool _closed;

	public bool Accepted { get; private set; }
	public ExternalTrackSource? Source { get; private set; }
	internal ExternalTrack? LoadedTrack => _track;

	/// <summary>What the window does (reading, matching, a failure) - the main window puts it in its log, which stays visible behind the dialog.</summary>
	public event Action<string, LogLevel>? Log;

	public ExternalTrackWindow() : this(null, null, 0)
	{
	}

	public ExternalTrackWindow(FileSummary? summary, ExternalTrackSource? source, double currentSeconds)
	{
		InitializeComponent();
		_summary = summary;
		Source = source;
		_pages = [FileStep, SyncStep, SummaryStep];
		_dots = [.. StepTitles.Select(_ => StepDot())];
		RemoveButton.IsVisible = source is not null;
		double last = summary is { TotalFrameCount: > 0 } ? (summary.TotalFrameCount - 1) / summary.Video.Fps : 0;
		double fps = summary?.Video.Fps ?? 0;
		FirstAnchor.SetVideoRange(last, fps, currentSeconds);
		SecondAnchor.SetVideoRange(last, fps, currentSeconds);
		FirstAnchor.VideoSeconds = currentSeconds;
		SecondAnchor.VideoSeconds = last;
		if (source?.Synchronization is { } sync)
		{
			FirstAnchor.VideoSeconds = sync.First.VideoSeconds;
			FirstAnchor.Utc = sync.First.TrackUtc;
			SecondAnchor.Utc = sync.At(last);
			if (sync.Second is { } second)
			{
				UseSecond.IsChecked = true;
				SecondAnchor.VideoSeconds = second.VideoSeconds;
				SecondAnchor.Utc = second.TrackUtc;
			}
		}

		PhoneButton.IsVisible = summary?.CameraFormat?.IsDrone == true;
		ShowStep(Step.File);
		Opened += async (_, _) =>
		{
			if (source is not null) await OpenCurrentAsync(source);
			else await UseSavedLogAsync();
		};
		Closed += (_, _) => _closed = true;
	}

	/// <summary>
	///     A drone's flight log kept from an earlier search (or beside the recording) that covers the recording is loaded at once - the user only
	///     picks one by hand when there is none, or another is wanted. A track picked meanwhile is left alone.
	/// </summary>
	private async Task UseSavedLogAsync()
	{
		if (_summary is not { CameraFormat.IsDrone: true } summary) return;

		ExternalTrackSource? found = await Task.Run(() => ExternalTrackSource.FindFor(summary.CameraFormat, summary.InputPaths[0], summary.ContainerRecordingStartUtc));
		if (_closed || found is null || _track is not null || _busy) return;

		Log?.Invoke(string.Format(Strings.Track_FoundSaved, Path.GetFileName(found.Path)), LogLevel.Info);
		await LoadTrackAsync(found.Path);
	}

	/// <summary>An attached track: its file is read again and the window opens on the summary of how it is placed now.</summary>
	private async Task OpenCurrentAsync(ExternalTrackSource source)
	{
		if (!await LoadTrackAsync(source.Path)) return;

		await EnsureProposalAsync();
		if (_closed) return;

		_sync = source.Synchronization ?? (source.OffsetSeconds is { } offset && _summary?.ContainerRecordingStartUtc is { } start
			? new TrackSynchronization(new TrackSyncAnchor(0, start.AddSeconds(offset)))
			: null);
		if (_sync is null) return;

		FirstAnchor.VideoSeconds = _sync.First.VideoSeconds;
		FirstAnchor.Utc = _sync.First.TrackUtc;
		bool automatic = MatchesProposal(_sync);
		AutoMode.IsChecked = automatic && _proposal is not null;
		ManualMode.IsChecked = !AutoMode.IsChecked;
		_modeChosen = true;
		ShowSummary(_sync);
	}

	/// <summary>A step indicator: a pill for the current step, a dot for the others.</summary>
	private Border StepDot()
	{
		var dot = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
		StepDots.Children.Add(dot);
		return dot;
	}

	private void ShowStep(Step step)
	{
		_step = step;
		for (int i = 0; i < _pages.Length; i++)
		{
			bool current = i == (int)step;
			_pages[i].IsVisible = current;
			_dots[i].Width = current ? 18 : 6;
			_dots[i].Background = current ? Palette.Accent : Palette.StrokeStrong;
		}

		StepLabel.Text = string.Format(Strings.Welcome_StepOf, (int)step + 1, _pages.Length);
		StepTitle.Text = StepTitles[(int)step];
		BackButton.IsVisible = step != Step.File;
		NextButton.Content = step == Step.Summary ? Strings.Track_Apply : Strings.Welcome_Next;
		Show(ProblemText, "");
		UpdateButtons();
	}

	private async void OnPhoneClick(object? sender, RoutedEventArgs e)
	{
		var window = new PhoneLogWindow(_summary);
		window.Log += (message, level) => Log?.Invoke(message, level);
		await window.ShowDialog(this);
		if (!_closed && window.FoundPath is { } path) await LoadTrackAsync(path);
	}

	private async void OnBrowseClick(object? sender, RoutedEventArgs e)
	{
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenRememberedAsync("gps-track", new FilePickerOpenOptions
		{
			Title = Strings.Track_Title,
			FileTypeFilter = [new FilePickerFileType(Strings.Track_FileType) { Patterns = [.. ExternalTrackReader.Extensions.SelectMany(e => new[] { "*" + e, "*" + e.ToUpperInvariant() })] }]
		});
		if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
		await LoadTrackAsync(path);
	}

	private async Task<bool> LoadTrackAsync(string path)
	{
		SetBusy(true);
		Show(ProblemText, "");
		string name = Path.GetFileName(path);
		Show(LoadingText, Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? Strings.Track_ReadingDjiLog : Strings.Track_Reading);
		Log?.Invoke(string.Format(Strings.Track_ReadingFile, name), LogLevel.Info);
		try
		{
			// Caught inside the task: a file that isn't a usable track is expected, and an exception crossing Task.Run stops the debugger as user-unhandled.
			(ExternalTrack? read, string? problem) = await Task.Run<(ExternalTrack?, string?)>(() =>
			{
				try
				{
					return (ExternalTrackReader.Read(path), null);
				}
				catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
				{
					return (null, ex.Message);
				}
			});
			if (_closed) return false;
			if (read is not { } track)
			{
				Show(ProblemText, problem ?? "");
				Log?.Invoke(string.Format(Strings.Track_ReadFailed, name, problem), LogLevel.Warn);
				return false;
			}

			bool attached = Source?.Path == path;
			_track = track;
			_proposal = null;
			_proposalReady = false;
			_modeChosen = false;
			_sync = null;
			PathBox.Text = path;
			TrackInfo.Text = string.Format(Strings.Track_Info, track.StartUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), track.EndUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture), track.Points.Count, track.Pauses.Count);
			string stats = DescribeStats(track);
			TrackStats.Text = stats;
			TrackStats.IsVisible = stats.Length > 0;
			Log?.Invoke(string.Format(Strings.Track_Loaded, name, track.Points.Count, track.StartUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), track.EndUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)), LogLevel.Info);
			FirstAnchor.SetTrackPoints(track.Points);
			SecondAnchor.SetTrackPoints(track.Points);
			if (!attached)
			{
				UseSecond.IsChecked = false;
				FirstAnchor.Utc = track.StartUtc;
				SecondAnchor.Utc = track.EndUtc;
			}

			return true;
		}
		catch (Exception ex)
		{
			Show(ProblemText, ex.Message);
			Log?.Invoke(string.Format(Strings.Track_ReadFailed, name, ex.Message), LogLevel.Warn);
			return false;
		}
		finally
		{
			Show(LoadingText, "");
			if (!_closed) SetBusy(false);
		}
	}

	/// <summary>Finds where the recording lies on the track by itself, once per track - the manual anchors start from that place.</summary>
	private async Task EnsureProposalAsync()
	{
		if (_proposalReady || _track is not { } track || _summary is not { TelemetryFrames: { } frames } summary) return;

		SetBusy(true);
		Show(LoadingText, Strings.Track_Aligning);
		try
		{
			_proposal = await Task.Run(() => ExternalTrackSource.Propose(frames, summary.Video.Fps, summary.ContainerRecordingStartUtc, track));
			if (_closed) return;

			_proposalReady = true;
			if (_proposal is { } found)
			{
				if (Source?.Path != PathBox.Text)
				{
					FirstAnchor.VideoSeconds = 0;
					FirstAnchor.Utc = found.VideoStartUtc;
				}

				Log?.Invoke(ProposalText(found), found.NeedsVerification ? LogLevel.Warn : LogLevel.Info);
			}
			else
			{
				Log?.Invoke(Strings.Track_Auto_None, LogLevel.Warn);
			}
		}
		catch (Exception ex)
		{
			Show(ProblemText, ex.Message);
			Log?.Invoke(ex.Message, LogLevel.Warn);
		}
		finally
		{
			Show(LoadingText, "");
			if (!_closed) SetBusy(false);
		}
	}

	private static string ProposalText(TrackAlignment alignment)
	{
		return alignment.Method switch
		{
			TrackAlignmentMethod.RecordingSpan => Strings.Track_Auto_RecordingSpan,
			TrackAlignmentMethod.ContainerClock => Strings.Track_Auto_ContainerClock,
			_ => string.Format(Strings.Track_Auto_Motion, alignment.Correlation)
		};
	}

	private static string MethodText(TrackAlignmentMethod method)
	{
		return method switch
		{
			TrackAlignmentMethod.RecordingSpan => Strings.Track_Method_RecordingSpan,
			TrackAlignmentMethod.ContainerClock => Strings.Track_Method_ContainerClock,
			_ => Strings.Track_Method_Motion
		};
	}

	private bool MatchesProposal(TrackSynchronization sync)
	{
		return _proposal is { } found && sync.Second is null && sync.First.VideoSeconds == 0 && Math.Abs((sync.First.TrackUtc - found.VideoStartUtc).TotalSeconds) < 0.001;
	}

	private async void OnNextClick(object? sender, RoutedEventArgs e)
	{
		if (_busy) return;

		switch (_step)
		{
			case Step.File when _track is not null:
				await EnsureProposalAsync();
				if (!_closed) ShowSync();
				break;
			case Step.Sync:
				if (TryBuildSync() is { } sync) ShowSummary(sync);
				break;
			case Step.Summary:
				Accept();
				break;
		}
	}

	private void OnBackClick(object? sender, RoutedEventArgs e)
	{
		if (_busy || _step == Step.File) return;
		if (_step == Step.Summary) ShowSync();
		else ShowStep(Step.File);
	}

	private void ShowSync()
	{
		ShowStep(Step.Sync);
		AutoMode.IsEnabled = _proposal is not null;
		if (!_modeChosen)
		{
			AutoMode.IsChecked = _proposal is not null;
			ManualMode.IsChecked = _proposal is null;
			_modeChosen = true;
		}

		if (_proposal is { } found)
		{
			VerdictText.Text = ProposalText(found);
			Show(VerdictHint, string.Format(Strings.Track_Auto_Start, found.VideoStartUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
		}
		else
		{
			VerdictText.Text = Strings.Track_Auto_None;
			Show(VerdictHint, Strings.Track_Auto_NoneHint);
		}
	}

	/// <summary>The synchronization the second step describes, or null with the reason shown when it can't be used.</summary>
	private TrackSynchronization? TryBuildSync()
	{
		if (_track is not { } track) return null;
		try
		{
			TrackSynchronization sync;
			if (AutoMode.IsChecked == true && _proposal is { } found)
			{
				sync = new TrackSynchronization(new TrackSyncAnchor(0, found.VideoStartUtc));
			}
			else
			{
				TrackSyncAnchor first = FirstAnchor.Read();
				TrackSyncAnchor? second = UseSecond.IsChecked == true ? SecondAnchor.Read() : null;
				sync = new TrackSynchronization(first, second);
				sync.Validate();
			}

			if (ExternalTrackSource.Coverage(track, sync, FirstAnchor.MaxVideoSeconds) <= 0) throw new InvalidDataException(Strings.Track_NoOverlap);
			return sync;
		}
		catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
		{
			Show(ProblemText, ex.Message);
			return null;
		}
	}

	private static string DescribeStats(ExternalTrack track)
	{
		List<string> parts = [];
		if (track.DeviceName is { } device) parts.Add(string.Format(Strings.Track_StatDevice, device));

		double meters = 0;
		for (int i = 1; i < track.Points.Count; i++)
		{
			ExternalTrackPoint a = track.Points[i - 1], b = track.Points[i];
			if (!b.StartsAfterGap) meters += TelemetryMath.HaversineMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
		}

		parts.Add(string.Format(Strings.Track_StatDistance, (meters / 1000).ToString("0.00", CultureInfo.CurrentCulture)));
		if (track.Points.Where(p => p.SpeedMs is not null).Select(p => p.SpeedMs!.Value).DefaultIfEmpty(-1).Max() is >= 0 and var speed)
			parts.Add(string.Format(Strings.Track_StatMaxSpeed, (speed * 3.6).ToString("0", CultureInfo.CurrentCulture)));
		if (track.Points.Where(p => p.HeightMeters is not null).Select(p => p.HeightMeters!.Value).DefaultIfEmpty(double.NaN).Max() is var height && !double.IsNaN(height))
			parts.Add(string.Format(Strings.Track_StatMaxHeight, height.ToString("0", CultureInfo.CurrentCulture)));
		if (track.Recordings.Count > 0) parts.Add(string.Format(Strings.Track_StatRecordings, track.Recordings.Count));

		return string.Join(" · ", parts);
	}

	private void ShowSummary(TrackSynchronization sync)
	{
		_sync = sync;
		ShowStep(Step.Summary);
		double coverage = _track is { } track ? ExternalTrackSource.Coverage(track, sync, FirstAnchor.MaxVideoSeconds) : 0;
		bool exact = MatchesProposal(sync) && _proposal is { NeedsVerification: false };
		SumTrack.Text = Path.GetFileName(PathBox.Text);
		SumRange.Text = TrackInfo.Text;
		SumMethod.Text = sync.Second is not null ? Strings.Track_Method_ManualDrift : MatchesProposal(sync) ? MethodText(_proposal!.Method) : Strings.Track_Method_Manual;
		SumCoverage.Text = string.Format(Strings.Track_Coverage, (int)Math.Round(coverage * 100));

		List<string> hints = [];
		if (coverage < 0.995) hints.Add(Strings.Track_CoverageHint);
		if (!exact) hints.Add(Strings.Track_VerifyHint);
		Show(SummaryHint, string.Join(" ", hints));
	}

	private void Accept()
	{
		if (_sync is null || PathBox.Text is not { Length: > 0 } path) return;

		Source = new ExternalTrackSource(path, Synchronization: _sync);
		Accepted = true;
		Close();
	}

	/// <summary>A message line is there only while it has something to say - an empty one would leave a gap in the layout.</summary>
	private static void Show(TextBlock line, string text)
	{
		line.Text = text;
		line.IsVisible = !string.IsNullOrEmpty(text);
	}

	private void SetBusy(bool busy)
	{
		_busy = busy;
		Busy.IsVisible = busy;
		TrackDetails.IsVisible = _track is not null;
		Pages.IsEnabled = !busy;
		RemoveButton.IsEnabled = !busy;
		UpdateButtons();
	}

	private void UpdateButtons()
	{
		NextButton.IsEnabled = !_busy && (_step != Step.File || _track is not null);
		BackButton.IsEnabled = !_busy;
	}

	private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

	private void OnRemoveClick(object? sender, RoutedEventArgs e)
	{
		Source = null;
		Accepted = true;
		Close();
	}
}
