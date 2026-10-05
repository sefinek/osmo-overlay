using System.Buffers.Binary;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui.Native;

public enum UiSound
{
	Question,
	Info,
	Success,
	Warning,
	Error,
	RenderComplete
}

internal static class AppSound
{
	private static readonly Lock Gate = new();
	private static readonly Dictionary<UiSound, float[]> Samples = [];
	private static AudioOutput? _output;
	private static bool _unavailable;

	public static void Play(UiSound sound)
	{
		lock (Gate)
		{
			if (_unavailable) return;

			try
			{
				if (!Samples.TryGetValue(sound, out float[]? samples))
				{
					samples = Load(sound);
					Samples.Add(sound, samples);
				}

				_output ??= AudioOutput.TryOpen(48000, 2);
				if (_output is null)
				{
					_unavailable = true;
					return;
				}

				_output.Clear();
				_output.Push(samples);
				_output.Flush();
				_output.Start();
			}
			catch (Exception ex) when (ex is IOException or InvalidDataException or DllNotFoundException
				or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
			{
				_unavailable = true;
			}
		}
	}

	public static void Dispose()
	{
		lock (Gate)
		{
			_unavailable = true;
			_output?.Dispose();
			_output = null;
			Samples.Clear();
		}
	}

	private static float[] Load(UiSound sound)
	{
		string name = sound switch
		{
			UiSound.Question => "question",
			UiSound.Info => "info",
			UiSound.Success => "success",
			UiSound.Warning => "warning",
			UiSound.Error => "error",
			UiSound.RenderComplete => "render-complete",
			_ => throw new ArgumentOutOfRangeException(nameof(sound))
		};
		using Stream stream = typeof(AppSound).Assembly.GetManifestResourceStream($"OsmoOverlay.Gui.Sounds.{name}.wav")
			?? throw new InvalidDataException($"Missing UI sound: {name}");
		Span<byte> header = stackalloc byte[44];
		stream.ReadExactly(header);
		if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..16].SequenceEqual("WAVEfmt "u8)
			|| BinaryPrimitives.ReadInt32LittleEndian(header[16..]) != 16
			|| BinaryPrimitives.ReadInt16LittleEndian(header[20..]) != 1
			|| BinaryPrimitives.ReadInt16LittleEndian(header[22..]) != 2
			|| BinaryPrimitives.ReadInt32LittleEndian(header[24..]) != 48000
			|| BinaryPrimitives.ReadInt16LittleEndian(header[34..]) != 16
			|| !header[36..40].SequenceEqual("data"u8))
			throw new InvalidDataException($"Invalid UI sound format: {name}");

		int bytes = BinaryPrimitives.ReadInt32LittleEndian(header[40..]);
		if (bytes <= 0 || bytes % 4 != 0 || bytes != stream.Length - 44)
			throw new InvalidDataException($"Invalid UI sound length: {name}");

		var samples = new float[bytes / 2];
		using var reader = new BinaryReader(stream);
		for (int i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16() / 32768f;
		return samples;
	}
}
