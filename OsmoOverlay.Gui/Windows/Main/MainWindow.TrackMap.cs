using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Gui;

/// <summary>The map behind a GPS track viewed with no recording (TrackBackdrop): its card in the left column and what a change in it does to the preview.</summary>
public partial class MainWindow
{
	private readonly DispatcherTimer _trackMapDelay = new() { Interval = TimeSpan.FromMilliseconds(500) };
	private CancellationTokenSource? _trackMapBuild;
	private bool _loadingTrackMap;

	private void WireTrackMap()
	{
		TrackMapZoomBox.Minimum = RouteMapMosaic.MinZoom;
		TrackMapZoomBox.Maximum = RouteMapMosaic.MaxZoom;
		TrackMapSource.SelectionChanged += providerId => ChangeTrackMap(map => map with { ProviderId = providerId });
		TrackMapSource.LabelsChanged += show => ChangeTrackMap(map => map with { ShowLabels = show });
		TrackMapSource.SharedChanged += () => _trackMapDelay.Start();
		_trackMapDelay.Tick += async (_, _) =>
		{
			_trackMapDelay.Stop();
			await RebuildTrackBackdropAsync();
		};
	}

	private void ShowTrackMapSettings()
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		TrackMapSettings map = TrackMapSettings.From(settings);
		_loadingTrackMap = true;
		TrackMapSource.Load(map.ProviderId, settings, map.ShowLabels, perMapLabels: true);
		TrackMapFollowCheck.IsChecked = map.Follow;
		TrackMapZoomBox.Value = map.MaxZoom;
		TrackMapTrailCheck.IsChecked = map.ShowTrail;
		_loadingTrackMap = false;
	}

	/// <summary>Saved at once, the map fetched again once the changes stop (a spin of the zoom box is several).</summary>
	private void ChangeTrackMap(Func<TrackMapSettings, TrackMapSettings> change)
	{
		if (_loadingTrackMap) return;

		OverlaySettings settings = OverlaySettingsStore.Load();
		OverlaySettingsStore.Save(settings with { TrackMap = change(TrackMapSettings.From(settings)).Sanitized() });
		_trackMapDelay.Stop();
		_trackMapDelay.Start();
	}

	private void OnTrackMapFollowChanged(object? sender, RoutedEventArgs e)
	{
		bool follow = TrackMapFollowCheck.IsChecked == true;
		ChangeTrackMap(map => map with { Follow = follow });
	}

	private void OnTrackMapZoomChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (TrackMapZoomBox.Value is not { } zoom) return;

		ChangeTrackMap(map => map with { MaxZoom = (int)zoom });
	}

	private void OnTrackMapTrailChanged(object? sender, RoutedEventArgs e)
	{
		bool show = TrackMapTrailCheck.IsChecked == true;
		ChangeTrackMap(map => map with { ShowTrail = show });
	}

	/// <summary>A new map for the open track, shown where the preview was; one still being fetched is dropped for it.</summary>
	private async Task RebuildTrackBackdropAsync()
	{
		if (!_trackOnly || _fullTelemetry is not { } playback || _summary is not { } summary) return;

		_trackMapBuild?.Cancel();
		var build = _trackMapBuild = new CancellationTokenSource();
		OverlaySettings settings = OverlaySettingsStore.Load();
		TrackMapStatus.Text = Strings.TrackMap_Loading;
		TrackMapStatus.IsVisible = true;
		try
		{
			TrackBackdrop? backdrop = await Task.Run(() => TrackBackdrop.BuildAsync(playback.DerivedFrames, settings, TrackOnlyFormat.Of(summary.Video), build.Token), build.Token);
			if (build != _trackMapBuild || _fullTelemetry != playback)
			{
				backdrop?.Dispose();
				return;
			}

			if (backdrop is null) AppendLog(Strings.TrackMap_Unavailable, LogLevel.Warn);
			bool playing = _previewPlayer.IsPlaying;
			TimeSpan position = _fullTelemetryPosition;
			ExternalTrackPlayback updated = _fullTelemetry = playback with { Backdrop = backdrop };
			try
			{
				await OpenPreviewPlayerAsync(summary, position, trackPlayback: updated);
			}
			finally
			{
				playback.Backdrop?.Dispose();
			}

			if (playing && _fullTelemetry == updated) _previewPlayer.Play(position);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, ex.Message);
			AppendLog(string.Format(Strings.TrackOnly_Failed, ex.Message), LogLevel.Error);
		}
		finally
		{
			if (build == _trackMapBuild) TrackMapStatus.IsVisible = false;
		}
	}
}
