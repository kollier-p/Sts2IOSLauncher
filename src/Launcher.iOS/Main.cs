using Foundation;
using UIKit;
using Security;
using Launcher.Core;
using Launcher.Steam;
using SteamKit2.Authentication;
using System.Text.Json;
using System.Globalization;

namespace Launcher.iOS;

static class Program
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? options) => true;
}

[Register("SceneDelegate")]
public sealed class SceneDelegate : UIWindowSceneDelegate
{
    public override UIWindow? Window { get; set; }
    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        Window = new UIWindow(windowScene);
        ResetLauncher();
        Window.MakeKeyAndVisible();
    }
    internal void ResetLauncher() => Window!.RootViewController = new SetupController(this);
}

static class IdentityVault
{
    const string Service = "org.sts2mobile.ioslauncher.steam";
    static SecRecord Query() => new(SecKind.GenericPassword) { Service = Service, Account = "steam-session" };
    public static SteamIdentity? Load()
    {
        using var query = Query(); using var record = SecKeyChain.QueryAsRecord(query, out var status);
        if (status == SecStatusCode.ItemNotFound) return null;
        if (status != SecStatusCode.Success || record?.ValueData is null) throw new InvalidOperationException("Unable to read Steam Keychain session.");
        return JsonSerializer.Deserialize<SteamIdentity>(record.ValueData.ToArray());
    }
    public static void Save(SteamIdentity identity)
    {
        using var query = Query(); using var record = Query();
        record.Accessible = SecAccessible.WhenUnlockedThisDeviceOnly;
        using var data = NSData.FromArray(JsonSerializer.SerializeToUtf8Bytes(identity)); record.ValueData = data;
        var status = SecKeyChain.Add(record);
        if (status == SecStatusCode.DuplicateItem) { using var update = new SecRecord(SecKind.GenericPassword) { ValueData = data, Accessible = SecAccessible.WhenUnlockedThisDeviceOnly }; status = SecKeyChain.Update(query, update); }
        if (status != SecStatusCode.Success) throw new InvalidOperationException("Unable to save Steam Keychain session.");
    }
    public static void Clear() { using var q = Query(); var result = SecKeyChain.Remove(q); if (result != SecStatusCode.Success && result != SecStatusCode.ItemNotFound) throw new InvalidOperationException("Unable to remove Steam Keychain session."); }
}

