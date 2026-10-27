using System.Runtime.InteropServices;

namespace YourLocalLib.Services;

public interface IScanFeedback
{
    void Chime();
    void Problem();
    void Speak(string text);
    void Cancel();
}

public sealed class WindowsScanFeedback : IScanFeedback
{
    private readonly byte[] _chime = Tone(new[] { (880, 70), (1175, 110) });
    private readonly byte[] _problem = Tone(new[] { (196, 90), (164, 140) });
    private object? _synth;

    public void Chime() => Play(_chime);

    public void Problem() => Play(_problem);

    public void Speak(string text)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            _synth ??= CreateSynth();
            var type = _synth.GetType();
            type.GetMethod("SpeakAsyncCancelAll")?.Invoke(_synth, null);
            type.GetMethod("SpeakAsync", new[] { typeof(string) })?.Invoke(_synth, new object[] { text });
        }
        catch
        {
            // Speech is optional. The card still shows the result.
        }
    }

    public void Cancel()
    {
        if (_synth == null)
            return;
        try
        {
            _synth.GetType().GetMethod("SpeakAsyncCancelAll")?.Invoke(_synth, null);
        }
        catch
        {
            // Ignore a missing speech engine.
        }
    }

    private static object CreateSynth()
    {
        var type = Type.GetType("System.Speech.Synthesis.SpeechSynthesizer, System.Speech")
            ?? throw new InvalidOperationException("Speech is unavailable.");
        return Activator.CreateInstance(type)!;
    }

    private static void Play(byte[] wav)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            PlaySound(wav, IntPtr.Zero, 0x0001 | 0x0004 | 0x0002);
        }
        catch
        {
            // The visual result remains if the machine has no sound.
        }
    }

    private static byte[] Tone((int Frequency, int Milliseconds)[] notes)
    {
        const int rate = 22050;
        var samples = new List<short>();
        foreach (var (frequency, milliseconds) in notes)
        {
            var count = rate * milliseconds / 1000;
            for (var i = 0; i < count; i++)
            {
                var envelope = Math.Min(1d, i / 200d) * Math.Min(1d, (count - i) / 400d);
                var wave = Math.Sin(2 * Math.PI * frequency * i / rate) * envelope * 0.35;
                samples.Add((short)(wave * short.MaxValue));
            }
            for (var i = 0; i < rate / 40; i++)
                samples.Add(0);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataLength = samples.Count * 2;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        foreach (var sample in samples)
            writer.Write(sample);
        return stream.ToArray();
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] pszSound, IntPtr hmod, uint fdwSound);
}
