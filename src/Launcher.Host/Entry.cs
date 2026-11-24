using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Launcher.Core;
using Launcher.Steam;
using SteamKit2.Authentication;

namespace Launcher.Host;

// Entry points called by the native host via coreclr_create_delegate.
public static class Entry
{
    static readonly object Sync = new();
    static PayloadStore? store;
    static string documents = "", managed = "";
    static CancellationTokenSource? active;
    static TaskCompletionSource<string>? prompt;
    static long promptId;
    static string? assemblyRoot, adapterRoot, releaseInfo;
    static Assembly? loadedGame;
    static InstallReceipt? current;
    static State state = new("idle", "No game installed");

    sealed record Choice(string Value, string Title, string Detail);
    sealed record State(string Stage, string Message, long Completed = 0, long Total = 0, double Speed = 0,
        string? Eta = null, string? Prompt = null, Choice[]? Choices = null, long PromptId = 0, string? Installed = null);
    sealed record Request(string Username, string Password, string Branch, string? Manifest, string? BetaPassword,
        bool Remember = false, bool Saved = false);

    static void Publish(State next)
    {
        lock (Sync) state = next;
        HostDiagnostics.Stage(next.Stage);
    }

    static string Str(nint pointer) => Marshal.PtrToStringUTF8(pointer) ?? "";

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Initialize(nint payloadRoot, nint docs, nint bundled)
    {
        try
        {
            store = new PayloadStore(Str(payloadRoot));
            documents = Str(docs);
            managed = Str(bundled);
            Directory.CreateDirectory(documents);
            HostDiagnostics.Initialize(Path.Combine(documents, "LauncherLogs"));
            AssemblyLoadContext.Default.Resolving += Resolve;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += NativeLibraries.Resolve;
            try
            {
                current = store.Current();
                Publish(new("idle", current is null ? "Install or import the game to get started." : "Ready to play."));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                HostDiagnostics.Error("read installation", error);
                Publish(new("error", "The active installation is unavailable. Select an installed version or install again."));
            }
            return 0;
        }
        catch (Exception error)
        {
            HostDiagnostics.Error("initialize", error);
            return -1;
        }
    }

