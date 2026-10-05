using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

public static class AppAssets
{
	private static readonly Lock Gate = new();
	private static readonly Dictionary<string, Bitmap?> Images = new(StringComparer.Ordinal);
	private static WindowIcon? _icon;
	private static bool _iconLoaded;

	public static WindowIcon? Icon
	{
		get
		{
			lock (Gate)
			{
				if (!_iconLoaded)
				{
					_icon = Load("OsmoOverlay.ico", path => new WindowIcon(path));
					_iconLoaded = true;
				}
				return _icon;
			}
		}
	}

	public static Bitmap? Image(string path)
	{
		lock (Gate)
		{
			if (!Images.TryGetValue(path, out Bitmap? image))
			{
				image = Load(path, assetPath => new Bitmap(assetPath));
				Images.Add(path, image);
			}
			return image;
		}
	}

	public static void Dispose()
	{
		lock (Gate)
		{
			foreach (Bitmap? image in Images.Values) image?.Dispose();
			Images.Clear();
		}
	}

	private static T? Load<T>(string path, Func<string, T> load) where T : class
	{
		try
		{
			return load(AssetPath(path));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			AppLogger.Warn(ex, string.Format(Strings.Assets_LoadFailed, path));
			return null;
		}
	}

	private static string AssetPath(string path)
	{
		return Path.Combine(AppContext.BaseDirectory, "Assets", path.Replace('/', Path.DirectorySeparatorChar));
	}
}

public sealed class AssetImageExtension : MarkupExtension
{
	public string Path { get; set; } = "";

	public override object ProvideValue(IServiceProvider serviceProvider)
	{
		return AppAssets.Image(Path)!;
	}
}
