using System.Diagnostics;
using System.Reflection;

namespace Launcher.Host;

// Use the installed game's GodotSharp assembly. The launcher has no competing Godot runtime.
internal sealed class GameRenderWarmup
{
    const int DrawSize = 48;
    const int BatchSize = 4;
    const string CatalogFile = "startup-assets-v1.txt";

    static readonly string[] Assets =
    [
        "res://images/packed/combat_ui/end_turn_button.png",
        "res://images/packed/combat_ui/end_turn_button_glow.png",
        "res://images/packed/sprite_fonts/ironclad_energy_icon.png",
        "res://images/packed/sprite_fonts/silent_energy_icon.png",
        "res://images/packed/sprite_fonts/regent_energy_icon.png",
        "res://images/atlases/intent_atlas.sprites/intent_attack.tres",
        "res://images/atlases/intent_atlas.sprites/intent_defend.tres",
        "res://materials/cards/banners/card_banner_common_mat.tres",
        "res://materials/cards/banners/card_banner_uncommon_mat.tres",
        "res://materials/cards/banners/card_banner_rare_mat.tres",
        "res://materials/cards/banners/card_banner_status_mat.tres",
        "res://materials/cards/banners/card_banner_curse_mat.tres",
        "res://materials/cards/frames/card_frame_red_mat.tres",
        "res://materials/cards/frames/card_frame_green_mat.tres",
        "res://materials/cards/frames/card_frame_blue_mat.tres",
        "res://materials/cards/frames/card_frame_pink_mat.tres",
        "res://materials/cards/frames/card_frame_orange_mat.tres",
        "res://materials/cards/frames/card_frame_colorless_mat.tres",
        "res://scenes/cards/card_portrait_blur_material.tres",
        "res://scenes/cards/card_canvas_group_blur_material.tres",
        "res://scenes/cards/card_canvas_group_mask_material.tres",
        "res://shaders/vfx/common/vfx_flipbook_shader.gdshader",
        "res://shaders/vfx/common/vfx_row_flipbook_shader.gdshader",
        "res://shaders/hsv.gdshader",
        "res://scenes/vfx/hit_spark_vfx.tscn",
        "res://scenes/vfx/vfx_damage_num.tscn",
        "res://scenes/vfx/vfx_attack_slash.tscn",
        "res://scenes/vfx/vfx_attack_blunt.tscn",
        "res://scenes/vfx/vfx_block.tscn",
        "res://scenes/vfx/vfx_shiv_throw.tscn",
        "res://scenes/vfx/vfx_poison_impact.tscn"
    ];

    static readonly List<object> RetainedAssets = [];
    static bool started;
    internal static string? CatalogDirectory { private get; set; }

    readonly Assembly godot;
    readonly object owner, tree, root;
    readonly EventInfo processFrame;
    readonly Delegate callback;
    readonly MethodInfo load, exists, addChild, queueFree, isInsideTree;
    readonly object reuse, childMode, updateOnce, updateDisabled;
    readonly PropertyInfo updateMode, guiDisableInput;
    readonly string? catalogPath;
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    readonly List<string> available = [];
    string[] work = Assets;
    object? overlay, status, progress, viewport;
    bool? originalInputDisabled;
    bool cachedCatalog;
    int nextAsset, loaded, submitted, failed, waitFrames;

