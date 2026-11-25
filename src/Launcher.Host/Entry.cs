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
    static GameLaunchOptions launchOptions = new();
    static GameWebDavSync? gameWebDav;
    static string documents = "", managed = "";
    static CancellationTokenSource? active;
    static TaskCompletionSource<string>? prompt;
    static long promptId;
    static string? assemblyRoot, adapterRoot, releaseInfo;
    static Assembly? loadedGame;
    static InstallReceipt? current;
    static InstallReceipt? profileVersion;
    static State state = new("idle", "No game installed");

    sealed record Choice(string Value, string Title, string Detail, string? Thumbnail = null);
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
            try { LaunchCopy.RemoveInterruptedCopies(documents); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { HostDiagnostics.Error("recover launch copy", error); }
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
                .Select(b =>
                {
                    var manifest = b.PasswordRequired ? "Password required" : $"Manifest {b.ManifestId}";
                    var description = new string(b.Description.Where(character => !char.IsControl(character)).Take(160).ToArray());
                    return new Choice(b.Name, b.Name, description.Length == 0 ? manifest : $"{manifest}\n{description}");
                }).ToArray();
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
                var (isolateSaves, isolateMods) = await AskProfileIsolation(ct);
                ct.ThrowIfCancellationRequested();
                profile = stored.Create(name, isolateSaves, isolateMods);
            }
            else if (selection == "unpin" && activeProfile?.PayloadId is not null)
            {
                ct.ThrowIfCancellationRequested();
                profile = stored.BindVersion(activeProfile.Id, null);
            }
            else
            {
                var chosen = existing.SingleOrDefault(item => item.Id == selection) ?? throw new InvalidDataException("Launch profile not found.");
                if (chosen.Id != LaunchProfiles.Default.Id)
                {
                    var action = await Ask("profile-action", chosen.Name, ct,
                        [new("select", "Use this profile", "Select it for the next game launch"),
                         new("rename", "Rename", "Change the name in the launcher"),
                         new("folders", "Change save and mod folders", "Choose shared or isolated storage"),
                         new("remove", "Remove profile", "Remove the profile from the launcher")]);
                    if (action == "rename" || action == "folders")
                    {
                        var name = chosen.Name;
                        var saveIsolation = chosen.IsolateSaves;
                        var modIsolation = chosen.IsolateMods;
                        if (action == "rename")
                        {
                            name = (await Ask("profile-name", "Enter a new profile name", ct)).Trim();
                            if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
                                return new("error", "Enter a profile name between 1 and 64 characters.");
                            if (existing.Any(item => item.Id != chosen.Id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                                return new("error", "A profile with that name already exists.");
                        }
                        else (saveIsolation, modIsolation) = await AskProfileIsolation(ct);
                        ct.ThrowIfCancellationRequested();
                        var updated = stored.Edit(chosen.Id, name, saveIsolation, modIsolation);
                        if (activeProfile?.Id == updated.Id) SetProfile(updated, profileVersion);
                        return new("idle", $"Updated {updated.Name}.");
                    }
                    if (action == "remove")
                    {
                        var confirm = await Ask("profile-remove", $"Remove {chosen.Name}?", ct,
                            [new("keep", "Keep profile", "Return to the launcher"),
                             new("remove", "Remove profile", "Keep its files in Launcher files")]);
                        if (confirm != "remove") return new("idle", "Launch profile kept.");
                        ct.ThrowIfCancellationRequested();
                        var remaining = stored.Remove(chosen.Id);
                        SetProfile(remaining, remaining.PayloadId is string pin ? payloads.FindVersion(pin) : null);
                        return new("idle", $"Removed {chosen.Name}. Active profile is {remaining.Name}.");
                    }
                    if (action != "select") throw new InvalidDataException("Choose a launch profile action.");
                }
                ct.ThrowIfCancellationRequested();
                profile = stored.Select(selection);
            }
            var bound = profile.PayloadId is string boundId ? payloads.FindVersion(boundId) : null;
            SetProfile(profile, bound);
            return profile.PayloadId is not null && bound is null
                ? new("error", "The pinned game version is unavailable. Choose a version or follow the global version.")
                : new("idle", $"Launch profile selected: {profile.Name}.");
        }) ? 0 : -1;

    static async Task<(bool Saves, bool Mods)> AskProfileIsolation(CancellationToken ct)
    {
        var isolation = await Ask("profile-isolation", "Choose what to isolate", ct,
            [new("both", "Saves and mods", "Separate both directories for this profile"),
             new("saves", "Saves only", "Use shared mods"),
             new("mods", "Mods only", "Use shared saves"),
             new("shared", "Shared saves and mods", "Use the default folders")]);
        return isolation switch
        {
            "both" => (true, true),
            "saves" => (true, false),
            "mods" => (false, true),
            "shared" => (false, false),
            _ => throw new InvalidDataException("Invalid profile mode.")
        };
    }

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

    static GameLaunchOptionsStore OptionsStore(PayloadStore payloads) =>
        new(Payload.SafePath(Path.GetDirectoryName(payloads.Root)!, "GameLaunchOptions.json"));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseLaunchOptions() => Start("Launch settings", new("settings", "Opening launch settings…"),
        async (payloads, ct) =>
        {
            var options = OptionsStore(payloads);
            var currentOptions = options.Load();
            var action = await Ask("launch-options", "Game launch settings", ct,
                [new("fps", "Frame rate limit", currentOptions.FrameLimit switch
                    { null => "Game setting", 0 => "Uncapped", int fps => $"{fps} FPS" }),
                 new("touch", "Touch as mouse", currentOptions.TouchAsMouse switch
                    { null => "Game setting", true => "On", false => "Off" }),
                 new("reset", "Restore game defaults", "Use the installed game's options")]);
            if (action == "fps")
            {
                var value = await Ask("launch-fps", "Frame rate limit", ct,
                    [new("default", "Game setting", ""), new("30", "30 FPS", ""),
                     new("60", "60 FPS", ""), new("120", "120 FPS", ""), new("0", "Uncapped", "")]);
                currentOptions = currentOptions with
                {
                    FrameLimit = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var fps) ? fps : null
                };
            }
            else if (action == "touch")
            {
                var value = await Ask("launch-touch", "Touch as mouse", ct,
                    [new("default", "Game setting", ""), new("yes", "On", "Send mouse clicks for touch input"),
                     new("no", "Off", "Send touch input directly")]);
                currentOptions = currentOptions with { TouchAsMouse = value == "yes" ? true : value == "no" ? false : null };
            }
            else if (action == "reset") currentOptions = new();
            else throw new InvalidDataException("Choose a launch setting.");
            ct.ThrowIfCancellationRequested();
            options.Save(currentOptions);
            return new("idle", "Game launch settings saved.");
        }) ? 0 : -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseSnapshots() => Start("Save snapshots", new("snapshots", "Reading save snapshots…"),
        async (payloads, ct) =>
        {
            var snapshots = ActiveSnapshots(payloads);
            var list = snapshots.List();
            var choices = new List<Choice> { new("new", "Create snapshot", "Back up the current saves and settings") };
            choices.Add(new("transfer", "Transfer a save slot", "Copy between normal and modded saves"));
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
            if (selection == "transfer") return await TransferSaveSlotAsync(snapshots, ct);
            if (!list.Any(backup => backup.Id == selection)) throw new InvalidDataException("Invalid snapshot selection.");
            var action = await Ask("snapshot-action", "Save snapshot", ct,
                [new("keep", "Keep current saves", "Return to the launcher"),
                 new("export", "Export ZIP", "Save an Android-compatible snapshot in Launcher files"),
                 new("restore", "Back up and restore", "Save the current state before restoring this snapshot")]);
            if (action == "export")
            {
                Publish(new("snapshots", "Verifying and exporting saves…"));
                var destination = snapshots.ExportArchive(selection, Payload.SafePath(documents, "SaveExports"), ct);
                return new("idle", $"Exported {Path.GetFileName(destination)} to Launcher files → SaveExports.");
            }
            if (action != "restore") return new("idle", "Current saves kept.");
            Publish(new("snapshots", "Verifying and restoring saves…"));
            snapshots.Restore(selection, ct);
            return new("idle", "Save snapshot restored.");
        }) ? 0 : -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BeginSnapshotImport(nint input)
    {
        var path = Str(input);
        if (!Path.IsPathFullyQualified(path)) return -2;
        return Start("Save snapshot import", new("snapshots", "Verifying save snapshot ZIP…"), (payloads, ct) =>
        {
            var imported = ActiveSnapshots(payloads).ImportArchive(path, ct);
            return Task.FromResult(new State("idle", $"Imported a snapshot with {imported.Files} files. Select it in Snapshots and save slots to restore."));
        }) ? 0 : -1;
    }

    static SaveSnapshots ActiveSnapshots(PayloadStore payloads)
    {
        var profile = activeProfile ?? throw new InvalidOperationException("Select a launch profile first.");
        var saves = profiles!.SnapshotSaveDirectory(profile);
        var support = Path.GetDirectoryName(payloads.Root)!;
        var backups = Payload.SafePath(support, $"SaveSnapshots/{(profile.IsolateSaves ? profile.Id : "shared")}");
        return new SaveSnapshots(saves, backups);
    }

    static async Task<State> TransferSaveSlotAsync(SaveSnapshots snapshots, CancellationToken ct)
    {
        var direction = await Ask("save-transfer", "Transfer a save slot", ct,
            [new("modded", "Normal to modded", "Copy a normal save slot into the modded save tree"),
             new("normal", "Modded to normal", "Copy a modded save slot into the normal save tree")]);
        if (direction is not ("modded" or "normal")) throw new InvalidDataException("Invalid save transfer direction.");
        var selection = await Ask("save-transfer-slot", "Choose the slot to copy", ct,
            [new("1", "Profile 1", ""), new("2", "Profile 2", ""), new("3", "Profile 3", "")]);
        if (!int.TryParse(selection, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot is < 1 or > 3)
            throw new InvalidDataException("Invalid save slot.");
        var destination = direction == "modded" ? "modded" : "normal";
        var action = await Ask("save-transfer-confirm", $"Replace {destination} save slot {slot}?", ct,
            [new("keep", "Keep current saves", "Return to the launcher"),
             new("transfer", "Back up and replace", "The copied save needs its original game version and compatible mods")]);
        if (action != "transfer") return new("idle", "Save slots kept.");
        Publish(new("snapshots", "Backing up and copying the selected save slot…"));
        var count = snapshots.TransferSlot(slot, direction == "modded", ct);
        return count == 0 ? new("idle", "The selected source slot has no saves.") :
            new("idle", $"Copied {count} files into {destination} slot {slot}.");
    }

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
                choices.Add(new("automation", "Automatic sync", settings.Automation switch
                {
                    WebDavAutomationMode.PullBeforePlay => "Download before playing",
                    WebDavAutomationMode.SyncBeforePlay => "Sync when starting the game",
                    WebDavAutomationMode.SyncInBackground => "Sync before play and in the background",
                    _ => "Manual"
                }));
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
            if (action == "automation")
            {
                var mode = await Ask("webdav-automation", "Automatic WebDAV sync", ct,
                    [new("manual", "Manual", "Sync only from the Saves page"),
                     new("pull", "Download before playing", "Check for new remote saves before launching"),
                     new("sync", "Sync before playing", "Upload saves from the last session, then check for remote changes"),
                     new("background", "Sync before play and in the background", "Try to upload when the game moves to the background")]);
                ct.ThrowIfCancellationRequested();
                WebDavVault.Save(settings with { Automation = mode switch
                {
                    "pull" => WebDavAutomationMode.PullBeforePlay,
                    "sync" => WebDavAutomationMode.SyncBeforePlay,
                    "background" => WebDavAutomationMode.SyncInBackground,
                    _ => WebDavAutomationMode.Manual
                } });
                return new("idle", "WebDAV sync mode saved.");
            }
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

    static async Task<State> ImportModAsync(ModLibrary library, string path, CancellationToken ct, string? label = null,
        WorkshopOrigin? workshop = null)
    {
        using var incoming = await library.PrepareArchiveAsync(path, ct, label, workshop);
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
            var paused = library.LoadingPaused;
            var choices = new List<Choice> { new("loading", paused ? "Resume mod loading" : "Pause mod loading",
                "Applies to this mod folder at the next launch"),
                new("presets", "Mod presets", "Save and switch package and module settings") };
            choices.AddRange(installed.Select(mod => new Choice(mod.Key, mod.Label,
                $"{(mod.Enabled ? "Enabled" : "Disabled")} · {string.Join(", ", mod.ModuleIds)}")));
            var selected = await Ask("mod", "Installed mod packages", ct, choices.ToArray());
            if (selected == "presets") return await BrowseModPresetsAsync(library, ct);
            if (selected == "loading")
            {
                ct.ThrowIfCancellationRequested();
                library.SetLoadingPaused(!paused);
                return new("idle", paused ? "Mod loading will resume at next launch." : "Mod loading is paused.");
            }
            var mod = installed.SingleOrDefault(item => item.Key == selected)
                ?? throw new InvalidDataException("The selected mod is unavailable.");
            var action = await Ask("mod-action", mod.Label, ct,
                [new("toggle", mod.Enabled ? "Disable package" : "Enable package", "Apply to the selected profile's mod directory"),
                 new("modules", "Modules and dependencies", "Review versions and module status"),
                 new("remove", "Remove package", "Delete the files installed with this package")]);
            ct.ThrowIfCancellationRequested();
            if (action == "modules") return await BrowseModModulesAsync(library, mod, ct);
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

    static async Task<State> BrowseModPresetsAsync(ModLibrary library, CancellationToken ct)
    {
        var presets = library.Presets();
        var choices = new List<Choice> { new("create", "Save current settings", "Create a preset for this mod folder") };
        choices.AddRange(presets.Select(preset => new Choice(preset.Id, preset.Name, $"{preset.Packages} packages")));
        var selected = await Ask("mod-presets", "Mod presets", ct, choices.ToArray());
        if (selected == "create")
        {
            if (library.List().Count == 0) return new("idle", "Install a mod to create a preset.");
            var name = (await Ask("mod-preset-name", "Name this mod preset", ct)).Trim();
            if (name.Length is < 1 or > 64 || name.Any(char.IsControl))
                return new("error", "Enter a preset name between 1 and 64 characters.");
            if (presets.Any(preset => preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return new("error", "A preset with that name already exists.");
            ct.ThrowIfCancellationRequested();
            return new("idle", $"Saved mod preset {library.CreatePreset(name).Name}.");
        }
        var preset = presets.SingleOrDefault(item => item.Id == selected) ?? throw new InvalidDataException("Invalid mod preset.");
        var action = await Ask("mod-preset-action", preset.Name, ct,
            [new("apply", "Apply preset", "Apply the saved settings to packages in this mod folder"),
             new("remove", "Remove preset", "Delete this configuration"),
             new("keep", "Keep current settings", "Return to the launcher")]);
        if (action == "apply")
        {
            Publish(new("mods", "Applying mod preset…"));
            library.ApplyPreset(selected, ct);
            return new("idle", $"Applied {preset.Name}.");
        }
        if (action == "remove")
        {
            var confirm = await Ask("mod-preset-remove", $"Remove {preset.Name}?", ct,
                [new("keep", "Keep preset", ""), new("remove", "Remove preset", "")]);
            if (confirm == "remove")
            {
                ct.ThrowIfCancellationRequested();
                library.DeletePreset(selected);
                return new("idle", "Mod preset removed.");
            }
        }
        return new("idle", "Mod settings kept.");
    }

    static async Task<State> BrowseModModulesAsync(ModLibrary library, InstalledMod package, CancellationToken ct)
    {
        var modules = library.Modules(package.Key);
        if (modules.Count == 0) return new("idle", "Reimport this package to make module controls available.");
        var installed = library.EnabledModuleIds();
        var choices = modules.Select(item =>
        {
            var module = item.Module;
            var status = item.Enabled is null ? "Manifest unavailable" : item.Enabled == true ? "Enabled" : "Disabled";
            var missing = module.Dependencies.Count(dependency => !installed.Contains(dependency));
            if (missing != 0) status += $" · {missing} missing dependencies";
            return new Choice(module.Id, module.Id, string.Join(" · ", new[] { status, module.Version, module.Author }.Where(value => value.Length != 0)));
        }).ToArray();
        var selected = await Ask("mod-modules", package.Label, ct, choices);
        var selectedModule = modules.SingleOrDefault(item => item.Module.Id == selected)
            ?? throw new InvalidDataException("The selected mod module is unavailable.");
        var info = selectedModule.Module;
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(info.MinimumGameVersion)) lines.Add($"Minimum game version {info.MinimumGameVersion}");
        var dependencies = info.Dependencies.Select(dependency => installed.Contains(dependency) ? dependency : $"{dependency} (missing or disabled)");
        if (info.Dependencies.Length != 0) lines.Add(string.Join(", ", dependencies));
        if (selectedModule.Enabled is null) return new("error", "This mod module needs its original manifest. Reimport the package.");
        var action = await Ask("mod-module-toggle", info.Id, ct,
            [new("keep", "Keep current setting", "Return to the launcher"),
             new("toggle", selectedModule.Enabled == true ? "Disable module" : "Enable module",
                 lines.Count == 0 ? "Applies the next time the game starts" : string.Join("\n", lines))]);
        if (action != "toggle") return new("idle", "Module settings kept.");
        ct.ThrowIfCancellationRequested();
        library.SetModuleEnabled(package.Key, selected, selectedModule.Enabled == false);
        return new("idle", selectedModule.Enabled == true ? "Mod module disabled." : "Mod module enabled.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BrowseWorkshop() => Start("Steam Workshop", new("mods", "Opening Steam Workshop…"),
        BrowseWorkshopAsync) ? 0 : -1;

    static async Task<State> BrowseWorkshopAsync(PayloadStore _, CancellationToken ct)
    {
        var action = await Ask("workshop-home", "Steam Workshop", ct,
            [new("popular", "Popular", "Choose a time range"),
             new("latest", "New releases", "Browse recently published items"),
             new("updated", "Recent updates", "Browse recently updated items"),
             new("subscribed", "Most subscribed", "Browse by subscriptions"),
             new("search", "Search Workshop", "Enter a title, item ID, or link"),
             new("item", "Open item", "Enter an item ID or Steam Community link"),
             new("updates", "Installed Workshop items", "Check this profile for updates")]);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
        var workshop = new WorkshopFiles(http);
        if (action == "updates") return await BrowseWorkshopUpdatesAsync(workshop, ct);
        if (action == "item")
        {
            var input = await Ask("workshop-id", "Enter a Workshop item ID or Steam Community link", ct);
            return WorkshopFiles.ParseId(input) is ulong id ? await OpenWorkshopItemAsync(workshop, id, ct) :
                new("error", "Enter a Steam Workshop item ID or link.");
        }
        var query = action == "search" ? (await Ask("workshop-query", "Search Slay the Spire 2 Workshop", ct)).Trim() : "";
        if (WorkshopFiles.ParseId(query) is ulong requested) return await OpenWorkshopItemAsync(workshop, requested, ct);
        var sort = action switch
        {
            "latest" => WorkshopSort.Latest,
            "updated" => WorkshopSort.Updated,
            "subscribed" => WorkshopSort.Subscribed,
            _ => WorkshopSort.Popular
        };
        var days = 7;
        if (action == "popular")
        {
            var range = await Ask("workshop-range", "Popular Workshop items", ct,
                [new("7", "Past week", ""), new("30", "Past 30 days", ""), new("90", "Past 3 months", ""),
                 new("180", "Past 6 months", ""), new("365", "Past year", ""), new("-1", "All time", "")]);
            if (!int.TryParse(range, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out days))
                return new("error", "Choose a Workshop time range.");
        }
        var page = 1;
        while (true)
        {
            Publish(new("mods", "Loading Steam Workshop…"));
            var results = await workshop.BrowseAsync(sort, page, query, days, ct);
            if (results.Items.Count == 0) return new("idle", "Steam Workshop returned no matching items.");
            var choices = results.Items.Select(item => new Choice(item.Id.ToString(CultureInfo.InvariantCulture), item.Title, item.Summary,
                item.Thumbnail?.AbsoluteUri)).ToList();
            if (results.Index > 1) choices.Add(new("previous", "Previous page", ""));
            if (results.Index < results.Pages) choices.Add(new("next", "Next page", ""));
            var selection = await Ask("workshop-page", $"Workshop · Page {results.Index} of {results.Pages}", ct, choices.ToArray());
            if (selection == "previous") page = Math.Max(1, results.Index - 1);
            else if (selection == "next") page = checked(results.Index + 1);
            else if (WorkshopFiles.ParseId(selection) is ulong selected && results.Items.Any(item => item.Id == selected))
                return await OpenWorkshopItemAsync(workshop, selected, ct);
            else return new("idle", "Workshop browsing cancelled.");
        }
    }

    static async Task<State> OpenWorkshopItemAsync(WorkshopFiles workshop, ulong id, CancellationToken ct)
    {
        Publish(new("mods", "Reading Workshop item…"));
        var item = await workshop.LookupAsync(id, ct);
        return await InstallWorkshopItemAsync(workshop, item, null, ct);
    }

    static async Task<State> BrowseWorkshopUpdatesAsync(WorkshopFiles workshop, CancellationToken ct)
    {
        var installed = ActiveMods().List().Where(item => item.Workshop is not null).ToArray();
        if (installed.Length == 0) return new("idle", "Install a Workshop item to track updates.");
        Publish(new("mods", "Checking installed Workshop items…"));
        var remote = await workshop.LookupAsync(installed.Select(item => item.Workshop!.ItemId), ct);
        var choices = installed.Select(mod =>
        {
            var origin = mod.Workshop!;
            var status = !remote.TryGetValue(origin.ItemId, out var entry) ? "Unavailable" :
                entry.Updated > origin.Updated ? "Listing updated" : "Listing unchanged";
            return new Choice(mod.Key, mod.Label, $"{origin.Branch} · {status}");
        }).ToArray();
        var selected = await Ask("workshop-updates", "Installed Workshop items", ct, choices);
        var currentWorkshop = installed.SingleOrDefault(item => item.Key == selected)?.Workshop;
        if (currentWorkshop is null || !remote.TryGetValue(currentWorkshop.ItemId, out var item))
            return new("idle", "This Workshop item is unavailable.");
        return await InstallWorkshopItemAsync(workshop, item, currentWorkshop, ct);
    }

    static async Task<State> InstallWorkshopItemAsync(WorkshopFiles workshop, WorkshopItem item, WorkshopOrigin? previous,
        CancellationToken ct)
    {
        var choices = new List<Choice>();
        if (item.Download is not null && (previous is null || previous.Manifest is null))
            choices.Add(new("direct", "Import public download", $"Workshop item {item.Id}"));
        choices.Add(new("steampipe", "Choose a Steam Workshop version", "Sign in to view available SteamPipe manifests"));
        var action = await Ask("workshop-install", item.Title, ct, choices.ToArray());
        if (action == "steampipe") return await WorkshopSteamPipeAsync(item, previous?.Branch, ct);
        if (action != "direct" || item.Download is null) return new("idle", "Workshop import cancelled.");
        var staging = Directory.CreateTempSubdirectory("sts2-workshop-").FullName;
        try
        {
            var path = Path.Combine(staging, $"{item.Id}.archive");
            Publish(new("mods", "Downloading Workshop archive…"));
            await workshop.DownloadAsync(item, path, ct);
            return await ImportModAsync(ActiveMods(), path, ct, item.Title, new WorkshopOrigin(item.Id, "public", null, item.Updated));
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    static async Task<State> WorkshopSteamPipeAsync(WorkshopItem item, string? previousBranch, CancellationToken ct)
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
        var branch = previousBranch ?? (installed?.DepotId == Payload.WindowsDepot ? installed.Branch : null);
        if (!string.IsNullOrEmpty(branch))
        {
            var matching = WorkshopManifest.ResolveBranch(manifests, branch);
            if (matching is not null)
            {
                manifests.RemoveAll(manifest => manifest.Id == matching.Id && manifest.MinimumBranch == matching.MinimumBranch &&
                    manifest.MaximumBranch == matching.MaximumBranch);
                manifests.Insert(0, matching);
            }
            else if (branch != "public" && manifests.Find(manifest => manifest.Source == "Default manifest") is { } fallback)
            {
                manifests.Add(fallback with { Branch = branch, Source = "Default manifest requested for installed branch" });
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
        var version = manifests[selection];
        if (!await InstallWorkshopDependenciesAsync(session, library, item, version.Branch, ct))
            return new("idle", "Workshop installation stopped before installing the selected item.");
        return await DownloadWorkshopItemAsync(session, library, item, version, ct);
    }

    static async Task<bool> InstallWorkshopDependenciesAsync(SteamSession session, ModLibrary library, WorkshopItem item,
        string branch, CancellationToken ct)
    {
        var installed = library.List().Where(mod => mod.Enabled && mod.Workshop is { } origin &&
            origin.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase)).Select(mod => mod.Workshop!.ItemId).ToHashSet();
        var dependencies = new Dictionary<ulong, WorkshopItem>();
        IReadOnlyList<ulong> missing;
        try
        {
            Publish(new("mods", "Checking Workshop dependencies…"));
            missing = await WorkshopDependencyOrder.MissingAsync(item.Id, installed, async (id, token) =>
            {
                var details = await session.WorkshopDetailsAsync(id, token);
                dependencies[id] = details.Item;
                return details.Dependencies;
            }, ct);
        }
        catch (IOException error)
        {
            HostDiagnostics.Error("Workshop dependencies", error);
            return await Ask("workshop-dependency-error", "Steam did not return all dependency details.", ct,
                [new("cancel", "Return to launcher", "Review the required items in Steam"),
                 new("one", "Install this item only", "Continue with the selected Workshop item")]) == "one";
        }
        if (missing.Count == 0) return true;
        var titles = string.Join(", ", missing.Select(id => dependencies[id].Title));
        var choice = await Ask("workshop-dependencies", $"{missing.Count} Workshop dependencies",
            ct, [new("cancel", "Cancel installation", "Return to the launcher"),
                 new("queue", "Install dependencies and item", titles),
                 new("one", "Install this item only", "Use any dependencies already on the iPad")]);
        if (choice != "queue") return choice == "one";
        foreach (var id in missing)
        {
            var dependency = dependencies[id];
            var manifests = await session.WorkshopManifestsAsync(dependency, ct);
            var target = WorkshopManifest.ResolveBranch(manifests, branch);
            if (target is null && manifests.FirstOrDefault(manifest => manifest.Source == "Default manifest") is { } fallback)
                target = fallback with { Branch = branch };
            if (target is null) throw new InvalidDataException($"Steam did not return a manifest for Workshop dependency {id}.");
            await DownloadWorkshopItemAsync(session, library, dependency, target, ct);
            if (!library.List().Any(mod => mod.Enabled && mod.Workshop is { } origin && origin.ItemId == id &&
                origin.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    static async Task<State> DownloadWorkshopItemAsync(SteamSession session, ModLibrary library, WorkshopItem item,
        WorkshopManifest version, CancellationToken ct)
    {
        var title = new string(item.Title.Where(character => !char.IsControl(character)).Take(128).ToArray());
        var progress = new DirectProgress<DownloadProgress>(value =>
            Publish(new("mods", $"Downloading {title}…", value.Completed, value.Total, value.BytesPerSecond,
                value.Remaining?.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture))));
        Publish(new("mods", "Requesting Workshop files from Steam…"));
        using var incoming = await library.PrepareDownloadAsync(item.Title,
            (stage, token) => session.DownloadWorkshopManifestAsync(version, stage, progress, token), ct,
            new WorkshopOrigin(item.Id, version.Branch, version.Id, item.Updated));
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
        PrepareLaunchAsync) ? 0 : -1;

    static async Task<State> PrepareLaunchAsync(PayloadStore payloads, CancellationToken ct)
    {
        if (NativeExtensionsAvailable() != 1) return new("error", "Play requires an extension-enabled launcher build.");
        if (activeProfile is null) return new("error", "Select a launch profile before starting the game.");
        var plan = LaunchPlan.Prepare(payloads, documents, ct, activeProfile.PayloadId);
        var paths = profiles!.EnsureDirectories(activeProfile);
        var modLibrary = new ModLibrary(paths.ModsDirectory);
        var mods = modLibrary.RuntimeDirectory();
        launchProfilePaths = paths with { ModsDirectory = mods, ModsPaused = mods != paths.ModsDirectory };
        Publish(new("verify", "Checking game runtime compatibility…"));
        if (Launcher.Adapters.GameCompatibilityAudit.FirstIssue(plan.GameAssembly, activeProfile.IsolateSaves,
            activeProfile.IsolateMods || launchProfilePaths.ModsPaused) is { } issue)
        {
            HostDiagnostics.Error("check game assembly compatibility", new NotSupportedException(issue));
            return new("error", issue);
        }
        var support = Path.GetDirectoryName(payloads.Root)!;
        var adapter = Path.Combine(support, "RuntimeAdapters", plan.Receipt.PayloadId);
        Directory.CreateDirectory(adapter);
        var harmony = Path.Combine(plan.Assemblies, "0Harmony.dll");
        if (!File.Exists(harmony)) return new("error", "The selected installation is missing its Harmony runtime.");
        try
        {
            Launcher.Adapters.Rewriter.Prepare("harmony", harmony, Path.Combine(managed, "STS2JitSupport.dll"),
                Path.Combine(adapter, "0Harmony.dll"));
        }
        catch (NotSupportedException error)
        {
            HostDiagnostics.Error("prepare Harmony", error);
            return new("error", "The selected game build uses a Harmony runtime that needs an iOS adapter.");
        }
        launchOptions = OptionsStore(payloads).Load();
        GameWebDavSync? cloud = null;
        if (WebDavVault.Load() is { Automation: WebDavAutomationMode.PullBeforePlay or WebDavAutomationMode.SyncBeforePlay or
            WebDavAutomationMode.SyncInBackground } webDav)
        {
            var profileKey = activeProfile.IsolateSaves ? activeProfile.Id : "shared";
            cloud = new GameWebDavSync(webDav, support, profileKey, profiles.SnapshotSaveDirectory(activeProfile));
            if (!await SyncBeforePlayAsync(cloud, ct))
                return new("idle", "Launch cancelled. Use WebDAV save sync to review the connection or conflicts.");
        }
        Publish(new("verify", "Preparing runtime files…"));

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
        var godotLog = Payload.SafePath(documents, "LauncherLogs/godot.log");
        Directory.CreateDirectory(Path.GetDirectoryName(godotLog)!);
        if (File.Exists(godotLog)) File.Move(godotLog, Payload.SafePath(documents, "LauncherLogs/godot.log.previous"), overwrite: true);
        assemblyRoot = plan.Assemblies;
        adapterRoot = adapter;
        cloud?.GameStarting();
        gameWebDav = cloud;
        loadedGame = AssemblyLoadContext.Default.LoadFromAssemblyPath(plan.GameAssembly);
        releaseInfo = Path.Combine(Path.GetDirectoryName(plan.Pack)!, "release_info.json");
        return new("ready", "Game verified. Starting Godot…");
    }

    static async Task<bool> SyncBeforePlayAsync(GameWebDavSync cloud, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            await cloud.BeforePlayAsync(upload =>
            {
                Publish(new("webdav", upload ? "Uploading the previous session’s saves…" : "Checking WebDAV for new saves…"));
            }, deadline.Token);
            return true;
        }
        catch (Exception error) when (!ct.IsCancellationRequested &&
            error is IOException or HttpRequestException or ArgumentException or InvalidDataException or OperationCanceledException)
        {
            HostDiagnostics.Error("automatic WebDAV sync", error);
            var message = error is SaveSyncConflictException conflict
                ? $"{conflict.Count} save files have conflicting changes."
                : "The automatic WebDAV sync did not complete.";
            var action = await Ask("webdav-beforeready", message, ct,
                [new("cancel", "Return to launcher", "Review the connection and conflicts under Saves"),
                 new("play", "Play with iPad saves", "Continue using the save files currently on this device")]);
            return action == "play";
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int BeginGameBackgroundSync() => gameWebDav?.StartBackground() == true ? 0 : -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GameBackgroundSyncActive() => gameWebDav?.BackgroundActive == true ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int CancelGameBackgroundSync()
    {
        gameWebDav?.CancelBackground();
        return 0;
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
            HostDiagnostics.Stage("install game compatibility hooks");
            GameCompatibility.Apply(loadedGame ?? throw new InvalidOperationException(),
                activeProfile ?? throw new InvalidOperationException(), launchProfilePaths ?? throw new InvalidOperationException(),
                releaseInfo ?? throw new InvalidOperationException());
            GameCompatibility.ApplyLaunchOptions(launchOptions);
            HostDiagnostics.Stage("game compatibility hooks installed");
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
