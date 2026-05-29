using System.Runtime.InteropServices;

namespace SoraV2BatteryTip;

internal sealed class AlertSoundService
{
    private const int SndAsync = 0x0001;
    private const int SndMemory = 0x0004;
    private const int SndNoDefault = 0x0002;
    private const int SndPurge = 0x0040;

    private readonly AppPaths _paths;
    private readonly Func<AppSettings> _settings;
    private readonly object _playLock = new();
    private GCHandle _playHandle;
    private byte[]? _playBuffer;

    public AlertSoundService(AppPaths paths, Func<AppSettings> settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public string[] GetSounds()
    {
        _paths.Ensure();
        return Directory.GetFiles(_paths.SoundsDirectory, "*.wav", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name.Equals("default.wav", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public void PlayCurrent()
    {
        var file = ResolveSoundPath(_settings().AlertSoundFile);
        if (file == null)
            return;
        var volume = Math.Clamp(_settings().AlertVolume, 0, 100);
        _ = Task.Run(() => PlayFile(file, volume));
    }

    public string? ResolveSoundPath(string? fileName)
    {
        _paths.Ensure();
        var safe = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "default.wav" : fileName);
        var selected = Path.Combine(_paths.SoundsDirectory, safe);
        if (File.Exists(selected))
            return selected;
        var fallback = Path.Combine(_paths.SoundsDirectory, "default.wav");
        return File.Exists(fallback) ? fallback : null;
    }

    public void OpenFolder()
    {
        _paths.Ensure();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_paths.SoundsDirectory) { UseShellExecute = true }); }
        catch { }
    }

    private void PlayFile(string file, int volume)
    {
        try
        {
            var wav = BuildPlayableWave(file, volume);
            if (wav == null)
                return;

            lock (_playLock)
            {
                StopAndReleaseLocked();
                _playBuffer = wav;
                _playHandle = GCHandle.Alloc(_playBuffer, GCHandleType.Pinned);
                PlaySound(_playHandle.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(Math.Max(600, EstimateDurationMilliseconds(wav) + 250));
                lock (_playLock)
                    StopAndReleaseLocked();
            });
        }
        catch { }
    }

    private void StopAndReleaseLocked()
    {
        try { PlaySound(IntPtr.Zero, IntPtr.Zero, SndPurge); } catch { }
        if (_playHandle.IsAllocated)
            _playHandle.Free();
        _playBuffer = null;
    }

    private static byte[]? BuildPlayableWave(string file, int volume)
    {
        var bytes = File.ReadAllBytes(file);
        var scaled = TryBuildScaledPcmWave(bytes, volume) ?? bytes;
        return EnsureCleanTail(scaled);
    }

    private static byte[]? TryBuildScaledPcmWave(byte[] bytes, int volume)
    {
        if (bytes.Length < 44 || !IsAscii(bytes, 0, "RIFF") || !IsAscii(bytes, 8, "WAVE"))
            return null;

        var fmtOffset = FindChunk(bytes, "fmt ");
        var dataOffset = FindChunk(bytes, "data");
        if (fmtOffset < 0 || dataOffset < 0)
            return null;

        var fmtSize = BitConverter.ToInt32(bytes, fmtOffset + 4);
        if (fmtSize < 16)
            return null;

        var fmtData = fmtOffset + 8;
        var audioFormat = BitConverter.ToUInt16(bytes, fmtData);
        var bitsPerSample = BitConverter.ToUInt16(bytes, fmtData + 14);
        if (audioFormat != 1 || bitsPerSample is not (8 or 16 or 24 or 32))
            return null;

        var dataSize = BitConverter.ToInt32(bytes, dataOffset + 4);
        var dataStart = dataOffset + 8;
        if (dataSize <= 0 || dataStart + dataSize > bytes.Length)
            return null;

        var output = (byte[])bytes.Clone();
        var gain = Math.Clamp(volume, 0, 100) / 100.0;
        var sampleBytes = bitsPerSample / 8;
        var dataEnd = dataStart + dataSize;

        for (var i = dataStart; i + sampleBytes <= dataEnd; i += sampleBytes)
        {
            if (bitsPerSample == 8)
            {
                var centered = output[i] - 128;
                output[i] = (byte)Math.Clamp(128 + (int)Math.Round(centered * gain), 0, 255);
            }
            else if (bitsPerSample == 16)
            {
                var sample = BitConverter.ToInt16(output, i);
                var scaled = (short)Math.Clamp((int)Math.Round(sample * gain), short.MinValue, short.MaxValue);
                var b = BitConverter.GetBytes(scaled);
                output[i] = b[0];
                output[i + 1] = b[1];
            }
            else if (bitsPerSample == 24)
            {
                var sample = output[i] | (output[i + 1] << 8) | (output[i + 2] << 16);
                if ((sample & 0x800000) != 0)
                    sample |= unchecked((int)0xFF000000);
                var scaled = Math.Clamp((int)Math.Round(sample * gain), -8388608, 8388607);
                output[i] = (byte)(scaled & 0xFF);
                output[i + 1] = (byte)((scaled >> 8) & 0xFF);
                output[i + 2] = (byte)((scaled >> 16) & 0xFF);
            }
            else
            {
                var sample = BitConverter.ToInt32(output, i);
                var scaled = Math.Clamp((long)Math.Round(sample * gain), int.MinValue, int.MaxValue);
                var b = BitConverter.GetBytes((int)scaled);
                output[i] = b[0];
                output[i + 1] = b[1];
                output[i + 2] = b[2];
                output[i + 3] = b[3];
            }
        }

        return output;
    }

