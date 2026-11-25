using System.Runtime.InteropServices;

namespace Launcher.Host;

internal sealed class IosTextureEncoder : IDisposable
{
    nint context;

    internal byte[]? Convert(byte[] source)
    {
        long length = OutputBytes(source, source.Length);
        if (length < 0) return null; // An unsupported source stays as installed.
        if (context == 0)
        {
            context = Create(Math.Clamp(Environment.ProcessorCount / 2, 1, 3));
            if (context == 0) throw new InvalidOperationException("Could not start the iPad texture encoder.");
        }
        var result = new byte[checked((int)length)];
        return Encode(context, source, source.Length, result, result.Length) switch
        {
            0 => result,
            -9 => null, // Keep the original when the converted pixels fail the quality check.
            var code => throw new InvalidOperationException($"The iPad texture encoder failed ({code}).")
        };
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
