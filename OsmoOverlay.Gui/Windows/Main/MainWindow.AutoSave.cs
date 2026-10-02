using System.Security.Cryptography;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

/// <summary>
///     The auto-save backup (OverlaySettings.AutoSaveMinutes): a copy of the open project in the app's data folder every few
///     minutes. Never the project's own file - only Save writes that - so a backup can't overwrite work the user meant to
///     discard. Skipped while nothing is loaded or a render runs.
/// </summary>
public partial class MainWindow
{
	private readonly DispatcherTimer _autoSaveTimer = new();
	private string? _lastBackupHash;

	private static string AutoSaveFolder => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "autosave");

	private void WireAutoSave()
	{
		_autoSaveTimer.Tick += (_, _) => AutoSave();
	}

	private void ApplyAutoSave(int minutes)
	{
		_autoSaveTimer.Stop();
		if (minutes <= 0) return;

		_autoSaveTimer.Interval = TimeSpan.FromMinutes(minutes);
		_autoSaveTimer.Start();
	}

	private void AutoSave()
	{
		if (_phase != UiPhase.SummaryReady || _summary is null) return;

		string name = _projectPath is not null ? Path.GetFileNameWithoutExtension(_projectPath) : "untitled";
		string path = Path.Combine(AutoSaveFolder, $"{name}.autosave{OverlayProject.Extension}");
		try
		{
			Directory.CreateDirectory(AutoSaveFolder);
			BuildProject().Save(path);
			// Written every time (cheap), but only worth a log line when the project changed since the last backup.
			string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
			if (hash == _lastBackupHash) return;

			_lastBackupHash = hash;
			AppendLog(string.Format(Strings.AutoSave_Saved, path));
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, string.Format(Strings.AutoSave_Failed, ex.Message));
		}
	}
}