    static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        var simpleName = name.Name;
        if (simpleName is null || simpleName.StartsWith("System.", StringComparison.Ordinal) ||
            simpleName is "System" or "mscorlib" or "netstandard" ||
            simpleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        foreach (var directory in new[] { adapterRoot, assemblyRoot })
        {
            if (directory is null) continue;
            var path = Path.Combine(directory, simpleName + ".dll");
            if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
        }
        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CopyStatus(nint buffer, int capacity)
    {
        byte[] json;
        lock (Sync) json = JsonSerializer.SerializeToUtf8Bytes(state with { Installed = current?.DisplayName() });
        if (buffer == 0 || capacity <= json.Length) return -json.Length - 1;
        Marshal.Copy(json, 0, buffer, json.Length);
        Marshal.WriteByte(buffer, json.Length, 0);
        return json.Length;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BeginDownload(nint input)
    {
        try
        {
            var request = JsonSerializer.Deserialize<Request>(Str(input)) ?? throw new InvalidDataException();
            if (!request.Saved && (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password)))
                return -2;
            ulong? exact = null;
            if (!string.IsNullOrWhiteSpace(request.Manifest))
            {
                if (!ulong.TryParse(request.Manifest.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                    return -3;
                exact = id;
            }
            return Start("Download", new("auth", "Connecting to Steam…"),
                (payloads, ct) => Download(payloads, request, exact, ct)) ? 0 : -4;
        }
        catch
        {
            return -1;
        }
    }

    static async Task<State> Download(PayloadStore payloads, Request request, ulong? exact, CancellationToken ct)
    {
        await using var session = new SteamSession();
        if (request.Saved)
        {
            var saved = SteamVault.Load();
            if (saved is null) return new("error", "No Steam session saved. Sign in with your account.");
            await session.LogOnAsync(saved, true, ct);
        }
        else
        {
            var previous = request.Remember ? SteamVault.Load() : null;
            var username = request.Username.Trim();
            var guardData = string.Equals(previous?.AccountName, username, StringComparison.OrdinalIgnoreCase) ? previous?.GuardData : null;
            var identity = await session.AuthenticateAsync(username, request.Password, new Guard(ct), guardData, request.Remember, ct);
            if (request.Remember)
            {
                try { SteamVault.Save(identity); }
                catch (Exception error) { HostDiagnostics.Error("keychain", error); }
            }
        }

        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "public" : request.Branch.Trim();
        IReadOnlyList<SteamSession.GameBranch>? branches = null;
        if (branch == "?")
        {
            branches = await session.ListBranchesAsync(ct);
            var choices = branches.Where(b => b.ManifestId.HasValue || b.PasswordRequired)
                .Select(b => new Choice(b.Name, b.Name,
                    b.PasswordRequired ? "Password required" : $"Manifest {b.ManifestId}")).ToArray();
            if (choices.Length == 0) return new("error", "Steam returned no branches with a Windows depot manifest.");
            branch = await Ask("branch", "Select a Steam branch", ct, choices);
            if (!choices.Any(choice => choice.Value == branch)) throw new InvalidDataException("Invalid Steam branch selection.");
        }

        var beta = request.BetaPassword;
        if (string.IsNullOrEmpty(beta) && branch != "public")
        {
            branches ??= await session.ListBranchesAsync(ct);
            if (branches.Any(b => b.Name == branch && b.PasswordRequired))
                beta = await Ask("password", "Private beta password", ct);
        }

        var reporter = new DirectProgress<DownloadProgress>(p =>
        {
            var message = p.Stage switch
            {
                "resolve" => "Checking Steam ownership and manifest…",
                "download" => "Downloading game files…",
                "process" => "Verifying and installing the game…",
                _ => "Finishing installation…"
            };
            Publish(new(p.Stage, message, p.Completed, p.Total, p.BytesPerSecond, p.Remaining?.ToString(@"hh\:mm\:ss")));
        });
        var receipt = await session.DownloadAndInstallAsync(payloads, branch, exact, reporter, ct, beta);
        return SetCurrent(receipt, "Steam installation complete.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BeginImport(nint input)
    {
        var path = Str(input);
        if (!Path.IsPathFullyQualified(path)) return -2;
        return Start("Archive import", new("import", "Opening game ZIP…"), async (payloads, ct) =>
        {
            var receipt = await ArchiveImport.ImportZipAsync(payloads, path,
                (done, total) => Publish(new("import", "Importing game files…", done, total)), ct);
            return SetCurrent(receipt, "Archive installation complete.");
        }) ? 0 : -1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseVersions() => Start("Version selection", new("versions", "Reading installed versions…"),
        async (payloads, ct) =>
        {
            var installations = payloads.InstalledVersions();
            var choices = installations.Select(receipt => new Choice(receipt.PayloadId,
                receipt.DisplayName(), receipt.InventorySha256 is null ? "Reinstall required" :
                    receipt.PayloadId == current?.PayloadId ? "Current installation" :
                    receipt.DepotId == Payload.WindowsDepot ? "Steam installation" : "Imported archive")).ToArray();
            if (choices.Length == 0) return new("idle", "No installed versions found.");
            var selection = await Ask("version", "Installed versions", ct, choices);
            var selected = installations.SingleOrDefault(receipt => receipt.PayloadId == selection);
            if (selected is null) throw new InvalidDataException("Invalid installed version selection.");
            if (selected.InventorySha256 is null) return new("error", "Reinstall this version to verify all its game files.");
            if (selection == current?.PayloadId) return new("idle", "Selected version is already active.");
            Publish(new("verify", "Verifying selected installation…"));
            return SetCurrent(payloads.ActivateVersion(selection, ct), "Selected version is ready to play.");
        }) ? 0 : -1;

    static State SetCurrent(InstallReceipt receipt, string message)
    {
        lock (Sync) current = receipt;
        return new("installed", message);
    }

    static bool Start(string operation, State pending, Func<PayloadStore, CancellationToken, Task<State>> run)
    {
        CancellationTokenSource cts;
        PayloadStore payloads;
        lock (Sync)
        {
            if (active is not null || store is null || state.Stage == "ready") return false;
            payloads = store;
            active = cts = new();
            state = pending;
        }
        HostDiagnostics.Stage(pending.Stage);
        _ = Task.Run(async () =>
        {
            State result;
            try { result = await run(payloads, cts.Token); }
            catch (OperationCanceledException) { result = new("idle", "Operation cancelled."); }
            catch (Exception error)
            {
                HostDiagnostics.Error(operation, error);
                result = new("error", $"{operation} failed ({error.GetType().Name}).");
            }
            lock (Sync)
            {
                active = null;
                prompt = null;
                state = result;
            }
            cts.Dispose();
            HostDiagnostics.Stage(result.Stage);
        });
        return true;
    }

    static async Task<string> Ask(string kind, string message, CancellationToken ct, Choice[]? choices = null)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Sync)
        {
            prompt = tcs;
            state = new("prompt", message, Prompt: kind, Choices: choices, PromptId: ++promptId);
        }
        try { return await tcs.Task.WaitAsync(ct); }
        finally { lock (Sync) { if (prompt == tcs) prompt = null; } }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Reply(nint input, long id)
    {
        lock (Sync) return id == promptId && prompt?.TrySetResult(Str(input)) == true ? 0 : -1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ForgetSession()
    {
        lock (Sync)
        {
            if (active is not null) return -1;
            try { return SteamVault.Clear(); }
            catch { return -2; }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Cancel()
    {
        lock (Sync) { active?.Cancel(); return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Prepare() => Start("Launch preparation", new("verify", "Verifying installation…"),
        (payloads, ct) => Task.FromResult(PrepareLaunch(payloads, ct))) ? 0 : -1;

    static State PrepareLaunch(PayloadStore payloads, CancellationToken ct)
    {
        if (NativeExtensionsAvailable() != 1) return new("error", "Play requires an extension-enabled launcher build.");
        var plan = LaunchPlan.Prepare(payloads, documents, ct);
        var adapter = Path.Combine(Path.GetDirectoryName(payloads.Root)!, "RuntimeAdapters", plan.Receipt.PayloadId);
        Directory.CreateDirectory(adapter);
        var harmony = Path.Combine(plan.Assemblies, "0Harmony.dll");
        if (!File.Exists(harmony)) throw new FileNotFoundException("The verified game payload is missing Harmony.", harmony);
        Launcher.Adapters.Rewriter.Prepare("harmony", harmony, Path.Combine(managed, "STS2JitSupport.dll"), Path.Combine(adapter, "0Harmony.dll"));

        // Work on a copy so the versioned installation stays verifiable.
        var pack = Path.Combine(documents, "StS2.pck");
        var temporary = pack + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (NativeClone(plan.Pack, temporary, 0) != 0) File.Copy(plan.Pack, temporary);
            PckScripts.Add(temporary, plan.GameAssembly, ct);
            PckExtensions.Patch(temporary, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, pack, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        assemblyRoot = plan.Assemblies;
        adapterRoot = adapter;
        loadedGame = AssemblyLoadContext.Default.LoadFromAssemblyPath(plan.GameAssembly);
        releaseInfo = Path.Combine(Path.GetDirectoryName(plan.Pack)!, "release_info.json");
        return new("ready", "Game verified. Starting Godot…");
    }

    [DllImport("__Internal", EntryPoint = "sts2_extensions_available")]
    static extern int NativeExtensionsAvailable();

    [DllImport("libSystem.B.dylib", EntryPoint = "clonefile", SetLastError = true)]
    static extern int NativeClone([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string target, int flags);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int StartGameHooks()
    {
        try
        {
            GameCompatibility.Apply(loadedGame ?? throw new InvalidOperationException(), documents,
                releaseInfo ?? throw new InvalidOperationException());
            return 0;
        }
        catch (Exception error)
        {
            HostDiagnostics.Error("game compatibility startup", error);
            return -1;
        }
    }

    sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    sealed class Guard(CancellationToken ct) : IAuthenticator
    {
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => Ask("code",
            previousCodeWasIncorrect ? "Incorrect Steam Guard code" : "Steam Guard mobile code", ct);

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => Ask("code",
            previousCodeWasIncorrect ? "Incorrect email code" : "Steam Guard email code", ct);

        public async Task<bool> AcceptDeviceConfirmationAsync() =>
            await Ask("confirm", "Approve in the Steam app or enter a code.", ct) == "approve";
    }
}
