using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Dji.Drones;

/// <summary>A phone (or any portable device) Windows shows under This PC.</summary>
public sealed record PhoneDevice(string Name);

/// <summary>A step of a search: a place looked at (Logs is how many it holds, null when there is no such folder) or a file copied.</summary>
public sealed record PhoneProgress(string? Place, int? Logs, string? File);

/// <summary>A folder that was looked at and how many flight logs it holds (0 when it is empty or the phone has no such folder).</summary>
public sealed record PhoneLogPlace(string Path, int Logs);

/// <summary>What a search for the flight logs gave: how many were copied now, how many were already kept, where logs were and how many places were looked at.</summary>
public sealed record PhoneCopyResult(int Copied, int AlreadyKept, IReadOnlyList<PhoneLogPlace> Places, int PlacesChecked)
{
	public int Found => Copied + AlreadyKept;
}

/// <summary>
///     Brings the flight logs of the DJI Fly app from a phone to the folder the app looks in for a recording's log (DjiFlightLogFormat.FindFor).
///     On Windows a phone is read through the shell as the Explorer does (MTP has no drive letter) - the places the app is known to keep
///     its logs are looked at, nothing else is read and nothing on the phone is changed. Any other system - or a phone that is
///     mounted as a folder - goes through ImportFromFolder.
/// </summary>
public static class PhoneFlightLogs
{
	private const int ShellThisPc = 17;
	private const int CopyNoUi = 4 | 16 | 1024;
	private const int FolderSearchDepth = 6;

	private static readonly TimeSpan CopyTimeout = TimeSpan.FromSeconds(90);

	/// <summary>Where DJI Fly (Android) keeps the logs, from the storage's root.</summary>
	internal static readonly string[][] KnownFolders =
	[
		["Android", "data", "dji.go.v5", "files", "FlightRecord"],
		["Download", "product_data", "flightRecords"],
		["DJI", "dji.go.v5", "FlightRecord"],
		["Android", "data", "dji.go.v4", "files", "FlightRecord"]
	];

