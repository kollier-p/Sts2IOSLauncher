using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Launcher.Core;
using Launcher.Steam;
using SteamKit2.Authentication;
namespace Launcher.Host;

// The native host calls these methods via coreclr_create_delegate.
// No UIKit binding and no second runtime. Never serialize credentials in status.
public static class Entry
{
    static readonly object Sync = new();
    static PayloadStore? store;
    static string documents = "", managed = "";
    static CancellationTokenSource? active;
    static TaskCompletionSource<string>? prompt;
    static string? assemblyRoot;
    static string? adapterRoot;
    static State state = new("idle", "Sign in to Steam");
    sealed record State(string Stage, string Message, long Completed=0,long Total=0,double Speed=0,string? Eta=null,string? Prompt=null,string[]? Branches=null);
    sealed record Request(string Username,string Password,string Branch,string? Manifest,string? BetaPassword);
    static void Publish(State next) { lock(Sync) state=next; }
    static string Str(nint p) => Marshal.PtrToStringUTF8(p) ?? "";
    public static int Initialize(nint payloadRoot,nint docs,nint bundled)
    {
        try {
            store = new PayloadStore(Str(payloadRoot));
            documents = Str(docs); managed=Str(bundled);
            Directory.CreateDirectory(documents);
            AssemblyLoadContext.Default.Resolving += Resolve;
            Publish(new("idle",store.Current() is {} r ? $"Installed: {r.Branch} / {r.ManifestId}" : "No game installed"));
            return 0;
        } catch { return -1; }
    }
    static Assembly? Resolve(AssemblyLoadContext context,AssemblyName name)
    {
        foreach(var dir in new[]{adapterRoot,assemblyRoot})
        {
            if(dir is null || name.Name is null || name.Name.IndexOfAny(Path.GetInvalidFileNameChars())>=0) continue;
            var path=Path.Combine(dir,name.Name+".dll");
            if(File.Exists(path)) return context.LoadFromAssemblyPath(path);
        }
        return null;
    }
    public static int CopyStatus(nint buffer,int capacity)
    {
        byte[] json; lock(Sync) json=JsonSerializer.SerializeToUtf8Bytes(state);
        if(buffer==0 || capacity<=json.Length) return -json.Length-1;
        Marshal.Copy(json,0,buffer,json.Length); Marshal.WriteByte(buffer,json.Length,0); return json.Length;
    }
    public static int BeginDownload(nint input)
    {
        try {
            var request=JsonSerializer.Deserialize<Request>(Str(input)) ?? throw new InvalidDataException();
            if(string.IsNullOrWhiteSpace(request.Username)||string.IsNullOrEmpty(request.Password)) return -2;
            ulong? exact=null;
            if(!string.IsNullOrWhiteSpace(request.Manifest)) {
                if(!ulong.TryParse(request.Manifest,out var id)||id==0) return -3;
                exact=id;
            }
            CancellationTokenSource cts;
            lock(Sync) { if(active is not null || store is null) return -4; active=cts=new(); }
            _=Task.Run(()=>Download(request,exact,cts));
            return 0;
        } catch { return -1; }
    }
    static async Task Download(Request request,ulong? exact,CancellationTokenSource cts)
    {
        try {
            Publish(new("auth","Connecting to Steam"));
            await using var session=new SteamSession();
            await session.AuthenticateAsync(request.Username,request.Password,new Guard(cts.Token),null,false,cts.Token);
            var branch=string.IsNullOrWhiteSpace(request.Branch)?"public":request.Branch;
            if(exact is null && branch=="?") {
                var branches=await session.ListBranchesAsync(cts.Token);
                branch=await Ask("branch","Select Steam branch",cts.Token,branches.Select(b=>b.Name).ToArray());
                if(!branches.Any(b=>b.Name==branch)) throw new InvalidDataException();
            }
            var beta=request.BetaPassword;
            if(string.IsNullOrEmpty(beta) && branch!="public" && exact is null) {
                var branches=await session.ListBranchesAsync(cts.Token);
                if(branches.Any(b=>b.Name==branch && b.PasswordRequired)) beta=await Ask("password","Private beta password",cts.Token);
            }
            var reporter=new DirectProgress<DownloadProgress>(p=>Publish(new(p.Stage,p.Stage,p.Completed,p.Total,p.BytesPerSecond,p.Remaining?.ToString(@"hh\:mm\:ss"))));
            var receipt=await session.DownloadAndInstallAsync(store!,branch,exact,reporter,cts.Token,beta);
            Publish(new("installed",$"Installed {receipt.Branch} / {receipt.ManifestId}. Ready for launch preparation."));
        } catch(OperationCanceledException) { Publish(new("idle","Cancelled; existing installation retained.")); }
          catch(Exception e) { Publish(new("error",$"Download failed ({e.GetType().Name}).")); }
        finally { lock(Sync){active=null;prompt=null;} cts.Dispose(); }
    }
    static async Task<string> Ask(string kind,string message,CancellationToken ct,string[]? branches=null)
    {
        var tcs=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(Sync){prompt=tcs;state=new("prompt",message,Prompt:kind,Branches:branches);}
        try{return await tcs.Task.WaitAsync(ct);}
        finally{lock(Sync){if(prompt==tcs)prompt=null;}}
    }
    public static int Reply(nint input) { lock(Sync) return prompt?.TrySetResult(Str(input))==true?0:-1; }
    public static int Cancel() { lock(Sync){active?.Cancel();return 0;} }
    public static int Prepare()
    {
        CancellationTokenSource cts;
        lock(Sync){if(active is not null||store is null)return -1;active=cts=new();}
        _=Task.Run(()=>{
            try {
                Publish(new("verify","Verifying installation"));
                var plan=LaunchPlan.Prepare(store!,documents,cts.Token);
                // Keep the verified immutable payload unchanged. Godot uses a stable user:// pack.
                var adapter=Path.Combine(Path.GetDirectoryName(store!.Root)!,"RuntimeAdapters",plan.Receipt.PayloadId);
                Directory.CreateDirectory(adapter);
                var harmony=Path.Combine(plan.Assemblies,"0Harmony.dll");
                if(File.Exists(harmony)) Launcher.Adapters.Rewriter.Prepare("harmony",harmony,Path.Combine(managed,"STS2JitSupport.dll"),Path.Combine(adapter,"0Harmony.dll"));
                assemblyRoot=plan.Assemblies;adapterRoot=adapter;
                // Load by name before Godot asks the runtime for its delegate.
                AssemblyLoadContext.Default.LoadFromAssemblyPath(plan.GameAssembly);
                var link=Path.Combine(documents,"StS2.pck");
                var temporary=link+"."+Guid.NewGuid().ToString("N")+".tmp";
                try { if(NativeLink(plan.Pack,temporary)!=0) File.Copy(plan.Pack,temporary); File.Move(temporary,link,true); }
                finally {if(File.Exists(temporary))File.Delete(temporary);}
                Publish(new("ready","Game verified. Starting Godot."));
            } catch(OperationCanceledException){Publish(new("idle","Launch cancelled"));}
              catch(Exception e){Publish(new("error",$"Launch preparation failed ({e.GetType().Name})."));}
            finally{lock(Sync){active=null;}cts.Dispose();}
        });
        return 0;
    }
    [DllImport("libSystem.B.dylib",EntryPoint="link",SetLastError=true)]
    static extern int NativeLink([MarshalAs(UnmanagedType.LPUTF8Str)] string source,[MarshalAs(UnmanagedType.LPUTF8Str)] string target);
    sealed class DirectProgress<T>(Action<T> report):IProgress<T>{public void Report(T value)=>report(value);}
    sealed class Guard(CancellationToken ct):IAuthenticator
    {
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)=>Ask("code",previousCodeWasIncorrect?"Incorrect Steam Guard code":"Steam Guard mobile code",ct);
        public Task<string> GetEmailCodeAsync(string email,bool previousCodeWasIncorrect)=>Ask("code",previousCodeWasIncorrect?"Incorrect email code":"Steam Guard email code",ct);
        public async Task<bool> AcceptDeviceConfirmationAsync()=>await Ask("confirm","Approve in Steam app or enter code",ct)=="approve";
    }
}
