using System.Reflection;

namespace Launcher.Host;

// Native launcher buttons feed the same Godot input path as a physical keyboard.
internal sealed class GameKeyboard
{
    readonly record struct Stroke(object Code, int Modifiers);
    static GameKeyboard? current;
    static readonly string[] ModifierKeys = ["Shift", "Ctrl", "Alt", "Meta"];
    static readonly string[] ModifierProperties = ["ShiftPressed", "CtrlPressed", "AltPressed", "MetaPressed"];

    readonly Queue<Stroke> queue = new();
    readonly object tree, root;
    readonly EventInfo processFrame;
    readonly Delegate frameCallback;
    readonly Type keyType, eventType;
    readonly MethodInfo parseEvent, isKeyPressed, flushEvents;
    readonly PropertyInfo code, physicalCode, pressed, guiDisableInput;
    readonly PropertyInfo[] modifiers;
    readonly object[] modifierCodes;
    Stroke? active;
    int activeFrames, ownedModifiers;
    bool watching;

    GameKeyboard(object menu, Assembly godot)
    {
        Type Godot(string name) => godot.GetType("Godot." + name, throwOnError: true)!;
        tree = Godot("Node").GetMethod("GetTree", Type.EmptyTypes)!.Invoke(menu, null)!;
        var sceneTree = Godot("SceneTree");
        root = sceneTree.GetProperty("Root")!.GetValue(tree)!;
        guiDisableInput = Godot("Viewport").GetProperty("GuiDisableInput")!;
        processFrame = sceneTree.GetEvent("ProcessFrame")!;
        frameCallback = Delegate.CreateDelegate(processFrame.EventHandlerType!, this, nameof(Frame));
        keyType = Godot("Key");
        eventType = Godot("InputEventKey");
        var input = Godot("Input");
        parseEvent = input.GetMethod("ParseInputEvent", [Godot("InputEvent")])!;
        isKeyPressed = input.GetMethod("IsKeyPressed", [keyType])!;
        flushEvents = input.GetMethod("FlushBufferedEvents", Type.EmptyTypes)!;
        code = eventType.GetProperty("Keycode")!;
        physicalCode = eventType.GetProperty("PhysicalKeycode")!;
        pressed = eventType.GetProperty("Pressed")!;
        modifiers = ModifierProperties.Select(name => eventType.GetProperty(name)!).ToArray();
        modifierCodes = ModifierKeys.Select(name => Enum.Parse(keyType, name)).ToArray();
    }

    internal static void Attach(object menu, Assembly godot) => current = new(menu, godot);
    internal static bool Send(string key, int modifiers) => current?.Enqueue(key, modifiers) == true;
    internal static void Reset() => current?.ReleaseAll();

    bool Enqueue(string key, int modifierMask)
    {
        if (modifierMask is < 0 or > 15 || queue.Count >= 16 ||
            !Enum.IsDefined(keyType, key) || key is "None" or "Unknown" || guiDisableInput.GetValue(root) is true)
            return false;
        queue.Enqueue(new(Enum.Parse(keyType, key), modifierMask));
        if (!watching)
        {
            processFrame.AddEventHandler(tree, frameCallback);
            watching = true;
        }
        return true;
    }

    public void Frame()
    {
        try
        {
            if (active is { })
            {
                if (--activeFrames > 0) return;
                ReleaseActive();
                StopIfIdle();
                return;
            }
            if (!queue.TryDequeue(out var next))
            {
                StopIfIdle();
                return;
            }

            active = next;
            activeFrames = 2;
            for (int i = 0; i < modifierCodes.Length; i++)
            {
                int bit = 1 << i;
                if ((next.Modifiers & bit) == 0 || isKeyPressed.Invoke(null, [modifierCodes[i]]) is true) continue;
                ownedModifiers |= bit;
                Emit(modifierCodes[i], ownedModifiers, down: true);
            }
            Emit(next.Code, next.Modifiers, down: true);
        }
        catch (Exception error)
        {
            HostDiagnostics.Error("send game keyboard event", error);
            ReleaseAll();
        }
    }

    void ReleaseActive()
    {
        if (active is not { } stroke) return;
        active = null;
        Emit(stroke.Code, stroke.Modifiers, down: false);
        for (int i = modifierCodes.Length - 1; i >= 0; i--)
        {
            int bit = 1 << i;
            if ((ownedModifiers & bit) == 0) continue;
            ownedModifiers &= ~bit;
            Emit(modifierCodes[i], ownedModifiers, down: false);
        }
    }

    void Emit(object key, int modifierMask, bool down)
    {
        using var inputEvent = (IDisposable)Activator.CreateInstance(eventType)!;
        code.SetValue(inputEvent, key);
        physicalCode.SetValue(inputEvent, key);
        pressed.SetValue(inputEvent, down);
        for (int i = 0; i < modifiers.Length; i++)
            modifiers[i].SetValue(inputEvent, (modifierMask & (1 << i)) != 0);
        parseEvent.Invoke(null, [inputEvent]);
    }

    void ReleaseAll()
    {
        if (!watching) return;
        queue.Clear();
        try
        {
            ReleaseActive();
            flushEvents.Invoke(null, null);
            StopIfIdle();
        }
        catch (Exception error) { HostDiagnostics.Error("release game keyboard", error); }
    }

    void StopIfIdle()
    {
        if (!watching || active is not null || queue.Count != 0) return;
        processFrame.RemoveEventHandler(tree, frameCallback);
        watching = false;
    }
}
