# Godot C#: wrappers collected while Godot still uses the object

A minimal Godot 4.7.2 (.NET) project with two reliable repros of C# wrappers being garbage collected at the wrong moment:

1. **`PackedScene` freed during `Instantiate()`:** crashes on the first call. Related issue: [godotengine/godot#109798](https://github.com/godotengine/godot/issues/109798)
2. **"Handle is not initialized" in `ResourceLoader.Load` with `CacheMode.Reuse`:** fails within seconds under GC stress. Related issue: [godotengine/godot#112067](https://github.com/godotengine/godot/issues/112067)

Both need an AOT export (see "Running it").

# Repro 1: `PackedScene` freed during `Instantiate()`

## The bug

```csharp
var scene = (PackedScene)ResourceLoader.Load(path, "PackedScene", ResourceLoader.CacheMode.IgnoreDeep);
return scene.Instantiate();
```

1. With `CacheMode.IgnoreDeep` (or `Ignore`) the resource cache doesn't hold the scene, so the C# wrapper is its only owner.
2. The generated binding for `Instantiate()` reads the native pointer from `scene` and calls into the engine. After that, nothing uses `scene` any more. In optimized code, .NET treats it as dead from that point on, even though the native call hasn't returned yet.
3. `Instantiate()` runs the script constructors of the scene's nodes. If a GC happens there (any allocation can trigger one), the wrapper is collected. Its finalizer releases the last reference, and the `SceneState` is freed while `Instantiate()` is still reading it.

The result is reads from freed memory, for example:

```
ERROR: Cannot get class ''.
   at: _instantiate_internal (core/object/class_db.cpp:587)
WARNING: Node  of type  cannot be created. A placeholder will be created instead.
ERROR: FATAL: Index p_index = 2 is out of bounds (size() = 0).
```

The process then usually crashes with an access violation (`0xC0000005`).

It doesn't happen in Debug builds or with a debugger attached, because locals are kept alive until the method returns there. With the regular JIT it is rare, because rarely called methods run unoptimized at first (tier 0). AOT builds are fully optimized from the start, so that's where it shows up.

## How this project makes it reliable

The root script of `InstantiatedScene.tscn` forces a GC in its constructor (`InstantiatedScene.cs`). That puts a collection exactly inside the window, so it crashes on the first `Instantiate()` instead of once in a while.

## Running it

It needs an AOT export (the project has `PublishAot` set), which requires the Godot 4.7.2 .NET export templates and the Visual Studio C++ build tools. The contrast with the editor is part of the repro:

1. Run the project from the editor and click "Instantiate without GC.KeepAlive". It works: in a Debug build every local stays alive until its method returns, so the window never opens.
2. Export with the "Windows Desktop" preset, or run `.\export_and_run.ps1 -GodotExe "<path to Godot_v4.7.2-stable_mono_win64.exe>"`, which exports and runs both repros headless, without and with their workarounds.
3. Start `Export\GodotGcRepro.exe` and click the buttons, or run it from the command line:
   - `GodotGcRepro.exe --headless -- --repro=instantiate`: crashes on the first iteration (exit code `-1073741819`, an access violation).
   - `GodotGcRepro.exe --headless -- --repro=instantiate-keepalive`: runs all 20 iterations, exit code 0.

In our runs: 5 out of 5 crashed without the workaround, and 5 out of 5 ran cleanly with it.

## The workaround

Keep the wrapper alive until `Instantiate()` has returned:

```csharp
var node = scene.Instantiate();
GC.KeepAlive(scene);
```

`GC.KeepAlive` does nothing at runtime. Its only effect is that `scene` counts as used until that line, so the GC can't collect it during the call. This is the documented pattern for objects whose native handle is still in use ([GC.KeepAlive](https://learn.microsoft.com/dotnet/api/system.gc.keepalive)).

## Possible fix in Godot

The generated C# bindings could call `GC.KeepAlive(this)` (and the same for wrapper arguments) after each native call. That would close this window for every method, not just `Instantiate()`.

# Repro 2: "Handle is not initialized" with `CacheMode.Reuse`

## The bug

```csharp
var scene = (PackedScene)ResourceLoader.Load(path, "PackedScene", ResourceLoader.CacheMode.Reuse);
var node = scene.Instantiate();
// ...the wrapper `scene` is dropped, and the same path is loaded again later
```

1. Every load of the same path hands C# the same cached `PackedScene`.
2. After each use, C# drops its wrapper, so the GC may collect it at any time (the cache doesn't keep the wrapper alive).
3. If a load hands the scene back while its previous wrapper is being collected or finalized, Godot tries to reuse that wrapper and fails:

```
ERROR: System.InvalidOperationException: Handle is not initialized.
   C# backtrace (most recent call first):
       [4] Godot.GodotObject Godot.NativeCalls.godot_icall_3_1268(nint, nint, string, string, int)
       [5] Godot.Resource Godot.ResourceLoader.Load(string, string, Godot.ResourceLoader+CacheMode)
```

`Load` then returns nothing usable, and the process often aborts shortly after (exit code `-1073740286`, a fail-fast).

This is the pattern of a game creating, say, choice buttons from the same scene file over and over. We first saw it there, in a real game.

## How this project makes it reliable

It loads `ReusedScene.tscn` (a single button) 50 times per frame for 20 seconds, while a background thread runs `GC.Collect()` and `GC.WaitForPendingFinalizers()` in a loop. That makes collections and finalizers land at any point of the main thread's work, so the race shows up within seconds instead of after hours of play.

## Running it

Same export as repro 1, then:

- `GodotGcRepro.exe --headless -- --repro=reuse`: logs "Handle is not initialized" and then usually aborts (exit code `-1073740286`), or exits with code 1 if it survives to the end.
- `GodotGcRepro.exe --headless -- --repro=reuse-keepalive`: keeps a reference to the loaded scene. About 90,000 loads, no errors, exit code 0.

In our runs: 5 out of 5 failed without the workaround (3 aborted, 2 logged the error), and 5 out of 5 ran cleanly with it.

## The workaround

Keep a C# reference to resources you load or fetch from Godot repeatedly, so their wrapper is never collected while the object is still in use. Here: keep the loaded `PackedScene` in a field. Or avoid the cache for it (`CacheMode.Ignore`), so every load gives a fresh object with a fresh wrapper.

## Possible fix in Godot

Handing out an object whose existing wrapper is being finalized needs to be safe: either create a new wrapper when the old one is gone, or keep the old one alive until the swap is done. Proposed fixes in forks are linked from #112067.
