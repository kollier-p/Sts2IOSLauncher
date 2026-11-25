using System.Reflection;

namespace Launcher.Host;

internal sealed class GameWarmupMemory
{
    const ulong MiB = GameMemory.MiB;
    const ulong MaxVideoGrowth = 128 * MiB;
    const ulong MinProcessHeadroom = 640 * MiB;

    readonly MethodInfo renderingInfo;
    readonly object videoMemory;
    ulong initialVideo;

    internal GameWarmupMemory(Type renderingServer, Type renderingInfoType)
    {
        renderingInfo = renderingServer.GetMethod("GetRenderingInfo", [renderingInfoType])!;
        videoMemory = Enum.Parse(renderingInfoType, "VideoMemUsed");
        initialVideo = VideoBytes();
    }

    internal string? LimitReached()
    {
        var video = VideoBytes();
        // An early menu can reach _Ready before the renderer publishes memory counters.
        if (initialVideo == 0) initialVideo = video;
        if (video > initialVideo && video - initialVideo >= MaxVideoGrowth)
            return $"renderer memory grew {(video - initialVideo) / MiB} MiB";
        if (GameMemory.AvailableBytes() is ulong available && available < MinProcessHeadroom)
            return $"iPad process headroom {available / MiB} MiB";
        return null;
    }

    ulong VideoBytes() => Convert.ToUInt64(renderingInfo.Invoke(null, [videoMemory]));
}