    GameRenderWarmup(object owner, Assembly godot)
    {
        this.owner = owner;
        this.godot = godot;
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
        var internalMode = Godot("Node+InternalMode");
        addChild = node.GetMethod("AddChild", [node, typeof(bool), internalMode])!;
        childMode = Enum.Parse(internalMode, "Disabled");
        queueFree = node.GetMethod("QueueFree", Type.EmptyTypes)!;
        isInsideTree = node.GetMethod("IsInsideTree", Type.EmptyTypes)!;
        updateMode = Godot("SubViewport").GetProperty("RenderTargetUpdateMode")!;
        updateOnce = Enum.Parse(updateMode.PropertyType, "Once");
        updateDisabled = Enum.Parse(updateMode.PropertyType, "Disabled");
        guiDisableInput = Godot("Viewport").GetProperty("GuiDisableInput")!;
        if (CatalogDirectory is { } directory) catalogPath = Path.Combine(directory, CatalogFile);
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
            if (isInsideTree.Invoke(owner, null) is not true || elapsed.Elapsed > TimeSpan.FromSeconds(12))
            {
                Finish(completed: false);
                return;
            }
            if (overlay is null)
            {
                ShowOverlay();
                RestoreCatalog();
                Set(progress!, "MaxValue", (double)work.Length);
                HostDiagnostics.Stage($"startup preload {(cachedCatalog ? "cached catalog" : "discovering assets")} ({work.Length} candidates)");
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
                Finish(completed: true);
                return;
            }

            var batch = Stopwatch.StartNew();
            int draws = 0;
            for (int i = 0; i < BatchSize && nextAsset < work.Length; i++)
            {
                var path = work[nextAsset++];
                Set(status!, "Text", (path.EndsWith(".tscn", StringComparison.Ordinal)
                    ? "Loading combat effects" : "Preparing textures and shaders") + $"  ·  {nextAsset}/{work.Length}");
                if (LoadAsset(path, draws)) draws++;
                if (batch.ElapsedMilliseconds >= 6) break;
            }
            Set(progress!, "Value", (double)nextAsset);
            if (viewport is not null)
            {
                Add(root, viewport);
                updateMode.SetValue(viewport, updateOnce);
                submitted += draws;
                // Give the renderer a full frame before freeing the offscreen canvas.
                waitFrames = 1;
            }
        }
        catch (Exception error)
        {
            HostDiagnostics.Error("startup preload", error);
            Finish(completed: false);
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
            if (new FileInfo(catalogPath).Length > 8192) return;
            var known = File.ReadAllLines(catalogPath).ToHashSet(StringComparer.Ordinal);
            var candidates = Assets.Where(known.Contains).ToArray();
            if (candidates.Length == 0 || candidates.Length != known.Count) return;
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

    bool LoadAsset(string path, int slot)
    {
        if (!cachedCatalog && exists.Invoke(null, [path, ""]) is not true) return false;
        var resource = load.Invoke(null, [path, "", reuse]);
        if (resource is null)
        {
            failed++;
            HostDiagnostics.Stage($"startup preload could not load {path}");
            return false;
        }
        available.Add(path);
        RetainedAssets.Add(resource);
        loaded++;

        // Loading a PackedScene also caches its dependency resources. Combat scripts
        // and effects run only when the game itself instantiates the scene.
        var drawable = CreateDrawable(resource);
        if (drawable is null) return false;
        if (viewport is null)
        {
            viewport = New("SubViewport");
            Set(viewport, "Size", New("Vector2I", BatchSize * DrawSize, DrawSize));
            updateMode.SetValue(viewport, updateDisabled);
        }
        Set(drawable, "Position", New("Vector2", (float)(slot * DrawSize), 0f));
        Set(drawable, "Size", New("Vector2", (float)DrawSize, (float)DrawSize));
        Add(viewport, drawable);
        return true;
    }

    object? CreateDrawable(object resource)
    {
        if (Godot("Texture2D").IsInstanceOfType(resource))
        {
            var texture = New("TextureRect");
            Set(texture, "Texture", resource);
            SetEnum(texture, "ExpandMode", "IgnoreSize");
            return texture;
        }

        var material = Godot("ShaderMaterial").IsInstanceOfType(resource) ? resource : null;
        var shader = material?.GetType().GetProperty("Shader")?.GetValue(material) ?? resource;
        var shaderType = Godot("Shader");
        if (!shaderType.IsInstanceOfType(shader) ||
            shaderType.GetMethod("GetMode", Type.EmptyTypes)!.Invoke(shader, null)?.ToString() != "CanvasItem")
            return null;

        if (material is null)
        {
            material = New("ShaderMaterial");
            Set(material, "Shader", shader);
        }
        var rect = New("ColorRect");
        Set(rect, "Material", material);
        return rect;
    }

    void Finish(bool completed)
    {
        try
        {
            processFrame.RemoveEventHandler(tree, callback);
            if (originalInputDisabled is { } disabled) guiDisableInput.SetValue(root, disabled);
            if (overlay is not null) queueFree.Invoke(overlay, null);
            if (viewport is not null) queueFree.Invoke(viewport, null);
            if (completed) SaveCatalog();
        }
        catch (Exception error) { HostDiagnostics.Error("close startup preload", error); }
        HostDiagnostics.Stage($"startup preload {(completed ? "complete" : "stopped")}: {loaded} resources, {submitted} offscreen draws, " +
            $"{failed} failed, {elapsed.ElapsedMilliseconds} ms");
    }
}
