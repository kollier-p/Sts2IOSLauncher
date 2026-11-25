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
    static LaunchProfiles? profiles;
    static LaunchProfile? activeProfile;
    static LaunchProfilePaths? launchProfilePaths;
    static string documents = "", managed = "";
    static CancellationTokenSource? active;
    static TaskCompletionSource<string>? prompt;
    static long promptId;
    static string? assemblyRoot, adapterRoot, releaseInfo;
    static Assembly? loadedGame;
    static InstallReceipt? current;
    static InstallReceipt? profileVersion;
    static State state = new("idle", "No game installed");

    sealed record Choice(string Value, string Title, string Detail);
    sealed record State(string Stage, string Message, long Completed = 0, long Total = 0, double Speed = 0,
        string? Eta = null, string? Prompt = null, Choice[]? Choices = null, long PromptId = 0,
        string? Installed = null, string? Profile = null);
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
            var support = Path.GetDirectoryName(store.Root)!;
            profiles = new LaunchProfiles(Path.Combine(support, "LaunchProfiles.json"), documents);
            try { activeProfile = profiles.Current(); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { HostDiagnostics.Error("read launch profiles", error); }
            AssemblyLoadContext.Default.Resolving += Resolve;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += NativeLibraries.Resolve;
            try
            {
                current = store.Current();
                profileVersion = activeProfile?.PayloadId is string boundId ? store.FindVersion(boundId) : null;
                Publish(activeProfile is null
                    ? new("error", "The active launch profile could not be read.")
                    : activeProfile.PayloadId is not null && profileVersion is null
                    ? new("error", "The pinned game version is unavailable. Choose another version for this profile.")
                    : new("idle", current is null ? "Install or import the game to get started." : "Ready to play."));
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
        lock (Sync) json = JsonSerializer.SerializeToUtf8Bytes(state with
        {
            Installed = (activeProfile?.PayloadId is null ? current : profileVersion)?.DisplayName(),
            Profile = activeProfile?.Name
        });
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
        return Start("Archive import", new("import", "Opening game archive…"), async (payloads, ct) =>
        {
            var receipt = await ArchiveImport.ImportAsync(payloads, path,
                (done, total) => Publish(new("import", "Importing game files…", done, total)), ct);
            return SetCurrent(receipt, "Archive installation complete.");
        }) ? 0 : -1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseVersions() => Start("Version selection", new("versions", "Reading installed versions…"),
        async (payloads, ct) =>
        {
            try { payloads.CompleteVersionRemovals(ct); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { HostDiagnostics.Error("finish removing version", error); }
            var installations = payloads.InstalledVersions();
            var choices = installations.Select(receipt => new Choice(receipt.PayloadId,
                receipt.DisplayName(), receipt.InventorySha256 is null ? "Reinstall required" :
                    receipt.PayloadId == current?.PayloadId ? "Current installation" :
                    receipt.DepotId == Payload.WindowsDepot ? "Steam installation" : "Imported archive")).ToArray();
            if (choices.Length == 0) return new("idle", "No installed versions found.");
            var selection = await Ask("version", "Installed versions", ct, choices);
            var selected = installations.SingleOrDefault(receipt => receipt.PayloadId == selection);
            if (selected is null) throw new InvalidDataException("Invalid installed version selection.");
            var actions = new List<Choice>();
            var isCurrent = selection == current?.PayloadId;
            if (!isCurrent && selected.InventorySha256 is not null)
                actions.Add(new("activate", "Set as global version", "Use this version for profiles without a binding"));
            if (activeProfile is { Id: not "default" } && selected.InventorySha256 is not null)
                actions.Add(new("bind", "Use for this launch profile", activeProfile.Name));
            if (!isCurrent && profiles?.List().Any(profile => profile.PayloadId == selection) != true)
                actions.Add(new("remove", "Remove version", "Delete this inactive installation"));
            if (actions.Count == 0) return new("idle", "This version is in use.");
            var action = await Ask("version-action", selected.DisplayName(), ct, actions.ToArray());
            if (!actions.Any(choice => choice.Value == action)) throw new InvalidDataException("Invalid version action.");
            if (action == "bind" && activeProfile is { Id: not "default" } && selected.InventorySha256 is not null)
            {
                ct.ThrowIfCancellationRequested();
                SetProfile(profiles!.BindVersion(activeProfile.Id, selection), selected);
                return new("idle", "Game version bound to the active launch profile.");
            }
            if (action == "remove")
            {
                var confirmation = await Ask("remove-version", "Remove this game version?", ct,
                    [new("keep", "Keep version", "Return to the launcher"), new("remove", "Remove version", selected.DisplayName())]);
                if (confirmation != "remove") return new("idle", "Version kept.");
                Publish(new("remove", "Removing the inactive installation…"));
                payloads.RemoveVersion(selection, ct);
                return new("idle", "Inactive version removed.");
            }
            if (action != "activate" || selected.InventorySha256 is null)
                throw new InvalidDataException("Invalid version action.");
            Publish(new("verify", "Verifying selected installation…"));
            return SetCurrent(payloads.ActivateVersion(selection, ct), "Global game version selected.");
        }) ? 0 : -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseProfiles() => Start("Launch profile", new("profiles", "Reading launch profiles…"),
        async (payloads, ct) =>
        {
            var stored = profiles ?? throw new InvalidOperationException("Launch profiles are unavailable.");
            var existing = stored.List();
            var choices = existing.Select(profile => new Choice(profile.Id, profile.Name,
                (profile.Id == activeProfile?.Id ? "Active · " : "") +
                (profile.IsolateSaves ? "Isolated saves" : "Shared saves") + " · " +
                (profile.IsolateMods ? "Isolated mods" : "Shared mods") +
                (profile.PayloadId is null ? "" : " · Pinned version"))).ToList();
            choices.Add(new("new", "New profile", "Choose which data to isolate"));
            if (activeProfile?.PayloadId is not null)
                choices.Add(new("unpin", "Follow the global version", activeProfile.Name));
            var selection = await Ask("profile", "Launch profiles", ct, choices.ToArray());
            LaunchProfile profile;
            if (selection == "new")
            {
                var name = (await Ask("profile-name", "Name the launch profile", ct)).Trim();
                if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
                    return new("error", "Enter a profile name between 1 and 64 characters.");
                if (existing.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return new("error", "A profile with that name already exists.");
                var isolation = await Ask("profile-isolation", "Choose what to isolate", ct,
                    [new("both", "Saves and mods", "Separate both directories for this profile"),
                     new("saves", "Saves only", "Share mods with the default profile"),
                     new("mods", "Mods only", "Use the game's existing saves")]);
                if (isolation is not ("both" or "saves" or "mods")) throw new InvalidDataException("Invalid profile mode.");
                ct.ThrowIfCancellationRequested();
                profile = stored.Create(name, isolation != "mods", isolation != "saves");
            }
            else if (selection == "unpin" && activeProfile?.PayloadId is not null)
            {
                ct.ThrowIfCancellationRequested();
                profile = stored.BindVersion(activeProfile.Id, null);
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                profile = stored.Select(selection);
            }
            var bound = profile.PayloadId is string boundId ? payloads.FindVersion(boundId) : null;
            SetProfile(profile, bound);
            return profile.PayloadId is not null && bound is null
                ? new("error", "The pinned game version is unavailable. Choose a version or follow the global version.")
                : new("idle", $"Launch profile selected: {profile.Name}.");
        }) ? 0 : -1;

    static void SetProfile(LaunchProfile profile, InstallReceipt? version)
    {
        lock (Sync)
        {
            activeProfile = profile;
            profileVersion = version;
        }
    }

    static State SetCurrent(InstallReceipt receipt, string message)
    {
        lock (Sync) current = receipt;
        return new("installed", message);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseSnapshots() => Start("Save snapshots", new("snapshots", "Reading save snapshots…"),
        async (payloads, ct) =>
        {
            var profile = activeProfile ?? throw new InvalidOperationException("Select a launch profile first.");
            var saveDirectory = profiles!.SnapshotSaveDirectory(profile);
            var support = Path.GetDirectoryName(payloads.Root)!;
            var snapshotDirectory = Payload.SafePath(support, $"SaveSnapshots/{(profile.IsolateSaves ? profile.Id : "shared")}");
            var snapshots = new SaveSnapshots(saveDirectory, snapshotDirectory);
            var list = snapshots.List();
            var choices = new List<Choice> { new("new", "Create snapshot", "Back up the current saves and settings") };
            choices.AddRange(list.Select(backup => new Choice(backup.Id,
                backup.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), $"{backup.Reason} · {backup.Files} files")));
            var selection = await Ask("snapshot", "Save snapshots", ct, choices.ToArray());
            if (selection == "new")
            {
                Publish(new("snapshots", "Backing up saves…"));
                return snapshots.Create("Manual snapshot", ct) is null
                    ? new("idle", "There are no saves in this profile to back up.")
                    : new("idle", "Save snapshot created.");
            }
            if (!list.Any(backup => backup.Id == selection)) throw new InvalidDataException("Invalid snapshot selection.");
            var action = await Ask("snapshot-restore", "Restore this save snapshot?", ct,
                [new("keep", "Keep current saves", "Return to the launcher"),
                 new("restore", "Back up and restore", "Save the current state before restoring this snapshot")]);
            if (action != "restore") return new("idle", "Current saves kept.");
            Publish(new("snapshots", "Verifying and restoring saves…"));
            snapshots.Restore(selection, ct);
            return new("idle", "Save snapshot restored.");
        }) ? 0 : -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseWebDav() => Start("WebDAV", new("webdav", "Opening WebDAV settings…"),
        async (payloads, ct) =>
        {
            var profile = activeProfile ?? throw new InvalidOperationException("Select a launch profile first.");
            var settings = WebDavVault.Load();
            var choices = new List<Choice> { new("configure", "Configure connection", "Enter a WebDAV URL and remote save slot") };
            if (settings is not null)
            {
                choices.Add(new("test", "Test connection", $"Remote slot {settings.Slot}"));
                choices.Add(new("pull", "Download saves", "Back up and apply remote changes"));
                choices.Add(new("push", "Upload saves", "Send local save changes"));
                choices.Add(new("forget", "Forget WebDAV connection", "Remove the credentials from Keychain"));
            }
            var action = await Ask("webdav-action", "WebDAV save sync", ct, choices.ToArray());
            if (action == "configure")
            {
                var endpoint = await Ask("webdav-url", "Enter the HTTPS WebDAV collection URL", ct);
                var user = await Ask("webdav-user", "Enter the WebDAV username", ct);
                var password = await Ask("webdav-password", "Enter the WebDAV password", ct);
                var slot = (await Ask("webdav-slot", "Enter the remote save slot", ct)).Trim();
                if (slot.Length == 0) slot = "global";
                var syncSettings = await Ask("webdav-settings", "Include game settings in sync?", ct,
                    [new("no", "Saves only", "Keep device settings local"), new("yes", "Include settings", "Sync settings.save along with saves")]);
                var updated = new WebDavSettings(endpoint, user.Trim(), password, slot, syncSettings == "yes");
                try { updated.BaseUri(); }
                catch (ArgumentException error) { return new("error", error.Message); }
                ct.ThrowIfCancellationRequested();
                WebDavVault.Save(updated);
                return new("idle", "WebDAV connection saved.");
            }
            if (settings is null) throw new InvalidOperationException("A WebDAV connection is required.");
            if (action == "forget")
            {
                if (WebDavVault.Clear() != 0) throw new IOException("Unable to clear the WebDAV credentials.");
                return new("idle", "WebDAV connection forgotten.");
            }

            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
            var cloud = new WebDavSaves(http, settings);
            if (action == "test")
            {
                Publish(new("webdav", "Testing WebDAV connection…"));
                await cloud.TestConnectionAsync(ct);
                return new("idle", "WebDAV connection succeeded.");
            }
            if (action is not ("pull" or "push")) throw new InvalidDataException("Invalid WebDAV action.");
            var upload = action == "push";
            var profileKey = profile.IsolateSaves ? profile.Id : "shared";
            var support = Path.GetDirectoryName(payloads.Root)!;
            var saveDirectory = profiles!.SnapshotSaveDirectory(profile);
            var snapshots = Payload.SafePath(support, $"SaveSnapshots/{profileKey}");
            var baseline = Payload.SafePath(support, "WebDavBaselines");
            Publish(new("webdav", "Comparing local and remote saves…"));
            SaveSyncResult result;
            try
            {
                result = await cloud.SyncAsync(saveDirectory, snapshots, baseline, profileKey, upload, false, ct);
            }
            catch (SaveSyncConflictException conflict)
            {
                var preferred = upload ? "Use iPad saves" : "Use WebDAV saves";
                var resolution = await Ask("webdav-conflict", $"{conflict.Count} save files conflict", ct,
                    [new("cancel", "Keep both copies", "Return without syncing"),
                     new("replace", preferred, upload ? "Replace conflicting files on the server" : "Back up local saves before replacing them")]);
                if (resolution != "replace") return new("idle", "WebDAV changes kept on both sides.");
                Publish(new("webdav", "Applying the selected WebDAV changes…"));
                result = await cloud.SyncAsync(saveDirectory, snapshots, baseline, profileKey, upload, true, ct);
            }
            return new("idle", upload ? $"Uploaded {result.Transferred} save files." : $"Downloaded {result.Transferred} save files.");
        }) ? 0 : -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseSteamCloud() => Start("Steam Cloud", new("cloud", "Opening Steam Cloud…"),
        async (payloads, ct) =>
        {
            var profile = activeProfile ?? throw new InvalidOperationException("Select a launch profile first.");
            var action = await Ask("steam-cloud", "Steam Cloud", ct,
                [new("pull", "Download saves", "Back up and apply remote changes"),
                 new("push", "Upload saves", "Send local save changes to Steam")]);
            if (action is not ("pull" or "push")) throw new InvalidDataException("Invalid Steam Cloud action.");
            var settings = await Ask("steam-cloud-settings", "Include game settings in sync?", ct,
                [new("no", "Saves only", "Keep device settings local"), new("yes", "Include settings", "Sync settings.save along with saves")]);
            var saved = SteamVault.Load();
            var user = saved is null ? (await Ask("steam-user", "Enter your Steam account name", ct)).Trim() : saved.AccountName;
            var password = saved is null ? await Ask("steam-password", "Enter your Steam password", ct) : "";
            if (user.Length == 0 || (saved is null && password.Length == 0)) return new("error", "Enter your Steam account name and password.");
            Publish(new("auth", "Connecting to Steam Cloud…"));
            await using var session = new SteamSession();
            if (saved is null) await session.AuthenticateAsync(user, password, new Guard(ct), null, false, ct);
            else await session.LogOnAsync(saved, true, ct);

            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
            var cloud = session.OpenCloud(http);
            var profileKey = profile.IsolateSaves ? profile.Id : "shared";
            var support = Path.GetDirectoryName(payloads.Root)!;
            var saveDirectory = profiles!.SnapshotSaveDirectory(profile);
            var snapshots = Payload.SafePath(support, $"SaveSnapshots/{profileKey}");
            var history = Payload.SafePath(support, "SteamCloudBaselines");
            var upload = action == "push";
            Publish(new("cloud", "Comparing local and Steam Cloud saves…"));
            SaveSyncResult result;
            try
            {
                result = await cloud.SyncAsync(saveDirectory, snapshots, history, profileKey, settings == "yes", upload, false, ct);
            }
            catch (SaveSyncConflictException conflict)
            {
                var resolution = await Ask("steam-cloud-conflict", $"{conflict.Count} save files conflict", ct,
                    [new("cancel", "Keep both copies", "Return without syncing"),
                     new("replace", upload ? "Use iPad saves" : "Use Steam Cloud saves",
                         upload ? "Replace conflicting files in Steam Cloud" : "Back up local saves before replacing them")]);
                if (resolution != "replace") return new("idle", "Steam Cloud changes kept on both sides.");
                Publish(new("cloud", "Applying the selected Steam Cloud changes…"));
                result = await cloud.SyncAsync(saveDirectory, snapshots, history, profileKey, settings == "yes", upload, true, ct);
            }
            return new("idle", upload ? $"Uploaded {result.Transferred} save files to Steam Cloud." : $"Downloaded {result.Transferred} Steam Cloud save files.");
        }) ? 0 : -1;

    static ModLibrary ActiveMods()
    {
        var profile = activeProfile ?? throw new InvalidOperationException("Select a launch profile first.");
        return new ModLibrary(profiles!.EnsureDirectories(profile).ModsDirectory);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BeginModImport(nint input)
    {
        var path = Str(input);
        if (!Path.IsPathFullyQualified(path)) return -2;
        return Start("Mod import", new("mods", "Checking mod archive…"),
            (_, ct) => ImportModAsync(ActiveMods(), path, ct)) ? 0 : -1;
    }

    static async Task<State> ImportModAsync(ModLibrary library, string path, CancellationToken ct, string? label = null)
    {
        using var incoming = await library.PrepareArchiveAsync(path, ct, label);
        return await InstallModAsync(library, incoming, ct);
    }

    static async Task<State> InstallModAsync(ModLibrary library, ModLibrary.PreparedImport incoming, CancellationToken ct)
    {
        var previous = library.Replacement(incoming);
        if (previous is not null)
        {
            var decision = await Ask("mod-replacement", "Replace the installed mod package?", ct,
                [new("keep", "Keep installed package", previous.Label), new("replace", "Install replacement", incoming.Label)]);
            if (decision != "replace") return new("idle", "Installed mod package kept.");
        }
        Publish(new("mods", "Installing mod package…"));
        var installed = library.Install(incoming, previous is not null, ct);
        return new("idle", $"Installed {installed.Label}.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseMods() => Start("Mod management", new("mods", "Reading installed mods…"),
        async (_, ct) =>
        {
            var library = ActiveMods();
            var installed = library.List();
            var choices = installed.Select(mod => new Choice(mod.Key, mod.Label,
                $"{(mod.Enabled ? "Enabled" : "Disabled")} · {string.Join(", ", mod.ModuleIds)}")).ToArray();
            if (choices.Length == 0) return new("idle", "Import a mod archive to get started.");
            var selected = await Ask("mod", "Installed mod packages", ct, choices);
            var mod = installed.SingleOrDefault(item => item.Key == selected)
                ?? throw new InvalidDataException("The selected mod is unavailable.");
            var action = await Ask("mod-action", mod.Label, ct,
                [new("toggle", mod.Enabled ? "Disable package" : "Enable package", "Apply to the selected profile's mod directory"),
                 new("remove", "Remove package", "Delete the files installed with this package")]);
            ct.ThrowIfCancellationRequested();
            if (action == "toggle")
            {
                library.SetEnabled(selected, !mod.Enabled);
                return new("idle", mod.Enabled ? "Mod package disabled." : "Mod package enabled.");
            }
            if (action != "remove") throw new InvalidDataException("Invalid mod action.");
            var confirmation = await Ask("remove-mod", "Remove this mod package?", ct,
                [new("keep", "Keep package", "Return to the launcher"), new("remove", "Remove package", mod.Label)]);
            if (confirmation != "remove") return new("idle", "Mod package kept.");
            ct.ThrowIfCancellationRequested();
            library.Remove(selected);
            return new("idle", "Mod package removed.");
        }) ? 0 : -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseWorkshop() => Start("Steam Workshop", new("mods", "Opening Steam Workshop…"),
        async (_, ct) =>
        {
            var input = await Ask("workshop-id", "Enter a Workshop item ID or Steam Community link", ct);
            var id = WorkshopFiles.ParseId(input);
            if (id is null) return new("error", "Enter a Steam Workshop item ID or link.");
            Publish(new("mods", "Reading Workshop item…"));
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
            var workshop = new WorkshopFiles(http);
            var item = await workshop.LookupAsync(id.Value, ct);
            var choices = new List<Choice>();
            if (item.Download is not null) choices.Add(new("direct", "Import public download", $"Workshop item {item.Id}"));
            choices.Add(new("steampipe", "Choose a Steam Workshop version", "Sign in to view available SteamPipe manifests"));
            var action = await Ask("workshop-install", item.Title, ct,
                choices.ToArray());
            if (action == "steampipe") return await WorkshopSteamPipeAsync(item, ct);
            if (action != "direct" || item.Download is null) return new("idle", "Workshop import cancelled.");
            var staging = Directory.CreateTempSubdirectory("sts2-workshop-").FullName;
            try
            {
                var path = Path.Combine(staging, $"{item.Id}.archive");
                Publish(new("mods", "Downloading Workshop archive…"));
                await workshop.DownloadAsync(item, path, ct);
                return await ImportModAsync(ActiveMods(), path, ct, item.Title);
            }
            finally { Directory.Delete(staging, recursive: true); }
        }) ? 0 : -1;

    static async Task<State> WorkshopSteamPipeAsync(WorkshopItem item, CancellationToken ct)
    {
        var saved = SteamVault.Load();
        var user = saved is null ? (await Ask("steam-user", "Enter your Steam account name", ct)).Trim() : saved.AccountName;
        var password = saved is null ? await Ask("steam-password", "Enter your Steam password", ct) : "";
        if (user.Length == 0 || (saved is null && password.Length == 0)) return new("error", "Enter your Steam account name and password.");
        Publish(new("auth", "Signing in to Steam Workshop…"));
        await using var session = new SteamSession();
        if (saved is null) await session.AuthenticateAsync(user, password, new Guard(ct), null, false, ct);
        else await session.LogOnAsync(saved, true, ct);
        Publish(new("mods", "Reading available Workshop versions…"));
        var manifests = (await session.WorkshopManifestsAsync(item, ct)).ToList();
        if (manifests.Count == 0) return new("error", "Steam returned no accessible Workshop versions.");
        var installed = activeProfile?.PayloadId is null ? current : profileVersion;
        if (installed?.DepotId == Payload.WindowsDepot)
        {
            var matching = WorkshopManifest.ResolveBranch(manifests, installed.Branch);
            if (matching is not null)
            {
                manifests.RemoveAll(manifest => manifest.Id == matching.Id && manifest.MinimumBranch == matching.MinimumBranch &&
                    manifest.MaximumBranch == matching.MaximumBranch);
                manifests.Insert(0, matching);
            }
            else if (installed.Branch != "public" && manifests.Find(manifest => manifest.Source == "Default manifest") is { } fallback)
            {
                manifests.Add(fallback with { Branch = installed.Branch, Source = "Default manifest requested for installed branch" });
            }
        }
        var choices = manifests.Select((manifest, index) => new Choice(index.ToString(CultureInfo.InvariantCulture),
            $"{manifest.Branch} · {manifest.Source}",
            manifest.MinimumBranch.Length == 0 && manifest.MaximumBranch.Length == 0 ? $"{manifest.Id}" :
                $"{manifest.Id} · {manifest.MinimumBranch} to {manifest.MaximumBranch}")).ToArray();
        var selected = await Ask("workshop-manifest", item.Title, ct, choices);
        if (!int.TryParse(selected, NumberStyles.None, CultureInfo.InvariantCulture, out var selection) || selection >= manifests.Count)
            return new("error", "Choose an available Workshop version.");
        var library = ActiveMods();
        var progress = new DirectProgress<DownloadProgress>(value =>
            Publish(new("mods", "Downloading Workshop files…", value.Completed, value.Total, value.BytesPerSecond,
                value.Remaining?.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture))));
        Publish(new("mods", "Requesting Workshop files from Steam…"));
        using var incoming = await library.PrepareDownloadAsync(item.Title,
            (stage, token) => session.DownloadWorkshopManifestAsync(manifests[selection], stage, progress, token), ct);
        return await InstallModAsync(library, incoming, ct);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseNexus() => Start("Nexus Mods", new("mods", "Opening Nexus Mods…"), BrowseNexusAsync) ? 0 : -1;

    static async Task<State> BrowseNexusAsync(PayloadStore _, CancellationToken ct)
    {
        var key = NexusVault.Load();
        var actions = new List<Choice>();
        if (key is not null)
        {
            actions.Add(new("browse", "Browse or look up a mod", "Browse recent and trending mods or enter a mod page link"));
            actions.Add(new("authorized", "Use a Nexus download link", "Paste the link from a requested mod manager download"));
        }
        actions.Add(new("configure", key is null ? "Set API key" : "Replace API key", "Save a personal Nexus API key in Keychain"));
        if (key is not null) actions.Add(new("forget", "Forget API key", "Remove the saved Nexus key"));
        var action = await Ask("nexus-action", "Nexus Mods", ct, actions.ToArray());
        if (action == "forget")
            return NexusVault.Clear() == 0 ? new("idle", "Nexus API key removed.") : new("error", "Could not remove the Nexus API key.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
        if (action == "configure")
        {
            key = (await Ask("nexus-key", "Enter your personal Nexus Mods API key", ct)).Trim();
            if (key.Length is 0 or > 2048 || key.Any(character => character <= ' ' || character > '~'))
                return new("error", "Enter a valid Nexus API key.");
            Publish(new("mods", "Validating your Nexus API key…"));
            try { await new NexusMods(http, key).ValidateAsync(ct); }
            catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            { return new("error", "Nexus rejected this API key."); }
            NexusVault.Save(key);
            return new("idle", "Nexus API key saved. Open Nexus Mods to browse or import a download link.");
        }
        if (key is null) return new("error", "Set your Nexus Mods API key first.");
        var nexus = new NexusMods(http, key);
        NexusMod mod;
        NexusModFile file;
        NexusDownloadToken? authorization = null;
        if (action == "authorized")
        {
            authorization = NexusMods.ParseNxm(await Ask("nexus-link", "Paste your authorized nxm download link", ct));
            if (authorization is null) return new("error", "Enter an authorized Slay the Spire 2 Nexus download link.");
            Publish(new("mods", "Reading Nexus file details…"));
            mod = await nexus.LookupAsync(authorization.ModId, ct);
            file = (await nexus.FilesAsync(mod.Id, ct)).SingleOrDefault(item => item.Id == authorization.FileId)
                ?? throw new InvalidDataException("Nexus no longer lists this file.");
        }
        else if (action == "browse")
        {
            var query = (await Ask("nexus-query", "Enter a mod name, ID, or page link. Leave blank to browse recent and trending mods.", ct)).Trim();
            Publish(new("mods", "Reading Nexus Mods catalog…"));
            var matches = await nexus.SearchAsync(query, ct);
            if (matches.Count == 0) return new("idle", "No recent or trending mods match. Look up a mod by its page link to search the full catalog.");
            var selected = await Ask("nexus-mod", "Choose a Nexus mod", ct,
                matches.Select(item => new Choice(item.Id.ToString(CultureInfo.InvariantCulture), item.Name, item.Summary)).ToArray());
            mod = matches.Single(item => item.Id.ToString(CultureInfo.InvariantCulture) == selected);
            Publish(new("mods", "Reading Nexus file details…"));
            var files = await nexus.FilesAsync(mod.Id, ct);
            if (files.Count == 0) return new("idle", "Nexus has no files for this mod.");
            var selectedFile = await Ask("nexus-file", mod.Name, ct,
                files.Select(item => new Choice(item.Id.ToString(CultureInfo.InvariantCulture), item.Name, item.Version)).ToArray());
            file = files.Single(item => item.Id.ToString(CultureInfo.InvariantCulture) == selectedFile);
        }
        else return new("error", "Choose a Nexus action.");

        var confirmation = await Ask("nexus-install", "Download and import this Nexus file?", ct,
            [new("cancel", "Cancel", "Return to the launcher"), new("install", mod.Name, file.Name)]);
        if (confirmation != "install") return new("idle", "Nexus import cancelled.");
        var staging = Directory.CreateTempSubdirectory("sts2-nexus-").FullName;
        try
        {
            var path = Path.Combine(staging, $"{file.Id}.archive");
            Publish(new("mods", "Downloading Nexus archive…"));
            try { await nexus.DownloadAsync(mod.Id, file.Id, authorization, path, ct); }
            catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.Forbidden)
            { return new("error", "Nexus denied the request. Open the mod page and request a mod manager download link."); }
            return await ImportModAsync(ActiveMods(), path, ct, mod.Name);
        }
        finally { Directory.Delete(staging, recursive: true); }
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
            catch (InsufficientStorageException error)
            {
                const double MiB = 1024.0 * 1024;
                result = new("error", $"This operation needs {Math.Ceiling(error.RequiredBytes / MiB):N0} MiB of free space. {Math.Floor(error.AvailableBytes / MiB):N0} MiB is available.");
            }
            catch (NotSupportedException) when (operation is "Archive import" or "Mod import" or "Steam Workshop" or "Nexus Mods")
            {
                result = new("error", "Choose a ZIP, 7z, RAR, or supported TAR archive.");
            }
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
        if (activeProfile is null) return new("error", "Select a launch profile before starting the game.");
        var plan = LaunchPlan.Prepare(payloads, documents, ct, activeProfile.PayloadId);
        launchProfilePaths = profiles!.EnsureDirectories(activeProfile);
        new ModLibrary(launchProfilePaths.ModsDirectory).List();
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
            if (NativeClone(plan.Pack, temporary, 0) != 0)
            {
                PayloadStorage.EnsureCapacity(documents, new FileInfo(plan.Pack).Length);
                LaunchCopy.Copy(plan.Pack, temporary, ct);
            }
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
            GameCompatibility.Apply(loadedGame ?? throw new InvalidOperationException(),
                activeProfile ?? throw new InvalidOperationException(), launchProfilePaths ?? throw new InvalidOperationException(),
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

        public async Task<bool> AcceptDeviceConfirmationAsync()
        {
            if (await Ask("confirm", "Approve in the Steam app or enter a code.", ct) != "approve") return false;
            Publish(new("auth", "Waiting for Steam Guard approval…"));
            return true;
        }
    }
}