    private static byte[] EnsureCleanTail(byte[] bytes)
    {
        if (bytes.Length < 44 || !IsAscii(bytes, 0, "RIFF") || !IsAscii(bytes, 8, "WAVE"))
            return bytes;

        var fmtOffset = FindChunk(bytes, "fmt ");
        var dataOffset = FindChunk(bytes, "data");
        if (fmtOffset < 0 || dataOffset < 0)
            return bytes;

        var channels = BitConverter.ToUInt16(bytes, fmtOffset + 8 + 2);
        var sampleRate = BitConverter.ToInt32(bytes, fmtOffset + 8 + 4);
        var bitsPerSample = BitConverter.ToUInt16(bytes, fmtOffset + 8 + 14);
        var dataSize = BitConverter.ToInt32(bytes, dataOffset + 4);
        var blockAlign = Math.Max(1, channels * bitsPerSample / 8);
        if (channels <= 0 || sampleRate <= 0 || dataSize <= 0 || bitsPerSample is not (8 or 16 or 24 or 32))
            return bytes;

        var tailFrames = (int)(sampleRate * 0.18);
        var tailBytes = tailFrames * blockAlign;
        var output = new byte[bytes.Length + tailBytes];
        Buffer.BlockCopy(bytes, 0, output, 0, bytes.Length);

        var newDataSize = dataSize + tailBytes;
        var newRiffSize = output.Length - 8;
        Buffer.BlockCopy(BitConverter.GetBytes(newRiffSize), 0, output, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(newDataSize), 0, output, dataOffset + 4, 4);

        var dataStart = dataOffset + 8;
        var dataEnd = dataStart + dataSize;
        var fadeBytes = Math.Min(dataSize, (int)(sampleRate * 0.12) * blockAlign);
        var fadeStart = dataEnd - fadeBytes;
        FadeToZero(output, fadeStart, dataEnd, bitsPerSample);
        ZeroPcm(output, dataEnd, output.Length, bitsPerSample);
        return output;
    }

    private static void FadeToZero(byte[] bytes, int start, int end, int bitsPerSample)
    {
        var sampleBytes = bitsPerSample / 8;
        var sampleCount = Math.Max(1, (end - start) / sampleBytes);
        var index = 0;
        for (var i = start; i + sampleBytes <= end; i += sampleBytes, index++)
        {
            var remaining = 1.0 - index / (double)Math.Max(1, sampleCount - 1);
            var gain = remaining * remaining;
            ScaleSample(bytes, i, bitsPerSample, gain);
        }
    }

    private static void ZeroPcm(byte[] bytes, int start, int end, int bitsPerSample)
    {
        var sampleBytes = bitsPerSample / 8;
        for (var i = start; i + sampleBytes <= end; i += sampleBytes)
        {
            if (bitsPerSample == 8)
                bytes[i] = 128;
            else
                for (var j = 0; j < sampleBytes; j++) bytes[i + j] = 0;
        }
    }

    private static void ScaleSample(byte[] bytes, int offset, int bitsPerSample, double gain)
    {
        if (bitsPerSample == 8)
        {
            var centered = bytes[offset] - 128;
            bytes[offset] = (byte)Math.Clamp(128 + (int)Math.Round(centered * gain), 0, 255);
        }
        else if (bitsPerSample == 16)
        {
            var sample = BitConverter.ToInt16(bytes, offset);
            var scaled = (short)Math.Clamp((int)Math.Round(sample * gain), short.MinValue, short.MaxValue);
            var b = BitConverter.GetBytes(scaled);
            bytes[offset] = b[0]; bytes[offset + 1] = b[1];
        }
        else if (bitsPerSample == 24)
        {
            var sample = bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
            if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
            var scaled = Math.Clamp((int)Math.Round(sample * gain), -8388608, 8388607);
            bytes[offset] = (byte)(scaled & 0xFF);
            bytes[offset + 1] = (byte)((scaled >> 8) & 0xFF);
            bytes[offset + 2] = (byte)((scaled >> 16) & 0xFF);
        }
        else
        {
            var sample = BitConverter.ToInt32(bytes, offset);
            var scaled = Math.Clamp((long)Math.Round(sample * gain), int.MinValue, int.MaxValue);
            var b = BitConverter.GetBytes((int)scaled);
            bytes[offset] = b[0]; bytes[offset + 1] = b[1]; bytes[offset + 2] = b[2]; bytes[offset + 3] = b[3];
        }
    }

    private static int EstimateDurationMilliseconds(byte[] bytes)
    {
        try
        {
            var fmtOffset = FindChunk(bytes, "fmt ");
            var dataOffset = FindChunk(bytes, "data");
            if (fmtOffset < 0 || dataOffset < 0)
                return 1000;
            var byteRate = BitConverter.ToInt32(bytes, fmtOffset + 8 + 8);
            var dataSize = BitConverter.ToInt32(bytes, dataOffset + 4);
            if (byteRate <= 0 || dataSize <= 0)
                return 1000;
            return (int)Math.Ceiling(dataSize * 1000.0 / byteRate);
        }
        catch { return 1000; }
    }

    private static int FindChunk(byte[] bytes, string id)
    {
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (size < 0 || offset + 8 + size > bytes.Length)
                return -1;
            if (IsAscii(bytes, offset, id))
                return offset;
            offset += 8 + size + (size & 1);
        }
        return -1;
    }

    private static bool IsAscii(byte[] bytes, int offset, string value)
    {
        if (offset < 0 || offset + value.Length > bytes.Length)
            return false;
        for (var i = 0; i < value.Length; i++)
            if (bytes[offset + i] != value[i])
                return false;
        return true;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, int fdwSound);
}