sealed class SetupController(SceneDelegate app) : UIViewController
{
    readonly PayloadStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STS2Payloads"));
    readonly UILabel status = new() { Lines = 0, TextColor = UIColor.White };
    readonly UITextField username = Field("Steam account name"), password = Field("Steam password", true), branch = Field("Branch (public)"), manifest = Field("Exact Manifest ID (optional)");
    readonly UIButton start = UIButton.FromType(UIButtonType.System), cancel = UIButton.FromType(UIButtonType.System), saved = UIButton.FromType(UIButtonType.System), logout = UIButton.FromType(UIButtonType.System);
    readonly UISwitch remember = new();
    readonly UIProgressView progress = new(UIProgressViewStyle.Default);
    CancellationTokenSource? active;
    static UITextField Field(string placeholder, bool secure = false) => new() { Placeholder = placeholder, SecureTextEntry = secure, BorderStyle = UITextBorderStyle.RoundedRect, AutocapitalizationType = UITextAutocapitalizationType.None, AutocorrectionType = UITextAutocorrectionType.No, TextColor = UIColor.White, BackgroundColor = UIColor.FromRGB(44, 44, 48) };
    public override void ViewDidLoad()
    {
        base.ViewDidLoad(); View!.BackgroundColor = UIColor.FromRGB(22, 22, 26); OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;
        var heading = new UILabel { Text = "Slay the Spire 2", Font = UIFont.BoldSystemFontOfSize(32), TextColor = UIColor.White, Lines = 0 };
        var sub = new UILabel { Text = "Steam game installation · stage 1", TextColor = UIColor.LightGray, Lines = 0 };
        branch.Text = "public"; remember.On = false;
        start.SetTitle("Sign in & download", UIControlState.Normal); saved.SetTitle("Use saved Steam session", UIControlState.Normal); cancel.SetTitle("Cancel", UIControlState.Normal); logout.SetTitle("Forget Steam session", UIControlState.Normal);
        var rememberLabel = new UILabel { Text = "Remember Steam session", TextColor = UIColor.White };
        var toggle = new UIStackView(new UIView[] { rememberLabel, remember }) { Axis = UILayoutConstraintAxis.Horizontal, Distribution = UIStackViewDistribution.EqualSpacing };
        var stack = new UIStackView(new UIView[] { heading, sub, username, password, branch, manifest, toggle, start, saved, logout, progress, status, cancel }) { Axis = UILayoutConstraintAxis.Vertical, Spacing = 16, TranslatesAutoresizingMaskIntoConstraints = false };
        View.AddSubview(stack); var guide = View.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(new[] { stack.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 24), stack.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 24), stack.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -24), stack.BottomAnchor.ConstraintLessThanOrEqualTo(guide.BottomAnchor, -20) });
        start.TouchUpInside += async (_, _) => await Run(false);
        saved.TouchUpInside += async (_, _) => await Run(true);
        cancel.TouchUpInside += (_, _) => active?.Cancel();
        logout.TouchUpInside += (_, _) => { try { IdentityVault.Clear(); status.Text = "Steam session removed."; } catch { status.Text = "Unable to remove session."; } };
        cancel.Enabled = false;
        try { var installed = store.Current(); status.Text = installed is null ? "No game installed." : $"Installed Steam manifest {installed.ManifestId}. Game runtime not yet integrated."; }
        catch { status.Text = "Existing installation could not be validated. Download again."; }
    }
    async Task Run(bool reuse)
    {
        if (active is not null) return;
        var selectedBranch = branch.Text?.Trim() ?? "public"; if (selectedBranch.Length == 0) selectedBranch = "public";
        ulong? selectedManifest = null;
        if (!string.IsNullOrWhiteSpace(manifest.Text)) { if (!ulong.TryParse(manifest.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0) { status.Text = "Enter a valid Manifest ID."; return; } selectedManifest = value; }
        var account = username.Text?.Trim() ?? ""; var secret = password.Text ?? "";
        if (!reuse && (account.Length == 0 || secret.Length == 0)) { status.Text = "Enter your Steam account name and password."; return; }
        password.Text = ""; View!.EndEditing(true);
        using var cts = new CancellationTokenSource(); active = cts; SetBusy(true);
        bool success = false;
        try
        {
            status.Text = "Connecting to Steam…";
            await using var session = new SteamSession();
            SteamIdentity identity;
            if (reuse) { identity = IdentityVault.Load() ?? throw new InvalidOperationException("No saved Steam session. Sign in again."); await session.LogOnAsync(identity, true, cts.Token); }
            else { identity = await session.AuthenticateAsync(account, secret, new GuardUI(this, cts.Token), null, remember.On, cts.Token); if (remember.On) IdentityVault.Save(identity); }
            secret = "";
            var reporter = new Progress<DownloadProgress>(p => { progress.Progress = p.Total > 0 ? Math.Clamp((float)((double)p.Completed / p.Total), 0, 1) : 0; status.Text = $"{p.Stage}: {p.Completed:N0} / {p.Total:N0} bytes"; });
            await Task.Run(() => session.DownloadAndInstallAsync(store, selectedBranch, selectedManifest, reporter, cts.Token), cts.Token); success = true;
        }
        catch (OperationCanceledException) { status.Text = "Cancelled. Existing installation is unchanged."; }
        catch (UnauthorizedAccessException) { status.Text = "Steam authorization failed. Check account ownership and manifest access."; }
        catch (Exception e) { status.Text = $"Installation failed ({e.GetType().Name}). Sign in again or retry the download."; }
        finally { active = null; SetBusy(false); }
        if (success) app.ResetLauncher(); // In-process reset only; real jailbreak relaunch is a separate device-dependent adapter.
    }
    void SetBusy(bool busy) { start.Enabled = saved.Enabled = logout.Enabled = username.Enabled = password.Enabled = branch.Enabled = manifest.Enabled = remember.Enabled = !busy; cancel.Enabled = busy; }
    internal Task<string> AskCode(string prompt, CancellationToken ct)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        InvokeOnMainThread(() => {
            if (ct.IsCancellationRequested) { result.TrySetCanceled(ct); return; }
            var alert = UIAlertController.Create("Steam Guard", prompt, UIAlertControllerStyle.Alert);
            alert.AddTextField(field => { field.Placeholder = "Code"; field.AutocapitalizationType = UITextAutocapitalizationType.AllCharacters; });
            alert.AddAction(UIAlertAction.Create("Submit", UIAlertActionStyle.Default, _ => result.TrySetResult(alert.TextFields![0].Text?.Trim() ?? "")));
            alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, _ => result.TrySetCanceled()));
            PresentViewController(alert, true, null);
        });
        return result.Task.WaitAsync(ct);
    }
    internal Task<bool> AskConfirmation(CancellationToken ct)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        InvokeOnMainThread(() => {
            if (ct.IsCancellationRequested) { result.TrySetCanceled(ct); return; }
            var alert = UIAlertController.Create("Steam Guard", "Approve the sign-in in your Steam mobile app, or choose to enter a code.", UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create("Wait for approval", UIAlertActionStyle.Default, _ => result.TrySetResult(true)));
            alert.AddAction(UIAlertAction.Create("Enter code", UIAlertActionStyle.Default, _ => result.TrySetResult(false)));
            alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, _ => result.TrySetCanceled()));
            PresentViewController(alert, true, null);
        });
        return result.Task.WaitAsync(ct);
    }
}
sealed class GuardUI(SetupController ui, CancellationToken ct) : IAuthenticator
{
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => ui.AskCode(previousCodeWasIncorrect ? "Incorrect code. Try again." : "Enter your Steam mobile authenticator code.", ct);
    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => ui.AskCode(previousCodeWasIncorrect ? "Incorrect code. Try again." : "Enter the code sent to your Steam email.", ct);
    public Task<bool> AcceptDeviceConfirmationAsync() => ui.AskConfirmation(ct);
}
