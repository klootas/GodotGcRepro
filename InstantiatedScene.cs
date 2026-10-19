using Godot;
using System;

/// <summary>
/// Root script of the scene that repro 1 instantiates. Its constructor runs in the middle of PackedScene.Instantiate(),
/// while only native code is still using the PackedScene. In a real game any allocation in a script constructor or
/// field initializer can trigger a GC at this point; here we force one so it happens every time.
/// </summary>
public partial class InstantiatedScene : Node2D
{
    public InstantiatedScene()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
