using Avalonia.Threading;
using OsmoOverlay.Core;

namespace OsmoOverlay.Gui;

/// <summary>
///     Whether the open project has changes the project file (or, with no file yet, nothing) doesn't have. The state is compared with
///     the one at the last save, open or load (OverlayProject.Fingerprint, which leaves the playhead out) every two seconds rather than
///     hooked into every place an edit is made - an edit made anywhere shows, including ones added later. Shown as a star in the
///     title; closing, a new project and opening another ask first.
/// </summary>
public partial class MainWindow
{
	private readonly DispatcherTimer _dirtyTimer = new() { Interval = TimeSpan.FromSeconds(2) };
	private string? _cleanFingerprint;

	private bool HasUnsavedChanges { get; set; }

	private void WireUnsaved()
	{
		_dirtyTimer.Tick += (_, _) => CheckUnsaved();
		_dirtyTimer.Start();
	}

	private void MarkClean()
	{
		_cleanFingerprint = CurrentFingerprint();
		SetUnsaved(false);
	}

	/// <summary>The same, for a project just written - from the project that was written, not the window as it is by now.</summary>
	private void MarkClean(OverlayProject saved)
	{
		_cleanFingerprint = saved.Fingerprint();
		CheckUnsaved();
	}

	private string? CurrentFingerprint()
	{
		return _phase == UiPhase.SummaryReady && _summary is not null ? BuildProject().Fingerprint() : null;
	}

	private void CheckUnsaved()
	{
		if (_phase != UiPhase.SummaryReady || _summary is null || _cleanFingerprint is null)
		{
			SetUnsaved(false);
			return;
		}

		SetUnsaved(CurrentFingerprint() != _cleanFingerprint);
	}

	private void SetUnsaved(bool unsaved)
	{
		if (HasUnsavedChanges == unsaved) return;

		HasUnsavedChanges = unsaved;
		UpdateTitle();
	}

	/// <summary>
	///     True to go on with what would discard the changes (a new project, another one opened, closing): there are none, or the
	///     user saved them or chose not to.
	/// </summary>
	private async Task<bool> ConfirmUnsavedAsync()
	{
		CheckUnsaved();
		if (!HasUnsavedChanges) return true;

		DialogChoice choice = await ConfirmDialog.AskOrSkipAsync(this, Strings.Unsaved_Title, Strings.Unsaved_Message, Strings.Unsaved_Save,
			Strings.Unsaved_Discard, DialogKind.Warning);
		if (choice == DialogChoice.Skip) return true;
		if (choice != DialogChoice.Confirm) return false;

		await SaveProjectCoreAsync();
		CheckUnsaved();
		return !HasUnsavedChanges;
	}
}
