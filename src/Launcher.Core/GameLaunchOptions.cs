using System.Text.Json;

namespace Launcher.Core;

public sealed record GameLaunchOptions(int? FrameLimit = null, bool? TouchAsMouse = null,
    bool RenderWarmup = true, bool ExtendedWarmup = true);

public sealed class GameLaunchOptionsStore(string path)
{
    public GameLaunchOptions Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            using var input = File.OpenRead(path);
            var options = JsonSerializer.Deserialize<GameLaunchOptions>(input);
            return options is not null && Valid(options) ? options : new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save(GameLaunchOptions options)
    {
        if (!Valid(options)) throw new ArgumentException("Choose a supported frame rate limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(output, options);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static bool Valid(GameLaunchOptions options) => options.FrameLimit is null or 0 or 30 or 60 or 120;
}