	public static string CacheFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "flight-logs");

	/// <summary>Reading a phone through the shell is a Windows way; elsewhere the user points at a folder.</summary>
	public static bool CanReadDevices => OperatingSystem.IsWindows();

	public static bool IsLogName(string name)
	{
		return name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && name.Contains("FlightRecord", StringComparison.OrdinalIgnoreCase);
	}

	public static Task<IReadOnlyList<PhoneDevice>> ListDevicesAsync()
	{
		return CanReadDevices ? RunSta<IReadOnlyList<PhoneDevice>>(ListDevices) : Task.FromResult<IReadOnlyList<PhoneDevice>>([]);
	}

	public static Task<PhoneCopyResult> CopyFromAsync(PhoneDevice device, IProgress<PhoneProgress>? progress = null, CancellationToken cancel = default)
	{
		return RunSta(() => CopyFrom(device, progress, cancel));
	}

	/// <summary>Copies every flight log found anywhere under `folder` (a phone mounted as a folder, a copy of its files).</summary>
	public static PhoneCopyResult ImportFromFolder(string folder, IProgress<PhoneProgress>? progress = null, CancellationToken cancel = default)
	{
		Directory.CreateDirectory(CacheFolder);
		int copied = 0, kept = 0;
		Dictionary<string, int> perFolder = new(StringComparer.OrdinalIgnoreCase);
		var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = FolderSearchDepth, IgnoreInaccessible = true };
		foreach (string file in Directory.EnumerateFiles(folder, "*.txt", options))
		{
			cancel.ThrowIfCancellationRequested();
			string name = Path.GetFileName(file);
			if (!IsLogName(name) || !FlightLog.LooksLikeFlightLog(Head(file))) continue;

			string folderName = Path.GetDirectoryName(file) ?? folder;
			perFolder[folderName] = perFolder.GetValueOrDefault(folderName) + 1;
			string target = Path.Combine(CacheFolder, name);
			if (IsKept(target))
			{
				kept++;
				continue;
			}

			File.Copy(file, target, true);
			copied++;
			progress?.Report(new PhoneProgress(null, null, name));
		}

		return new PhoneCopyResult(copied, kept, [.. perFolder.Select(pair => new PhoneLogPlace(pair.Key, pair.Value))], perFolder.Count);
	}

	/// <summary>A copy from an earlier search - only a whole one: a copy that was cut short is removed and made again.</summary>
	private static bool IsKept(string target)
	{
		if (!File.Exists(target)) return false;

		try
		{
			if (FlightLog.ReadHeader(target).StartedUtc is not null) return true;
			File.Delete(target);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			AppLogger.Warn(ex, $"{Path.GetFileName(target)} kept from an earlier search is not usable.");
		}

		return false;
	}

	private static byte[] Head(string path)
	{
		byte[] head = new byte[128];
		using FileStream stream = File.OpenRead(path);
		int read = stream.Read(head);
		return head[..read];
	}

	private static Task<T> RunSta<T>(Func<T> work)
	{
		var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			try
			{
				done.SetResult(work());
			}
			catch (Exception ex)
			{
				done.SetException(ex);
			}
		}) { IsBackground = true, Name = "Phone flight logs" };
		if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return done.Task;
	}

	private static dynamic Shell()
	{
		if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
		return Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application") ?? throw new PlatformNotSupportedException())!;
	}

	/// <summary>Portable devices have a path that starts with "::" - a drive or a network share has a letter or a host.</summary>
	private static IReadOnlyList<PhoneDevice> ListDevices()
	{
		dynamic thisPc = Shell().NameSpace(ShellThisPc);
		List<PhoneDevice> devices = [];
		foreach (dynamic item in thisPc.Items())
		{
			if (((string)item.Path).StartsWith("::", StringComparison.Ordinal) && item.IsFolder && item.GetFolder.Items().Count > 0) devices.Add(new PhoneDevice((string)item.Name));
		}

		return devices;
	}

	private static PhoneCopyResult CopyFrom(PhoneDevice device, IProgress<PhoneProgress>? progress, CancellationToken cancel)
	{
		dynamic shell = Shell();
		dynamic? found = null;
		foreach (dynamic item in shell.NameSpace(ShellThisPc).Items())
		{
			if ((string)item.Name == device.Name && ((string)item.Path).StartsWith("::", StringComparison.Ordinal)) found = item;
		}

		if (found is null) throw new IOException(device.Name);

		Directory.CreateDirectory(CacheFolder);
		dynamic destination = shell.NameSpace(CacheFolder);
		int copied = 0, kept = 0, checkedPlaces = 0;
		List<PhoneLogPlace> places = [];
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, object?> folders = new(StringComparer.OrdinalIgnoreCase);
		foreach (dynamic storage in found.GetFolder.Items())
		{
			foreach (string[] known in KnownFolders)
			{
				cancel.ThrowIfCancellationRequested();
				checkedPlaces++;
				string place = string.Join('/', known);
				dynamic? folder = Descend(storage, known, folders);
				if (folder is null)
				{
					places.Add(new PhoneLogPlace(place, 0));
					progress?.Report(new PhoneProgress(place, null, null));
					continue;
				}

				List<dynamic> logs = LogsIn(folder);
				progress?.Report(new PhoneProgress(place, logs.Count, null));
				places.Add(new PhoneLogPlace(place, logs.Count));
				foreach (dynamic file in logs)
				{
					cancel.ThrowIfCancellationRequested();
					string name = (string)file.Name;
					if (!seen.Add(name)) continue;

					string target = Path.Combine(CacheFolder, name);
					if (IsKept(target))
					{
						kept++;
						continue;
					}

					destination.CopyHere(file, CopyNoUi);
					if (!WaitForCopy(target, cancel)) continue;

					copied++;
					progress?.Report(new PhoneProgress(null, null, name));
				}
			}
		}

		return new PhoneCopyResult(copied, kept, places, checkedPlaces);
	}

	/// <summary>Walks down `path` from the storage's root; `folders` keeps every folder reached (null for a missing one), because over MTP each listing is slow and the known paths share their beginnings.</summary>
	private static dynamic? Descend(dynamic storage, string[] path, Dictionary<string, object?> folders)
	{
		dynamic? current = storage.GetFolder;
		string key = (string)storage.Name;
		foreach (string part in path)
		{
			key += '/' + part;
			if (folders.TryGetValue(key, out object? known))
			{
				current = known;
			}
			else
			{
				dynamic? next = null;
				foreach (dynamic item in current!.Items())
				{
					if (item.IsFolder && string.Equals((string)item.Name, part, StringComparison.OrdinalIgnoreCase))
					{
						next = item.GetFolder;
						break;
					}
				}

				folders[key] = next;
				current = next;
			}

			if (current is null) return null;
		}

		return current;
	}

	/// <summary>The logs in a folder and in the folders under it (the app keeps a second copy in "Download").</summary>
	private static List<dynamic> LogsIn(dynamic folder)
	{
		List<dynamic> logs = [];
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (dynamic item in folder.Items())
		{
			if (item.IsFolder)
			{
				if (!string.Equals((string)item.Name, "Download", StringComparison.OrdinalIgnoreCase)) continue;
				foreach (dynamic inner in item.GetFolder.Items())
				{
					if (!inner.IsFolder && IsLogName((string)inner.Name) && seen.Add((string)inner.Name)) logs.Add(inner);
				}
			}
			else if (IsLogName((string)item.Name) && seen.Add((string)item.Name)) logs.Add(item);
		}

		return logs;
	}

	/// <summary>The shell copies in the background: the file is there once it has a size that stops changing. False (and the file removed) when it never does.</summary>
	private static bool WaitForCopy(string target, CancellationToken cancel)
	{
		DateTime until = DateTime.UtcNow + CopyTimeout;
		long last = -1;
		int stable = 0;
		while (DateTime.UtcNow < until)
		{
			cancel.ThrowIfCancellationRequested();
			long size = File.Exists(target) ? new FileInfo(target).Length : -1;
			stable = size > 0 && size == last ? stable + 1 : 0;
			if (stable >= 3) return true;

			last = size;
			Thread.Sleep(200);
		}

		AppLogger.Warn($"Copying {Path.GetFileName(target)} from the phone did not finish in {CopyTimeout.TotalSeconds:0} s.");
		try
		{
			File.Delete(target);
		}
		catch (IOException)
		{
		}

		return false;
	}
}
