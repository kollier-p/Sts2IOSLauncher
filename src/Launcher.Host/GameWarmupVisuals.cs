using System.Reflection;

namespace Launcher.Host;

internal sealed class GameWarmupVisuals
{
    internal readonly record struct Preview(object? Texture, object? Material, object? ParticleMaterial = null);

    readonly MethodInfo getState, getNodeCount, getNodeType, getNodeInstance, getPropertyCount, getPropertyName, getPropertyValue;
    readonly MethodInfo asObject, getAnimationNames, getFrameCount, getFrameTexture, getShaderMode, getParticleFlag;
    readonly PropertyInfo variantType, shaderProperty;
    readonly Type textureType, shaderType, shaderMaterialType, canvasMaterialType, particleMaterialType, spriteFramesType;
    readonly ConstructorInfo stringName;
    readonly object objectVariant, disableZ;

    internal GameWarmupVisuals(Func<string, Type> godot)
    {
        textureType = godot("Texture2D");
        shaderType = godot("Shader");
        shaderMaterialType = godot("ShaderMaterial");
        canvasMaterialType = godot("CanvasItemMaterial");
        particleMaterialType = godot("ParticleProcessMaterial");
        getParticleFlag = particleMaterialType.GetMethods().Single(method => method.Name == "GetParticleFlag");
        disableZ = Enum.Parse(getParticleFlag.GetParameters()[0].ParameterType, "DisableZ");
        spriteFramesType = godot("SpriteFrames");
        stringName = godot("StringName").GetConstructor([typeof(string)])!;
        var packedScene = godot("PackedScene");
        getState = packedScene.GetMethod("GetState", Type.EmptyTypes)!;
        var state = godot("SceneState");
        getNodeCount = state.GetMethod("GetNodeCount", Type.EmptyTypes)!;
        getNodeType = state.GetMethod("GetNodeType", [typeof(int)])!;
        getNodeInstance = state.GetMethod("GetNodeInstance", [typeof(int)])!;
        getPropertyCount = state.GetMethod("GetNodePropertyCount", [typeof(int)])!;
        getPropertyName = state.GetMethod("GetNodePropertyName", [typeof(int), typeof(int)])!;
        getPropertyValue = state.GetMethod("GetNodePropertyValue", [typeof(int), typeof(int)])!;
        var variant = godot("Variant");
        variantType = variant.GetProperty("VariantType")!;
        objectVariant = Enum.Parse(variantType.PropertyType, "Object");
        asObject = variant.GetMethod("AsGodotObject", Type.EmptyTypes)!;
        getAnimationNames = spriteFramesType.GetMethod("GetAnimationNames", Type.EmptyTypes)!;
        getFrameCount = spriteFramesType.GetMethod("GetFrameCount", [stringName.DeclaringType!])!;
        getFrameTexture = spriteFramesType.GetMethod("GetFrameTexture", [stringName.DeclaringType!, typeof(int)])!;
        getShaderMode = shaderType.GetMethod("GetMode", Type.EmptyTypes)!;
        shaderProperty = shaderMaterialType.GetProperty("Shader")!;
        SceneType = packedScene;
    }

    internal Type SceneType { get; }
    internal bool IsTexture(object? resource) => textureType.IsInstanceOfType(resource);
    internal bool IsCanvasMaterial(object? resource) => canvasMaterialType.IsInstanceOfType(resource) || HasShaderMode(resource, "CanvasItem");
    internal bool IsCanvasShader(object resource) => shaderType.IsInstanceOfType(resource) && ShaderMode(resource) == "CanvasItem";

    bool HasShaderMode(object? material, string mode) => shaderMaterialType.IsInstanceOfType(material) &&
        shaderProperty.GetValue(material) is { } shader && ShaderMode(shader) == mode;

    bool CanPreviewParticles(object? material) => HasShaderMode(material, "Particles") ||
        // Godot can otherwise normalize a default 3D material in place when assigned to GPUParticles2D.
        (particleMaterialType.IsInstanceOfType(material) && getParticleFlag.Invoke(material, [disableZ]) is true);

    string? ShaderMode(object shader) => getShaderMode.Invoke(shader, null)?.ToString();

    internal IEnumerable<Preview> ReadScene(object scene, bool gpuParticles)
    {
        // Read the packed data. Instantiating the original scene could run game or mod constructors.
        var visited = new HashSet<object>();
        var pending = new Queue<object>();
        pending.Enqueue(scene);
        int nodesLeft = 64;
        while (pending.TryDequeue(out var packed) && nodesLeft > 0)
        {
            if (!visited.Add(packed)) continue;
            var state = getState.Invoke(packed, null)!;
            int count = (int)getNodeCount.Invoke(state, null)!;
            for (int node = 0; node < count && nodesLeft > 0; node++, nodesLeft--)
            {
                using var nativeType = (IDisposable?)getNodeType.Invoke(state, [node]);
                var type = nativeType?.ToString();
                if (type is "Sprite2D" or "TextureRect" or "AnimatedSprite2D" or "GPUParticles2D" or "CPUParticles2D")
                {
                    foreach (var preview in ReadNode(state, node, type, gpuParticles)) yield return preview;
                }
                if (getNodeInstance.Invoke(state, [node]) is { } child) pending.Enqueue(child);
            }
        }
    }

    IEnumerable<Preview> ReadNode(object state, int node, string type, bool gpuParticles)
    {
        object? texture = null, material = null, frames = null, process = null;
        int count = (int)getPropertyCount.Invoke(state, [node])!;
        for (int property = 0; property < count; property++)
        {
            using var nativeName = (IDisposable?)getPropertyName.Invoke(state, [node, property]);
            var name = nativeName?.ToString();
            if (name is not ("texture" or "material" or "sprite_frames" or "process_material")) continue;
            using var value = (IDisposable)getPropertyValue.Invoke(state, [node, property])!;
            if (!Equals(variantType.GetValue(value), objectVariant)) continue;
            var resource = asObject.Invoke(value, null);
            switch (name)
            {
                case "texture" when IsTexture(resource): texture = resource; break;
                case "material" when IsCanvasMaterial(resource): material = resource; break;
                case "sprite_frames" when spriteFramesType.IsInstanceOfType(resource): frames = resource; break;
                case "process_material" when CanPreviewParticles(resource): process = resource; break;
            }
        }

        if (type == "GPUParticles2D" && gpuParticles && process is not null)
            yield return new(texture, material, process);
        else if (frames is not null)
        {
            var names = (string[])getAnimationNames.Invoke(frames, null)!;
            foreach (var name in names.Take(2))
            {
                using var animation = (IDisposable)stringName.Invoke([name]);
                int length = (int)getFrameCount.Invoke(frames, [animation])!;
                if (length == 0) continue;
                if (getFrameTexture.Invoke(frames, [animation, 0]) is { } first) yield return new(first, material);
                if (length > 1 && getFrameTexture.Invoke(frames, [animation, length / 2]) is { } middle)
                    yield return new(middle, material);
            }
        }
        else if (texture is not null || material is not null)
            yield return new(texture, material);
    }
}
