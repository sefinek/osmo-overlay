using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Gui;

public partial class ExternalTrackWindow : Window
{
	private readonly FileSummary? _summary;
	private ExternalTrack? _track;
	private bool _busy;
	private bool _closed;

	public bool Accepted { get; private set; }
	public ExternalTrackSource? Source { get; private set; }

	public ExternalTrackWindow() : this(null, null, 0) { }

	public ExternalTrackWindow(FileSummary? summary, ExternalTrackSource? source, double currentSeconds)
	{
		InitializeComponent();
		_summary = summary;
		Source = source;
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
		Opened += async (_, _) => { if (source is not null) await LoadTrackAsync(source.Path); };
		Closed += (_, _) => _closed = true;
	}

	private async void OnBrowseClick(object? sender, RoutedEventArgs e)
	{
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = Strings.Track_Title,
			FileTypeFilter = [new FilePickerFileType(Strings.Track_FileType) { Patterns = ["*.fit", "*.FIT", "*.gpx", "*.GPX"] }]
		});
		if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
		await LoadTrackAsync(path);
	}

	private async Task LoadTrackAsync(string path)
	{
		SetBusy(true);
		ProblemText.Text = "";
		try
		{
			ExternalTrack track = await Task.Run(() => ExternalTrackReader.Read(path));
			if (_closed) return;
			_track = track;
			PathBox.Text = path;
			TrackInfo.Text = string.Format(Strings.Track_Info, track.StartUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), track.EndUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture), track.Points.Count, track.Pauses.Count);
			FirstAnchor.SetTrackPoints(track.Points);
			SecondAnchor.SetTrackPoints(track.Points);
			if (Source?.Path != path)
			{
				UseSecond.IsChecked = false;
				FirstAnchor.Utc = track.StartUtc;
				SecondAnchor.Utc = track.EndUtc;
				EstimateInfo.Text = "";
			}
			else if (Source.Synchronization is null && Source.OffsetSeconds is { } offset && _summary?.ContainerRecordingStartUtc is { } start)
				FirstAnchor.Utc = start.AddSeconds(offset + FirstAnchor.VideoSeconds);
		}
		catch (Exception ex)
		{
			ProblemText.Text = ex.Message;
		}
		finally { if (!_closed) SetBusy(false); }
	}

	private async void OnEstimateClick(object? sender, RoutedEventArgs e)
	{
		if (_summary?.TelemetryFrames is not { } frames || _track is not { } track) return;
		SetBusy(true);
		try
		{
			TrackSyncEstimate? estimate = await Task.Run(() => ExternalTrackSync.Estimate(frames, _summary.Video.Fps, track));
			if (_closed) return;
			if (estimate is null) { ProblemText.Text = Strings.Track_NoEstimate; return; }
			FirstAnchor.VideoSeconds = 0;
			FirstAnchor.Utc = estimate.TrackTimeAtVideoStartUtc;
			UseSecond.IsChecked = false;
			ProblemText.Text = "";
			EstimateInfo.Text = string.Format(Strings.Track_EstimateInfo, estimate.Correlation);
		}
		catch (Exception ex) { if (!_closed) ProblemText.Text = ex.Message; }
		finally { if (!_closed) SetBusy(false); }
	}

	private void OnApplyClick(object? sender, RoutedEventArgs e)
	{
		if (_busy || _track is null) return;
		try
		{
			TrackSyncAnchor first = FirstAnchor.Read();
			TrackSyncAnchor? second = UseSecond.IsChecked == true ? SecondAnchor.Read() : null;
			var sync = new TrackSynchronization(first, second);
			sync.Validate();
			double end = FirstAnchor.MaxVideoSeconds;
			if (sync.At(0) > _track.EndUtc || sync.At(end) < _track.StartUtc) throw new InvalidDataException(Strings.Track_NoOverlap);
			Source = new ExternalTrackSource(PathBox.Text!, Synchronization: sync);
			Accepted = true;
			Close();
		}
		catch (Exception ex) { ProblemText.Text = ex.Message; }
	}

	private void SetBusy(bool busy)
	{
		_busy = busy;
		Editor.IsEnabled = !busy;
		ApplyButton.IsEnabled = !busy && _track is not null;
		RemoveButton.IsEnabled = !busy;
		EstimateButton.IsEnabled = !busy && _track is not null && _summary?.TelemetryFrames is { Count: > 0 };
	}

	private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
	private void OnRemoveClick(object? sender, RoutedEventArgs e) { Source = null; Accepted = true; Close(); }
}
