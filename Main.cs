using Godot;
using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Repros of C# wrappers being collected while Godot still uses the object (see README.md).
/// Run them with the buttons, or from the command line: GodotGcRepro.exe -- --repro=[instantiate|instantiate-keepalive|reuse|reuse-keepalive]
/// </summary>
public partial class Main : Control
{
    const string ScenePath = "res://InstantiatedScene.tscn";
    const int Iterations = 20;

    const string ReusedScenePath = "res://ReusedScene.tscn";
    const double ReuseDurationSeconds = 20;
    const int ReuseLoadsPerFrame = 50;

    RichTextLabel output;
    bool isCommandLineRun;

    // Reuse repro state
    bool isReusing;
    bool reuseKeepAlive;
    double reuseTimeLeft;
    long reuseLoads;
    PackedScene keptScene;
    static int handleErrorsLogged;
    static System.Threading.Thread gcThread;

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
            case "reuse": StartReuse(keepAlive: false); break;
            case "reuse-keepalive": StartReuse(keepAlive: true); break;
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

    // Repro 2: loading the same scene with CacheMode.Reuse over and over, while a background thread collects garbage.
    // Each load hands back the same cached PackedScene. C# drops its wrapper after each use, so the GC can collect it;
    // if the next load hands the scene back while that wrapper is being finalized, Godot fails to reuse it
    // ("Handle is not initialized", godotengine/godot#112067)
    void StartReuse(bool keepAlive)
    {
        if (isReusing)
            return;

        StartGcThread();
        reuseKeepAlive = keepAlive;
        reuseTimeLeft = ReuseDurationSeconds;
        reuseLoads = 0;
        keptScene = null;
        System.Threading.Interlocked.Exchange(ref handleErrorsLogged, 0);
        isReusing = true;

        Log($"Loading {ReusedScenePath} with CacheMode.Reuse {ReuseLoadsPerFrame} times per frame for {ReuseDurationSeconds} s, while a background thread collects garbage (keep a reference: {keepAlive})");
    }

    public override void _Process(double delta)
    {
        if (!isReusing)
            return;

        for (int i = 0; i < ReuseLoadsPerFrame; i++)
        {
            var scene = (PackedScene)ResourceLoader.Load(ReusedScenePath, "PackedScene", ResourceLoader.CacheMode.Reuse);
            if (reuseKeepAlive)
                keptScene = scene; // The workaround: a wrapper that stays referenced is never finalized

            var node = scene.Instantiate();
            GC.KeepAlive(scene); // Not repro 1
            AddChild(node);
            node.Free();
            reuseLoads++;
        }

        reuseTimeLeft -= delta;
        if (reuseTimeLeft > 0)
            return;

        isReusing = false;
        var errors = System.Threading.Volatile.Read(ref handleErrorsLogged);
        Log($"Done: {reuseLoads} loads, {errors} \"Handle is not initialized\" errors");

        if (isCommandLineRun)
            GetTree().Quit(errors > 0 ? 1 : 0);
    }

    static void StartGcThread()
    {
        if (gcThread != null)
            return;

        OS.AddLogger(new HandleErrorCounter());
        gcThread = new System.Threading.Thread(() =>
        {
            while (true)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                System.Threading.Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
            Name = "GC stress",
        };
        gcThread.Start();
    }

    // Godot catches the exception at the native boundary and logs it, so count it there
    partial class HandleErrorCounter : Logger
    {
        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            if ((rationale + code).Contains("Handle is not initialized"))
                System.Threading.Interlocked.Increment(ref handleErrorsLogged);
        }
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

        box.AddChild(new Label { Text = "Instantiate() on an uncached PackedScene (run an export, see README.md). Fixed in Godot 4.8 beta 2: before that, AOT crashed on the first click, and a regular (JIT) export after a few clicks, once the code was optimized", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        AddButton(box, "Instantiate without GC.KeepAlive (crashed before 4.8 beta 2)", () => Run(keepAlive: false));
        AddButton(box, "Instantiate with GC.KeepAlive (works, however often you click)", () => Run(keepAlive: true));

        box.AddChild(new Label { Text = "Loading a cached PackedScene (CacheMode.Reuse) over and over, under GC stress (still fails in 4.8 beta 2)", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        AddButton(box, "Load without keeping a reference (\"Handle is not initialized\", may abort)", () => StartReuse(keepAlive: false));
        AddButton(box, "Load while keeping a reference (works)", () => StartReuse(keepAlive: true));

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
