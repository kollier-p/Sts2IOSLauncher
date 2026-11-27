using System.Runtime.InteropServices;

namespace Launcher.Host;

internal sealed class IosTextureEncoder : IDisposable
{
    nint context;
    readonly Dictionary<long, int> kept = [];

    // Why textures stayed in a desktop format the iPad GPU expands to RGBA8.
    internal string KeptSummary => string.Join(", ", kept.OrderBy(pair => pair.Key).Select(pair => $"{Reason(pair.Key)} {pair.Value}"));

    static string Reason(long code) => code switch
    {
        -2 => "not a texture container",
        -3 => "unsupported format",
        -4 => "oversized",
        -5 => "unexpected data size",
        -9 => "quality check",
        _ => $"code {code}"
    };

    internal byte[]? Convert(byte[] source)
    {
        long length = OutputBytes(source, source.Length);
        if (length < 0)
        {
            // An unsupported source stays as installed.
            kept[length] = kept.GetValueOrDefault(length) + 1;
            return null;
        }
        if (context == 0)
        {
            context = Create(Math.Clamp(Environment.ProcessorCount / 2, 1, 3));
            if (context == 0) throw new InvalidOperationException("Could not start the iPad texture encoder.");
        }
        var result = new byte[checked((int)length)];
        return Encode(context, source, source.Length, result, result.Length) switch
        {
            0 => result,
            -9 => KeepForQuality(), // Keep the original when the converted pixels are clearly wrong.
            var code => throw new InvalidOperationException($"The iPad texture encoder failed ({code}).")
        };
    }

    byte[]? KeepForQuality()
    {
        kept[-9] = kept.GetValueOrDefault(-9) + 1;
        return null;
    }

    public void Dispose()
    {
        if (context != 0) Free(context);
        context = 0;
    }

    [DllImport("__Internal", EntryPoint = "sts2_texture_encoder_create")]
    static extern nint Create(int threads);

    [DllImport("__Internal", EntryPoint = "sts2_texture_encoder_free")]
    static extern void Free(nint context);

    [DllImport("__Internal", EntryPoint = "sts2_texture_encoder_output_bytes")]
    static extern long OutputBytes(byte[] source, long size);

    [DllImport("__Internal", EntryPoint = "sts2_texture_encoder_convert")]
    static extern int Encode(nint context, byte[] source, long size, [Out] byte[] result, long capacity);
}
