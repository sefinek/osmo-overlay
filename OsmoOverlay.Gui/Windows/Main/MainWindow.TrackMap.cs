using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
	private TrackMapSettings? _pendingTrackMap;
	private bool _loadingTrackMap;

	private void WireTrackMap()
	{
		TrackMapZoomBox.Minimum = RouteMapMosaic.MinZoom;
		TrackMapZoomBox.Maximum = RouteMapMosaic.MaxZoom;
		TrackMapMarkerBox.Minimum = (decimal)TrackMapSettings.MinMarkerScale;
		TrackMapMarkerBox.Maximum = (decimal)TrackMapSettings.MaxMarkerScale;
		TrackMapTrail.Changed += style => ChangeTrackMap(map => map with
		{
			ShowTrail = style.Visible, TrailColor = style.Color, ColorBySpeed = style.BySpeed, TrailWidth = style.Width
		});
		TrackMapSource.SelectionChanged += providerId => ChangeTrackMap(map => map with { ProviderId = providerId });
		TrackMapSource.LabelsChanged += show => ChangeTrackMap(map => map with { ShowLabels = show });
		TrackMapSource.SharedChanged += () => _trackMapDelay.Start();
		_trackMapDelay.Tick += async (_, _) =>
		{
			_trackMapDelay.Stop();
			if (_pendingTrackMap is { } pending)
			{
				_pendingTrackMap = null;
				OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { TrackMap = pending });
			}

			await RebuildTrackBackdropAsync();
		};
	}

	private void ShowTrackMapSettings()
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		var map = TrackMapSettings.From(settings);
		_loadingTrackMap = true;
		TrackMapSource.Load(map.ProviderId, settings, map.ShowLabels, perMapLabels: true);
		TrackMapFollowCheck.IsChecked = map.Follow;
		TrackMapZoomBox.Value = map.MaxZoom;
		TrackMapTrail.Populate(map.ShowTrail, map.TrailColor, map.ColorBySpeed, map.TrailWidth);
		TrackMapAheadCheck.IsChecked = map.ShowAhead;
		TrackMapMarkerBox.Value = (decimal)map.MarkerScale;
		_loadingTrackMap = false;
	}

	/// <summary>Kept until the changes stop (a spin of the zoom box is several), then saved and the map fetched again.</summary>
	private void ChangeTrackMap(Func<TrackMapSettings, TrackMapSettings> change)
	{
		if (_loadingTrackMap) return;

		_pendingTrackMap = change(_pendingTrackMap ?? TrackMapSettings.From(OverlaySettingsStore.Load())).Sanitized();
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

	private void OnTrackMapAheadChanged(object? sender, RoutedEventArgs e)
	{
		bool show = TrackMapAheadCheck.IsChecked == true;
		ChangeTrackMap(map => map with { ShowAhead = show });
	}

	private void OnTrackMapMarkerChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (TrackMapMarkerBox.Value is not { } scale) return;

		ChangeTrackMap(map => map with { MarkerScale = (float)scale });
	}

	/// <summary>A new map for the open track, shown where the preview was; one still being fetched is dropped for it.</summary>
	private async Task RebuildTrackBackdropAsync()
	{
		if (!_trackOnly || _fullTelemetry is not { } playback || _summary is not { } summary) return;

		_trackMapBuild?.Cancel();
		CancellationTokenSource build = _trackMapBuild = new CancellationTokenSource();
		OverlaySettings settings = OverlaySettingsStore.Load();
		try
		{
			TrackBackdrop? backdrop = await Task.Run(() => TrackBackdrop.BuildAsync(playback.DerivedFrames, settings, _trackOnlyFormat, build.Token), build.Token);
			if (build != _trackMapBuild || _fullTelemetry != playback)
			{
				backdrop?.Dispose();
				return;
			}

			if (backdrop is null) AppendLog(Strings.TrackMap_Unavailable, LogLevel.Warn);
			bool playing = _previewPlayer.IsPlaying;
			TimeSpan position = _fullTelemetryPosition;
			ExternalTrackPlayback updated = _fullTelemetry = playback with { Backdrop = backdrop };
			RefreshWidgetList();
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
			if (build == _trackMapBuild) _trackMapBuild = null;

			build.Dispose();
		}
	}
}
