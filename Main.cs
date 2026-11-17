using Godot;
using System;

/// <summary>
/// Repro of Godot's C# bindings failing under garbage collection (see README.md). Each runs for a while under GC stress.
/// Run them with the buttons, or from the command line: GodotGcRepro.exe --headless -- --repro=&lt;name&gt; [--seconds=N]
/// (names in Repros below).
/// </summary>
public partial class Main : Control
{
    const string ReusedScenePath = "res://ReusedScene.tscn";
    const string SpriteScenePath = "res://SpriteScene.tscn";
    static double durationSeconds = 20;

    record Repro(string Name, string Description, int StepsPerFrame, Action<Main> Step);

    static readonly Repro[] Repros =
    [
        // Loading the same scene with CacheMode.Reuse over and over. Each load hands back the same cached PackedScene. C# drops
        // its wrapper after each use, so the GC can collect it; if the next load hands the scene back while that wrapper is
        // being finalized, Godot fails to reuse it ("Handle is not initialized", godotengine/godot#112067)
        new("reuse", "Load a cached PackedScene (CacheMode.Reuse) without keeping a reference", 50, o => o.LoadReused(keep: false)),
        new("reuse-keepalive", "Load a cached PackedScene while keeping a reference (the workaround)", 50, o => o.LoadReused(keep: true)),

        // A dependency from the cache, with CacheMode.Ignore: the scene is fresh, but its texture is the cached one. Reading the
        // texture from C# makes a wrapper that's dropped at once; instantiating the next copy adds a reference to the texture
        // while that wrapper may be being finalized (the case in godotengine/godot#112067)
        new("dependency", "Load a scene with CacheMode.Ignore and read its cached texture from C#", 50, o => o.LoadWithDependency(keep: false)),
        new("dependency-keepalive", "The same, keeping the texture's wrapper (the workaround)", 50, o => o.LoadWithDependency(keep: true)),

    ];

    RichTextLabel output;
    bool isCommandLineRun;

    Repro running;
    double timeLeft;
    long steps;
    PackedScene keptScene;
    Texture2D keptTexture;
    static int errorsLogged;
    static System.Threading.Thread gcThread;

    public override void _Ready()
    {
        BuildUi();

        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--seconds="))
                durationSeconds = double.Parse(arg["--seconds=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            if (!arg.StartsWith("--repro="))
                continue;

            isCommandLineRun = true;
            CallDeferred(MethodName.RunFromCommandLine, arg["--repro=".Length..]);
        }
    }

    void RunFromCommandLine(string name)
    {
        var repro = Array.Find(Repros, o => o.Name == name);
        if (repro == null)
        {
            Log($"Unknown repro \"{name}\"");
            GetTree().Quit(2);
            return;
        }

        Start(repro);
    }

    void Start(Repro repro)
    {
        if (running != null)
            return;

        StartGcThread();
        timeLeft = durationSeconds;
        steps = 0;
        keptScene = null;
        System.Threading.Interlocked.Exchange(ref errorsLogged, 0);
        running = repro;

        Log($"{repro.Name}: {repro.Description}, {repro.StepsPerFrame} times per frame for {durationSeconds} s, while a background thread collects garbage");
    }

    public override void _Process(double delta)
    {
        if (running == null)
            return;

        for (int i = 0; i < running.StepsPerFrame; i++)
            running.Step(this);
        steps += running.StepsPerFrame;

        timeLeft -= delta;
        if (timeLeft > 0)
            return;

        var errors = System.Threading.Volatile.Read(ref errorsLogged);
        if (EngineDebugger.IsActive())
            Log($"Done: {steps} steps. Errors aren't counted when run from the editor: see the Debugger tab");
        else
            Log($"Done: {steps} steps, {errors} errors logged");
        running = null;

        if (isCommandLineRun)
            GetTree().Quit(errors > 0 ? 1 : 0);
    }

    void LoadReused(bool keep)
    {
        var scene = (PackedScene)ResourceLoader.Load(ReusedScenePath, "PackedScene", ResourceLoader.CacheMode.Reuse);
        if (keep)
            keptScene = scene; // The workaround: a wrapper that stays referenced is never finalized

        var node = scene.Instantiate();
        GC.KeepAlive(scene);
        AddChild(node);
        node.Free();
    }

    void LoadWithDependency(bool keep)
    {
        var scene = (PackedScene)ResourceLoader.Load(SpriteScenePath, "PackedScene", ResourceLoader.CacheMode.Ignore);
        var sprite = scene.Instantiate<Sprite2D>();
        GC.KeepAlive(scene);
        AddChild(sprite);
        var texture = sprite.Texture;
        if (keep)
            keptTexture = texture;
        _ = texture.GetSize();
        sprite.Free();
        scene.Dispose();
    }

    static void StartGcThread()
    {
        if (gcThread != null)
            return;

        OS.AddLogger(new ErrorCounter());
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

    // Godot catches exceptions at the native boundary and logs them, so count them there. Run from the editor, it sends them to
    // the debugger instead (ExceptionUtils.LogException), past every logger, so nothing is counted then
    partial class ErrorCounter : Logger
    {
        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            System.Threading.Interlocked.Increment(ref errorsLogged);
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

        box.AddChild(new Label { Text = "Each runs for 20 s under GC stress (see README.md)", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        foreach (var repro in Repros)
        {
            var button = new Button { Text = $"{repro.Name}: {repro.Description}" };
            var captured = repro;
            button.Pressed += () => Start(captured);
            box.AddChild(button);
        }

        output = new RichTextLabel { SizeFlagsVertical = SizeFlags.ExpandFill, ScrollFollowing = true };
        box.AddChild(output);
    }

    void Log(string text)
    {
        GD.Print(text);
        output.AppendText(text + "\n");
    }
}
