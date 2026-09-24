using Avalonia.Interactivity;
using OsmoOverlay.Core;

namespace OsmoOverlay.Gui;

/// <summary>
///     What gets rendered: the whole recording minus the parts cut out - the only model the GUI has, so
///     trimming the start or the end is just a cut from the first or to the last frame. NLE style: I and O
///     mark the first and the last frame of a selection (both included) at the frame on screen, X/Delete cuts
///     the selection out, repeat for every part. The cuts are frames, edited here (the editor's buttons and the
///     preview's keys, MainWindow.Transport.cs, both through ApplyCutAction) or typed in the left column's
///     CutEditor, resolved the same way RenderJob does (RenderPlan.Resolve), drawn on the PreviewTimeline and
///     handed to the preview as an OutputTimeline - so the preview draws the overlay exactly as the render will,
///     dims the cut-out parts and skips them while playing.
/// </summary>
public partial class MainWindow
{
	private const string CutsEverythingMessage = "That would cut out the whole recording - nothing would be left to render.";

	// Normalized (CutList), except right after a typed edit, which keeps the rows' order (see CutEditor.Validate).
	private List<FrameRange> _cuts = [];
	// Null without a recording or while nothing is cut.
	private OutputTimeline? _outputTimeline;
	// The selection's first and last frame, both included; a missing side means the start/end of the recording.
	private long? _markIn;
	private long? _markOut;

	private bool HasCuts => _outputTimeline is not null;

	private long SourceFrames => _summary is null
		? 0
		: _summary.TotalFrameCount ?? (long)Math.Ceiling(_summary.DurationSeconds * _summary.Video.Fps);

	private FrameRange? Selection => _markIn is null && _markOut is null
		? null
		: new FrameRange(_markIn ?? 0, (_markOut ?? SourceFrames - 1) + 1);

	private void WireCuts()
	{
		CutsEditor.CutsEdited += cuts =>
		{
			_cuts = cuts;
			ApplyCuts(false);
		};
		CutsEditor.CutRequested += ApplyCutAction;
		CutsEditor.SeekRequested += SeekToFrame;
	}

	/// <summary>The scissors button swaps the left column to the cut editor, the same way a widget's settings open (OpenElementSettings).</summary>
	private void OnToggleCutsPanelClick(object? sender, RoutedEventArgs e)
	{
		if (CutsColumnScroll.IsVisible)
		{
			CloseCutsColumn();
			return;
		}

		CloseElementSettings();
		// Cutting is done on the timeline - the expanded one shows where each cut falls.
		if (!PreviewTimeline.Expanded) SetTimelineExpanded(true, false);
		SourceColumnScroll.IsVisible = false;
		CutsColumnScroll.IsVisible = true;
		CutsColumnScroll.Offset = default;
		UpdateCutsButton();
	}

	private void OnCutsBackClick(object? sender, RoutedEventArgs e)
	{
		CloseCutsColumn();
	}

	private void OnClearCutsClick(object? sender, RoutedEventArgs e)
	{
		ClearCuts();
	}

	private void CloseCutsColumn()
	{
		if (!CutsColumnScroll.IsVisible) return;

		CutsColumnScroll.IsVisible = false;
		SourceColumnScroll.IsVisible = true;
		UpdateCutsButton();
	}

	/// <summary>Lit while the editor is open or while anything is cut, so a closed editor can't hide an active cut.</summary>
	private void UpdateCutsButton()
	{
		ToggleCutsButton.Classes.Set("active", CutsColumnScroll.IsVisible || HasCuts);
	}

