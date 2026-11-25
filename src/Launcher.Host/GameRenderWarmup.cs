using System.Diagnostics;
using System.Reflection;

namespace Launcher.Host;

// Use the installed game's GodotSharp assembly. The launcher has no competing Godot runtime.
internal sealed class GameRenderWarmup
{
    const int DrawSize = 48;
    const int BatchSize = 4;
    const int DrawSlots = 8;
    const int SceneDrawLimit = 4;

    static readonly List<object> RetainedAssets = [];
    static bool started;
    static Task? gamePreload;
    internal static string? CatalogDirectory { private get; set; }
    internal static bool Extended { private get; set; }

    readonly Assembly godot;
    readonly bool extended;
    readonly bool gpuParticles;
    readonly TimeSpan timeLimit;
    readonly object owner, tree, root;
    readonly EventInfo processFrame;
    readonly Delegate callback;
    readonly MethodInfo load, exists, listDirectory, addChild, queueFree, isInsideTree, isValid;
    readonly object reuse, childMode, updateOnce, updateAlways, updateDisabled;
    readonly PropertyInfo updateMode, guiDisableInput;
    readonly string? catalogPath;
    readonly GameWarmupVisuals visuals;
    readonly GameWarmupMemory? memory;
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    readonly List<string> available = [];
    readonly HashSet<GameWarmupVisuals.Preview> drawn = [];
    string[] work = [];
    object? overlay, status, progress, viewport;
    bool? originalInputDisabled;
    bool cachedCatalog, batchHasParticles, waitingForGame;
    int nextAsset, loaded, submitted, sceneDraws, particleDraws, failed, waitFrames;

    GameRenderWarmup(object owner, Assembly godot)
    {
        this.owner = owner;
        this.godot = godot;
        extended = Extended;
        timeLimit = TimeSpan.FromSeconds(extended ? 60 : 12);
        var node = Godot("Node");
        tree = node.GetMethod("GetTree", Type.EmptyTypes)!.Invoke(owner, null)!;
        var sceneTree = Godot("SceneTree");
        root = sceneTree.GetProperty("Root")!.GetValue(tree)!;
        processFrame = sceneTree.GetEvent("ProcessFrame")!;
        callback = Delegate.CreateDelegate(processFrame.EventHandlerType!, this, nameof(Frame));
        var loader = Godot("ResourceLoader");
        load = loader.GetMethods().Single(method => method.Name == "Load" && !method.IsGenericMethod &&
            method.GetParameters().Length == 3);
        reuse = Enum.Parse(load.GetParameters()[2].ParameterType, "Reuse");
        exists = loader.GetMethod("Exists", [typeof(string), typeof(string)])!;
        listDirectory = loader.GetMethod("ListDirectory", [typeof(string)])!;
        var internalMode = Godot("Node+InternalMode");
        addChild = node.GetMethod("AddChild", [node, typeof(bool), internalMode])!;
        childMode = Enum.Parse(internalMode, "Disabled");
        queueFree = node.GetMethod("QueueFree", Type.EmptyTypes)!;
        isInsideTree = node.GetMethod("IsInsideTree", Type.EmptyTypes)!;
        isValid = Godot("GodotObject").GetMethod("IsInstanceValid", [Godot("GodotObject")])!;
        updateMode = Godot("SubViewport").GetProperty("RenderTargetUpdateMode")!;
        updateOnce = Enum.Parse(updateMode.PropertyType, "Once");
        updateAlways = Enum.Parse(updateMode.PropertyType, "Always");
        updateDisabled = Enum.Parse(updateMode.PropertyType, "Disabled");
        guiDisableInput = Godot("Viewport").GetProperty("GuiDisableInput")!;
        visuals = new GameWarmupVisuals(Godot);
        if (extended)
        {
            var renderingServer = Godot("RenderingServer");
            memory = new GameWarmupMemory(renderingServer, Godot("RenderingServer+RenderingInfo"));
            var method = renderingServer.GetMethod("GetCurrentRenderingMethod", Type.EmptyTypes)!.Invoke(null, null) as string;
            gpuParticles = method is "mobile" or "gl_compatibility" or "forward_plus";
        }
        if (CatalogDirectory is { } directory)
            catalogPath = Path.Combine(directory, extended ? "startup-assets-extended-v1.txt" : "startup-assets-v1.txt");
    }

    public static void MainMenuReady(object __instance)
    {
        if (started) return;
        started = true;
        try
        {
            var node = __instance.GetType();
            while (node is not null && node.FullName != "Godot.Node") node = node.BaseType;
            if (node is null) throw new TypeLoadException("The game menu is not a Godot node.");
            var warmup = new GameRenderWarmup(__instance, node.Assembly);
            warmup.processFrame.AddEventHandler(warmup.tree, warmup.callback);
        }
        catch (Exception error) { HostDiagnostics.Error("schedule startup preload", error); }
    }

    public static void TrackGamePreload(Task __result) => gamePreload = __result;

