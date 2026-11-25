using System.Reflection;

namespace Launcher.Host;

// Use the installed game's GodotSharp assembly. The launcher has no competing Godot runtime.
internal sealed class GameRenderWarmup
{
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
        "res://materials/cards/banners/card_banner_rare_mat.tres",
        "res://materials/cards/frames/card_frame_red_mat.tres",
        "res://materials/cards/frames/card_frame_green_mat.tres",
        "res://shaders/vfx/common/vfx_flipbook_shader.gdshader",
        "res://shaders/hsv.gdshader"
    ];

    static bool started;
    static readonly List<object> RetainedAssets = [];
    readonly object owner;
    readonly Assembly godot;
    readonly object tree;
    readonly EventInfo processFrame;
    readonly Delegate callback;
    readonly MethodInfo load, exists, addChild, queueFree, isInsideTree;
    readonly PropertyInfo updateMode;
    readonly object updateOnce;
    object? viewport;
    int nextAsset, waitFrames, submitted;

    GameRenderWarmup(object owner, Assembly godot)
    {
        this.owner = owner;
        this.godot = godot;
        var node = Godot("Node");
        tree = node.GetMethod("GetTree", Type.EmptyTypes)!.Invoke(owner, null)!;
        var sceneTree = Godot("SceneTree");
        processFrame = sceneTree.GetEvent("ProcessFrame")!;
        callback = Delegate.CreateDelegate(processFrame.EventHandlerType!, this, nameof(Frame));
        var loader = Godot("ResourceLoader");
        load = loader.GetMethods().Single(method => method.Name == "Load" && !method.IsGenericMethod &&
            method.GetParameters().Length == 3);
        exists = loader.GetMethod("Exists", [typeof(string), typeof(string)])!;
        addChild = node.GetMethod("AddChild", [node, typeof(bool), Godot("Node+InternalMode")])!;
        queueFree = node.GetMethod("QueueFree", Type.EmptyTypes)!;
        isInsideTree = node.GetMethod("IsInsideTree", Type.EmptyTypes)!;
        updateMode = Godot("SubViewport").GetProperty("RenderTargetUpdateMode")!;
        updateOnce = Enum.Parse(updateMode.PropertyType, "Once");
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
        catch (Exception error) { HostDiagnostics.Error("schedule optional render warmup", error); }
    }

    Type Godot(string type) => godot.GetType("Godot." + type, throwOnError: true)!;
    object New(string type, params object[] inputs) => Activator.CreateInstance(Godot(type), inputs)!;
    static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    void Add(object parent, object child) => addChild.Invoke(parent, [child, false, Enum.ToObject(addChild.GetParameters()[2].ParameterType, 0)]);

    public void Frame()
    {
        try
        {
            if (waitFrames > 0) { waitFrames--; return; }
            if (viewport is not null)
            {
                queueFree.Invoke(viewport, null);
                viewport = null;
            }
            if (nextAsset >= Assets.Length || isInsideTree.Invoke(owner, null) is not true)
            {
                processFrame.RemoveEventHandler(tree, callback);
                HostDiagnostics.Stage($"render warmup submitted {submitted} offscreen draws");
                return;
            }

            var path = Assets[nextAsset++];
            if (exists.Invoke(null, [path, ""]) is not true) return;
            var resource = load.Invoke(null, [path, "", Enum.Parse(load.GetParameters()[2].ParameterType, "Reuse")]);
            if (resource is null) return;
            object drawable;
            if (Godot("Texture2D").IsInstanceOfType(resource))
            {
                drawable = New("Sprite2D");
                Set(drawable, "Texture", resource);
                Set(drawable, "Centered", false);
            }
            else
            {
                var material = Godot("ShaderMaterial").IsInstanceOfType(resource) ? resource : null;
                var shaderType = Godot("Shader");
                var shader = material?.GetType().GetProperty("Shader")?.GetValue(material) ?? resource;
                if (!shaderType.IsInstanceOfType(shader) ||
                    shaderType.GetMethod("GetMode", Type.EmptyTypes)!.Invoke(shader, null)?.ToString() != "CanvasItem") return;
                if (material is null)
                {
                    material = New("ShaderMaterial");
                    Set(material, "Shader", shader);
                }
                drawable = New("ColorRect");
                Set(drawable, "Size", New("Vector2", 64f, 64f));
                Set(drawable, "Material", material);
            }
            viewport = New("SubViewport");
            Set(viewport, "Size", New("Vector2I", 64, 64));
            Add(viewport, drawable);
            Add(Godot("SceneTree").GetProperty("Root")!.GetValue(tree)!, viewport);
            updateMode.SetValue(viewport, updateOnce);
            RetainedAssets.Add(resource);
            submitted++;
            waitFrames = 1;
        }
        catch (Exception error)
        {
            processFrame.RemoveEventHandler(tree, callback);
            if (viewport is not null) queueFree.Invoke(viewport, null);
            HostDiagnostics.Error("optional render warmup", error);
        }
    }
}
