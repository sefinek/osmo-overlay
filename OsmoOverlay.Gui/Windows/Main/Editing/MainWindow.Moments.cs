using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     Key moments: named flags the user puts on the recording (B at the frame on screen, the panel's buttons, or a peak of
///     the telemetry saved as one). They're frames of the combined recording, drawn on the PreviewTimeline, reached with
///     Up/Down (JumpToMarker) and saved in the project - they never change the render, so they stay out of the undo history.
/// </summary>
public partial class MainWindow
{
	private List<KeyMoment> _moments = [];

	private void WireMoments()
	{
		MomentsEditor.AddRequested += AddMomentAtCurrentFrame;
		MomentsEditor.MomentAdded += AddMoment;
		MomentsEditor.SeekRequested += frame => SeekToFrame(frame, true);
		MomentsEditor.MomentsEdited += moments =>
		{
			// A rename keeps the rows (rebuilding them mid-click would drop the button being pressed); a removal rebuilds.
			bool rebuild = moments.Count != _moments.Count;
			_moments = moments;
			RefreshMoments(rebuild);
		};
	}

	/// <summary>The flag button swaps the left column to the key moments panel, the same way the cut editor opens.</summary>
	private void OnToggleMomentsPanelClick(object? sender, RoutedEventArgs e)
	{
		if (MomentsColumnScroll.IsVisible)
		{
			CloseMomentsColumn();
			return;
		}

		CloseElementSettings();
		CutsColumnScroll.IsVisible = false;
		UpdateCutsButton();
		SourceColumnScroll.IsVisible = false;
		MomentsColumnScroll.IsVisible = true;
		MomentsColumnScroll.Offset = default;
		UpdateMomentsButton();
	}

	private void OnMomentsBackClick(object? sender, RoutedEventArgs e)
	{
		CloseMomentsColumn();
	}

	private void OnClearMomentsClick(object? sender, RoutedEventArgs e)
	{
		ClearMoments();
	}

	private void CloseMomentsColumn()
	{
		if (!MomentsColumnScroll.IsVisible) return;

		MomentsColumnScroll.IsVisible = false;
		SourceColumnScroll.IsVisible = !CutsColumnScroll.IsVisible && !WidgetSettingsColumnScroll.IsVisible;
		UpdateMomentsButton();
	}

	/// <summary>Lit while the panel is open or while any moment is set, like the cuts button.</summary>
	private void UpdateMomentsButton()
	{
		ToggleMomentsButton.Classes.Set("active", MomentsColumnScroll.IsVisible || _moments.Count > 0);
	}

	/// <summary>B: a moment at the frame on screen, named by its number until it's renamed in the panel.</summary>
	private void AddMomentAtCurrentFrame()
	{
		if (_summary is null) return;

		AddMoment(new KeyMoment(CurrentFrame(), $"Moment {_moments.Count + 1}"));
	}

	private void AddMoment(KeyMoment moment)
	{
		if (_summary is null) return;

		_moments = KeyMoments.Add(_moments, moment, SourceFrames);
		RefreshMoments();
	}

	private void ClearMoments()
	{
		_moments = [];
		RefreshMoments();
	}

	private void RefreshMoments(bool showInEditor = true)
	{
		if (showInEditor) MomentsEditor.Show(_moments);
		double fps = _summary?.Video.Fps ?? 0;
		IReadOnlyList<TimelineMoment> times = fps > 0 ? [.. _moments.Select(m => new TimelineMoment(m.Frame / fps, m.Name))] : [];
		foreach (PreviewTimeline timeline in Timelines) timeline.Moments = times;
		UpdateMomentsButton();
	}
}
