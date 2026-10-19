using Godot;
using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Repro: a PackedScene loaded without the cache can be freed while Instantiate() is still running (see README.md).
/// Run it with the buttons, or from the command line: GodotGcRepro.exe -- --repro=[instantiate|instantiate-keepalive]
/// </summary>
public partial class Main : Control
{
    const string ScenePath = "res://InstantiatedScene.tscn";
    const int Iterations = 20;

    RichTextLabel output;
    bool isCommandLineRun;

    public override void _Ready()
    {
        BuildUi();

        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--repro="))
                continue;

            isCommandLineRun = true;
            CallDeferred(MethodName.RunFromCommandLine, arg["--repro=".Length..]);
        }
    }

    void RunFromCommandLine(string repro)
    {
        switch (repro)
        {
            case "instantiate": Run(keepAlive: false); break;
            case "instantiate-keepalive": Run(keepAlive: true); break;
            default:
                Log($"Unknown repro \"{repro}\"");
                GetTree().Quit(2);
                break;
        }
    }

    void Run(bool keepAlive)
    {
        Log($"Loading and instantiating {ScenePath} {Iterations} times (GC.KeepAlive: {keepAlive})");

        for (int i = 1; i <= Iterations; i++)
        {
            Log($"  Instantiate {i}");
            var node = keepAlive ? LoadAndInstantiateKeptAlive(ScenePath) : LoadAndInstantiate(ScenePath);
            AddChild(node);
            node.Free();
        }

        Log("Done, no crash");
        if (isCommandLineRun)
            GetTree().Quit(0);
    }

    // The bug: after Instantiate() has read the native pointer, nothing uses `scene` any more. In optimized code the GC
    // may collect the wrapper while Instantiate() is still running, and its finalizer frees the (uncached) PackedScene.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Node LoadAndInstantiate(string path)
    {
        var scene = (PackedScene)ResourceLoader.Load(path, "PackedScene", ResourceLoader.CacheMode.IgnoreDeep);
        return scene.Instantiate();
    }

    // The workaround: a use of `scene` after Instantiate() keeps the wrapper alive until it has finished
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Node LoadAndInstantiateKeptAlive(string path)
    {
        var scene = (PackedScene)ResourceLoader.Load(path, "PackedScene", ResourceLoader.CacheMode.IgnoreDeep);
        var node = scene.Instantiate();
        GC.KeepAlive(scene);
        return node;
    }

    void BuildUi()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 16);
        AddChild(margin);

        var box = new VBoxContainer();
        margin.AddChild(box);

        box.AddChild(new Label { Text = "Instantiate() on an uncached PackedScene (run an AOT export, see README.md)" });
        AddButton(box, "Instantiate without GC.KeepAlive (crashes when running an export)", () => Run(keepAlive: false));
        AddButton(box, "Instantiate with GC.KeepAlive (works when running an export)", () => Run(keepAlive: true));

        output = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, ScrollFollowing = true };
        box.AddChild(output);
    }

    static void AddButton(Container parent, string text, Action action)
    {
        var button = new Button { Text = text };
        button.Pressed += action;
        parent.AddChild(button);
    }

    void Log(string text)
    {
        GD.Print(text);
        output.AppendText(text + "\n");
    }
}
