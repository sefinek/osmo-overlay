$ErrorActionPreference = 'Stop'
$soundDirectory = Join-Path $PSScriptRoot '../OsmoOverlay.Gui/Assets/Sounds'
[void][System.IO.Directory]::CreateDirectory($soundDirectory)

Add-Type -TypeDefinition @'
using System;
using System.IO;

public static class OsmoUiSoundGenerator
{
    private const int Rate = 48000;

    private static double Frequency(double note)
    {
        return 440 * Math.Pow(2, (note - 69) / 12);
    }

    private static void Tone(double[,] mix, double start, double length, double note,
        double gain, double pan, double warmth)
    {
        int offset = (int)(start * Rate);
        int count = Math.Min((int)(length * Rate), mix.GetLength(0) - offset);
        double frequency = Frequency(note);
        double left = Math.Sqrt((1 - pan) / 2);
        double right = Math.Sqrt((1 + pan) / 2);
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / Rate;
            double attack = 1 - Math.Exp(-t / 0.006);
            double release = Math.Pow(Math.Sin(Math.PI * 0.5 * (1 - t / length)), 2);
            double decay = Math.Exp(-t / (0.13 + warmth * 0.13));
            double phase = 2 * Math.PI * frequency * t;
            double body = Math.Sin(phase + 0.32 * Math.Sin(phase * 2) * Math.Exp(-t * 22));
            double glass = 0.16 * Math.Sin(phase * 2.001) * Math.Exp(-t * 13)
                + 0.07 * Math.Sin(phase * 3.997) * Math.Exp(-t * 25);
            double halo = 0.12 * Math.Sin(phase * 1.003) * Math.Exp(-t * 5);
            double sample = gain * attack * release * (body * decay + glass + halo);
            mix[offset + i, 0] += sample * left;
            mix[offset + i, 1] += sample * right;
        }
    }

    private static void Write(string directory, string name, double[,] dry, double peak)
    {
        int frames = dry.GetLength(0);
        double[,] wet = (double[,])dry.Clone();
        int[] delays = { 1397, 2551, 4099, 6421 };
        double[] gains = { 0.11, 0.075, 0.045, 0.025 };
        for (int tap = 0; tap < delays.Length; tap++)
            for (int i = delays[tap]; i < frames; i++)
                for (int channel = 0; channel < 2; channel++)
                    wet[i, channel] += dry[i - delays[tap], 1 - channel] * gains[tap];

        double maximum = 0;
        for (int i = 0; i < frames; i++)
            for (int channel = 0; channel < 2; channel++)
                maximum = Math.Max(maximum, Math.Abs(wet[i, channel]));

        double scale = peak / maximum;
        double sumSquares = 0;
        double maxStep = 0;
        double[] previous = new double[2];
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, name + ".wav"))))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + frames * 4);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(Rate);
            writer.Write(Rate * 4);
            writer.Write((short)4);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(frames * 4);
            for (int i = 0; i < frames; i++)
            {
                double fade = Math.Min(1, (double)(frames - 1 - i) / (Rate * 0.035));
                for (int channel = 0; channel < 2; channel++)
                {
                    double sample = wet[i, channel] * scale * fade;
                    sumSquares += sample * sample;
                    maxStep = Math.Max(maxStep, Math.Abs(sample - previous[channel]));
                    previous[channel] = sample;
                    writer.Write((short)Math.Round(sample * 32767));
                }
            }
        }
        Console.WriteLine("{0}: {1:F2}s, peak {2:F1} dBFS, RMS {3:F1} dBFS, max step {4:F4}",
            name, (double)frames / Rate, 20 * Math.Log10(peak),
            20 * Math.Log10(Math.Sqrt(sumSquares / (frames * 2))), maxStep);
    }

    public static void Generate(string directory)
    {
        var question = new double[(int)(Rate * 0.40), 2];
        Tone(question, 0, 0.32, 76, 0.6, 0, 0.8);
        Tone(question, 0.065, 0.25, 83, 0.14, 0.12, 0.6);
        Write(directory, "question", question, 0.25);

        var info = new double[(int)(Rate * 0.50), 2];
        Tone(info, 0, 0.42, 81, 0.55, -0.12, 0.8);
        Tone(info, 0.075, 0.34, 88, 0.23, 0.18, 0.6);
        Write(directory, "info", info, 0.28);

        var success = new double[(int)(Rate * 0.70), 2];
        Tone(success, 0, 0.36, 76, 0.45, -0.2, 0.8);
        Tone(success, 0.14, 0.47, 83, 0.56, 0.18, 1);
        Tone(success, 0.145, 0.47, 88, 0.12, 0.05, 0.9);
        Write(directory, "success", success, 0.32);

        var warning = new double[(int)(Rate * 0.70), 2];
        Tone(warning, 0, 0.27, 74, 0.5, -0.1, 0.7);
        Tone(warning, 0.23, 0.38, 74, 0.52, 0.1, 0.8);
        Tone(warning, 0.23, 0.32, 69, 0.16, 0, 0.8);
        Write(directory, "warning", warning, 0.34);

        var error = new double[(int)(Rate * 0.80), 2];
        Tone(error, 0, 0.33, 71, 0.48, -0.12, 1);
        Tone(error, 0.17, 0.53, 64, 0.55, 0.12, 1.2);
        Tone(error, 0.175, 0.51, 59, 0.2, 0, 1.2);
        Write(directory, "error", error, 0.36);

        var complete = new double[(int)(Rate * 1.40), 2];
        Tone(complete, 0, 0.44, 76, 0.39, -0.25, 0.9);
        Tone(complete, 0.14, 0.45, 80, 0.4, -0.05, 0.9);
        Tone(complete, 0.28, 0.47, 83, 0.43, 0.14, 1);
        Tone(complete, 0.46, 0.84, 88, 0.46, 0.24, 1.5);
        Tone(complete, 0.46, 0.82, 76, 0.14, -0.22, 1.5);
        Tone(complete, 0.465, 0.81, 83, 0.12, 0, 1.5);
        Write(directory, "render-complete", complete, 0.36);
    }
}
'@

[OsmoUiSoundGenerator]::Generate([System.IO.Path]::GetFullPath($soundDirectory))
