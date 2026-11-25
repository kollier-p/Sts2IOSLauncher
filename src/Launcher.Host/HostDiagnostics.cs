namespace Launcher.Host;
internal static class HostDiagnostics
{
    static readonly object Gate=new();
    static string? path,last;
    public static void Initialize(string directory)
    {
        lock(Gate){
            Directory.CreateDirectory(directory);
            path=Path.Combine(directory,"launcher.log");
            if(File.Exists(path)&&new FileInfo(path).Length>1024*1024)
                File.Move(path,path+".previous",true);
            Write("session started");
        }
    }
    public static void Stage(string stage){lock(Gate){if(last==stage)return;last=stage;Write("stage "+stage);}}
    public static void Error(string operation, Exception error)
    {
        lock (Gate)
        {
            Write(operation + " " + error.GetType().FullName + " HRESULT=" + error.HResult.ToString("X8"));
            for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            {
                if (cause is not PlatformNotSupportedException) continue;
                foreach (var frame in new System.Diagnostics.StackTrace(cause).GetFrames().Take(8))
                {
                    var method = frame.GetMethod();
                    if (method is not null) Write($"  at {method.DeclaringType?.FullName}.{method.Name}");
                }
                break;
            }
        }
    }
    static void Write(string message){try{if(path!=null)File.AppendAllText(path,DateTimeOffset.UtcNow.ToString("O")+" "+message+Environment.NewLine);}catch(IOException){}catch(UnauthorizedAccessException){}}
}