    Type Godot(string type) => godot.GetType("Godot." + type, throwOnError: true)!;
    object New(string type, params object[] inputs) => Activator.CreateInstance(Godot(type), inputs)!;
    static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);

    static void SetEnum(object target, string property, string value)
    {
        var member = target.GetType().GetProperty(property)!;
        member.SetValue(target, Enum.Parse(member.PropertyType, value));
    }

    void Add(object parent, object child) => addChild.Invoke(parent, [child, false, childMode]);

    public void Frame()
    {
        try
        {
            if (isValid.Invoke(null, [owner]) is not true || isInsideTree.Invoke(owner, null) is not true)
            {
                Finish("menu closed");
                return;
            }
            if (elapsed.Elapsed > timeLimit)
            {
                Finish("time limit");
                return;
            }
            if (overlay is null)
            {
                ShowOverlay();
                RestoreCatalog();
                if (!cachedCatalog)
                    work = GameWarmupAssets.Discover(extended, directory => (string[])listDirectory.Invoke(null, [directory])!);
                Set(progress!, "MaxValue", (double)work.Length);
                HostDiagnostics.Stage($"startup preload {(extended ? "extended" : "standard")}, " +
                    $"{(cachedCatalog ? "cached catalog" : "discovering assets")} ({work.Length} candidates)");
                // Let the loading screen present before any resource load.
                return;
            }
            if (waitFrames > 0)
            {
                waitFrames--;
                return;
            }
            if (viewport is not null)
            {
                queueFree.Invoke(viewport, null);
                viewport = null;
            }
            if (nextAsset == work.Length)
            {
                if (extended && gamePreload is { IsCompleted: false })
                {
                    if (memory?.LimitReached() is { } limit)
                    {
                        Finish(limit);
                        return;
                    }
                    if (!waitingForGame)
                    {
                        waitingForGame = true;
                        Set(status!, "Text", "Finishing the game’s built-in preload");
                        Set(progress!, "Indeterminate", true);
                        HostDiagnostics.Stage("startup preload waiting for game resources");
                    }
                    return;
                }
                Finish();
                return;
            }

            var batch = Stopwatch.StartNew();
            int draws = 0;
            batchHasParticles = false;
            for (int i = 0; i < BatchSize && nextAsset < work.Length && draws <= DrawSlots - SceneDrawLimit; i++)
            {
                if (memory?.LimitReached() is { } limit)
                {
                    Finish(limit);
                    return;
                }
                var path = work[nextAsset++];
                draws += LoadAsset(path, draws);
                if (batch.ElapsedMilliseconds >= 6) break;
            }
            Set(status!, "Text", $"{GameWarmupAssets.Phase(work[nextAsset - 1])}  ·  {nextAsset}/{work.Length}");
            Set(progress!, "Value", (double)nextAsset);
            if (viewport is not null)
            {
                Add(root, viewport);
                updateMode.SetValue(viewport, batchHasParticles ? updateAlways : updateOnce);
                submitted += draws;
                // Particles need consecutive frames to exercise simulation and draw.
                waitFrames = batchHasParticles ? 3 : 1;
            }
        }
        catch (Exception error)
        {
            HostDiagnostics.Error("startup preload", error);
            Finish("error");
        }
    }

    void ShowOverlay()
    {
        var layer = New("CanvasLayer");
        overlay = layer;
        Set(layer, "Name", New("StringName", "LauncherStartupPreload"));
        Set(layer, "Layer", 1000);
        var backdrop = New("ColorRect");
        Set(backdrop, "Color", New("Color", 0.035f, 0.046f, 0.069f, 1f));
        Add(layer, backdrop);
        Set(backdrop, "AnchorRight", 1f);
        Set(backdrop, "AnchorBottom", 1f);

        var center = New("CenterContainer");
        Add(backdrop, center);
        Set(center, "AnchorRight", 1f);
        Set(center, "AnchorBottom", 1f);
        var stack = New("VBoxContainer");
        var viewportSize = Godot("Viewport").GetMethod("GetVisibleRect", Type.EmptyTypes)!.Invoke(root, null)!;
        var size = viewportSize.GetType().GetProperty("Size")!.GetValue(viewportSize)!;
        float width = (float)size.GetType().GetField("X")!.GetValue(size)!;
        Set(stack, "CustomMinimumSize", New("Vector2", Math.Clamp(width * 0.55f, 240f, 480f), 0f));
        Add(center, stack);

        var title = New("Label");
        SetEnum(title, "HorizontalAlignment", "Center");
        Set(title, "Text", "Preparing Slay the Spire 2");
        Godot("Control").GetMethod("AddThemeFontSizeOverride", [Godot("StringName"), typeof(int)])!
            .Invoke(title, [New("StringName", "font_size"), 28]);
        Add(stack, title);

        status = New("Label");
        SetEnum(status, "HorizontalAlignment", "Center");
        Set(status, "Text", "Checking game assets");
        Add(stack, status);
        progress = New("ProgressBar");
        Set(progress, "ShowPercentage", false);
        Set(progress, "CustomMinimumSize", New("Vector2", 0f, 10f));
        Add(stack, progress);
        Add(root, layer);

        originalInputDisabled = (bool)guiDisableInput.GetValue(root)!;
        guiDisableInput.SetValue(root, true);
    }

    void RestoreCatalog()
    {
        if (catalogPath is null || !File.Exists(catalogPath)) return;
        try
        {
            if (new FileInfo(catalogPath).Length > 128 * 1024) return;
            var candidates = File.ReadAllLines(catalogPath);
            if (candidates.Length is 0 or > GameWarmupAssets.MaxCandidates ||
                candidates.Distinct(StringComparer.Ordinal).Count() != candidates.Length ||
                candidates.Any(path => !GameWarmupAssets.IsAllowed(path, extended))) return;
            work = candidates;
            cachedCatalog = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            HostDiagnostics.Error("read startup asset catalog", error);
        }
    }

    void SaveCatalog()
    {
        if (cachedCatalog || failed != 0 || available.Count == 0 || catalogPath is null) return;
        var temporary = catalogPath + ".tmp";
        try
        {
            File.WriteAllLines(temporary, available);
            File.Move(temporary, catalogPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            HostDiagnostics.Error("save startup asset catalog", error);
        }
    }

    int LoadAsset(string path, int slot)
    {
        if (!cachedCatalog && exists.Invoke(null, [path, ""]) is not true) return 0;
        var resource = load.Invoke(null, [path, "", reuse]);
        if (resource is null)
        {
            failed++;
            HostDiagnostics.Stage($"startup preload could not load {path}");
            return 0;
        }
        available.Add(path);
        RetainedAssets.Add(resource);
        loaded++;

        if (visuals.IsTexture(resource)) return Draw(new(resource, null), slot) ? 1 : 0;
        if (visuals.IsCanvasMaterial(resource)) return Draw(new(null, resource), slot) ? 1 : 0;
        if (visuals.IsCanvasShader(resource))
        {
            var material = New("ShaderMaterial");
            Set(material, "Shader", resource);
            return Draw(new(null, material), slot) ? 1 : 0;
        }
        if (!extended || !visuals.SceneType.IsInstanceOfType(resource)) return 0;

        int count = 0;
        foreach (var preview in visuals.ReadScene(resource, gpuParticles))
        {
            if (!Draw(preview, slot + count)) continue;
            sceneDraws++;
            if (++count == SceneDrawLimit) break;
        }
        return count;
    }

    bool Draw(GameWarmupVisuals.Preview preview, int slot)
    {
        if (!drawn.Add(preview)) return false;
        var drawable = CreateDrawable(preview);
        if (viewport is null)
        {
            viewport = New("SubViewport");
            Set(viewport, "Size", New("Vector2I", DrawSlots * DrawSize, DrawSize));
            updateMode.SetValue(viewport, updateDisabled);
        }
        bool particles = preview.ParticleMaterial is not null;
        float center = particles ? DrawSize / 2f : 0f;
        Set(drawable, "Position", New("Vector2", slot * DrawSize + center, center));
        if (!particles) Set(drawable, "Size", New("Vector2", (float)DrawSize, (float)DrawSize));
        Add(viewport, drawable);
        return true;
    }

    object CreateDrawable(GameWarmupVisuals.Preview preview)
    {
        object drawable;
        if (preview.ParticleMaterial is { } particleMaterial)
        {
            drawable = New("GpuParticles2D");
            Set(drawable, "Amount", 4);
            Set(drawable, "Lifetime", 0.5);
            Set(drawable, "Preprocess", 0.15);
            Set(drawable, "ProcessMaterial", particleMaterial);
            if (preview.Texture is { } particleTexture) Set(drawable, "Texture", particleTexture);
            batchHasParticles = true;
            particleDraws++;
        }
        else if (preview.Texture is { } texture)
        {
            drawable = New("TextureRect");
            Set(drawable, "Texture", texture);
            SetEnum(drawable, "ExpandMode", "IgnoreSize");
        }
        else drawable = New("ColorRect");
        if (preview.Material is { } material) Set(drawable, "Material", material);
        return drawable;
    }

    void Finish(string? reason = null)
    {
        try
        {
            processFrame.RemoveEventHandler(tree, callback);
            if (originalInputDisabled is { } disabled) guiDisableInput.SetValue(root, disabled);
            if (overlay is not null) queueFree.Invoke(overlay, null);
            if (viewport is not null) queueFree.Invoke(viewport, null);
            if (reason is null) SaveCatalog();
        }
        catch (Exception error) { HostDiagnostics.Error("close startup preload", error); }
        HostDiagnostics.Stage($"startup preload {(reason is null ? "complete" : $"stopped ({reason})")}: " +
            $"{loaded} resources, {submitted} offscreen draws ({sceneDraws} scene, {particleDraws} particle), " +
            $"{failed} failed, {elapsed.ElapsedMilliseconds} ms");
    }
}
