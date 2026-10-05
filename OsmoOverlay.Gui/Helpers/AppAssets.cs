using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;

namespace OsmoOverlay.Gui;

public static class AppAssets
{
	private static readonly Dictionary<string, Bitmap> Images = new(StringComparer.Ordinal);
	private static WindowIcon? _icon;

	public static WindowIcon Icon => _icon ??= new WindowIcon(AssetPath("OsmoOverlay.ico"));

	public static Bitmap Image(string path)
	{
		if (!Images.TryGetValue(path, out Bitmap? image))
		{
			image = new Bitmap(AssetPath(path));
			Images.Add(path, image);
		}
		return image;
	}

	public static void Dispose()
	{
		foreach (Bitmap image in Images.Values) image.Dispose();
		Images.Clear();
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
		return AppAssets.Image(Path);
	}
}
