namespace Telemavix.Core;

/// <summary>
///     The app's data lived in %LocalAppData%/OsmoOverlay before the program was renamed. Called first in every entry point,
///     before anything (the logger included) creates the new folder: once it exists the old one is left alone. A folder
///     that can't be moved (an older version still running from it) just means a fresh start in the new one.
/// </summary>
public static class LegacyDataFolder
{
	public static void Migrate()
	{
		string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string legacy = Path.Combine(root, "OsmoOverlay");
		string current = Path.Combine(root, "Telemavix");
		if (!Directory.Exists(legacy) || Directory.Exists(current)) return;

		try
		{
			Directory.Move(legacy, current);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"Could not move {legacy} to {current}: {ex.Message}");
		}
	}
}