	/// <summary>The editor's buttons and the preview's I/O/X keys, at the frame on screen.</summary>
	private void ApplyCutAction(CutAction action)
	{
		if (_summary is null) return;

		var frame = CurrentFrame();
		switch (action)
		{
			case CutAction.MarkIn:
				_markIn = frame;
				if (_markOut < frame) _markOut = null;
				ShowSelection();
				break;
			case CutAction.MarkOut:
				_markOut = frame;
				if (_markIn > frame) _markIn = null;
				ShowSelection();
				break;
			case CutAction.ClearSelection:
				ClearSelection();
				break;
			case CutAction.CutSelection:
				CutSelection();
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(action), action, null);
		}
	}

	private void CutSelection()
	{
		if (Selection is not { } selection) return;

		List<FrameRange> cuts = CutList.Add(_cuts, selection, SourceFrames);
		if (CutList.RemovesEverything(cuts, SourceFrames))
		{
			CutsEditor.ShowError(CutsEverythingMessage);
			if (!CutsColumnScroll.IsVisible) AppendLog(CutsEverythingMessage, LogLevel.Warn);
			return;
		}

		_cuts = cuts;
		_markIn = null;
		_markOut = null;
		ApplyCuts();
	}

	private void ClearSelection()
	{
		_markIn = null;
		_markOut = null;
		ShowSelection();
	}

	private void ClearCuts()
	{
		_cuts = [];
		_markIn = null;
		_markOut = null;
		ApplyCuts();
	}

	/// <param name="showInEditor">False when the edit came from the editor itself - rewriting its fields would move the caret.</param>
	private void ApplyCuts(bool showInEditor = true)
	{
		OutputTimeline? previous = _outputTimeline;
		_outputTimeline = BuildOutputTimeline();
		// Rebuilds the preview's renderer - skipped when nothing was cut before or after.
		if (previous is not null || _outputTimeline is not null) _previewPlayer.SetOutputTimeline(_outputTimeline);

		RefreshCutViews(showInEditor);
		if (_summary is not null) PopulateOutputInfo(_summary, _detectedEncoder);
	}

	/// <summary>Everything showing the cuts, apart from the preview's renderer - also for a (re)opened preview, which keeps its OutputTimeline.</summary>
	private void RefreshCutViews(bool showInEditor = true)
	{
		if (showInEditor) CutsEditor.Show(_cuts);
		CutsEditor.ShowSummary(DescribeRender());
		ShowSelection();
		UpdateCutsButton();
		UpdateCutScrim(_previewPosition);
		PreviewTimeline.Cuts = _summary is null ? [] : CutList.ToTimeRanges(CutList.Normalize(_cuts, SourceFrames), _summary.Video.Fps);
	}

	private void ShowSelection()
	{
		FrameRange? selection = _summary is null ? null : Selection;
		PreviewTimeline.Selection = selection?.ToTimeRange(_summary!.Video.Fps);
		CutsEditor.ShowSelection(selection is { } s ? DescribeSelection(s) : null, selection is not null);
	}

	/// <summary>Every path that changes _cuts rejects cuts that remove everything first, so Resolve always has something left here.</summary>
	private OutputTimeline? BuildOutputTimeline()
	{
		if (_summary is null || _cuts.Count == 0) return null;

		var fps = _summary.Video.Fps;
		RenderPlan plan = RenderPlan.Resolve(null, null, CutList.ToTimeRanges(_cuts, fps), null, fps, SourceFrames);
		return plan.IsPartial ? new OutputTimeline(plan, fps) : null;
	}

	/// <summary>The cuts as RenderOptions takes them, or null for the whole recording.</summary>
	private List<TimeRange>? CutOutsForRender()
	{
		return HasCuts && _summary is not null
			? CutList.ToTimeRanges(CutList.Normalize(_cuts, SourceFrames), _summary.Video.Fps)
			: null;
	}

	/// <summary>Frames the render will produce - resolved exactly as RenderJob resolves them.</summary>
	private long PlannedFrameCount()
	{
		return _outputTimeline?.Plan.TotalFrames ?? SourceFrames;
	}

	/// <summary>"2 cuts, 01:05.300 removed" - for the Output card and the render log.</summary>
	private string DescribeCuts()
	{
		if (_summary is null) return "";

		var count = CutList.Normalize(_cuts, SourceFrames).Count;
		var removed = TimeText.Format(CutList.RemovedFrames(_cuts, SourceFrames) / _summary.Video.Fps);
		return $"{count} {(count == 1 ? "cut" : "cuts")}, {removed} removed";
	}

	/// <summary>The cut editor's summary line.</summary>
	private string DescribeRender()
	{
		if (_summary is null) return "";
		if (!HasCuts) return "Nothing is cut - the whole recording gets rendered.";

		var fps = _summary.Video.Fps;
		return $"Rendered: {TimeText.Format(PlannedFrameCount() / fps)} of {TimeText.Format(SourceFrames / fps)} ({DescribeCuts()})";
	}

	/// <summary>"04:41.181 - 08:33.363 (03:52.182)" - From/To the way the cut rows show them, then the length.</summary>
	private string DescribeSelection(FrameRange selection)
	{
		var fps = _summary!.Video.Fps;
		return $"{TimeText.Format(selection.Start / fps)} - {TimeText.Format(selection.End / fps)} ({TimeText.Format(selection.Length / fps)})";
	}

	/// <summary>Dims the preview while the frame on screen is one the render leaves out.</summary>
	private void UpdateCutScrim(TimeSpan position)
	{
		CutScrim.IsVisible = PreviewImage.Source is not null && _outputTimeline is { } timeline &&
		                     timeline.ToOutputSeconds(position.TotalSeconds) is null;
	}
}
