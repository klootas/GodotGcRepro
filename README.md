# Godot C#: wrappers collected while Godot still uses the object

A minimal Godot (.NET) project with two reliable repros of C# wrappers being garbage collected at the wrong moment:

1. **`PackedScene` freed during `Instantiate()`:** crashed on the first call. **Fixed in Godot 4.8 beta 2** by [godotengine/godot#124714](https://github.com/godotengine/godot/pull/124714). Related issue: [godotengine/godot#109798](https://github.com/godotengine/godot/issues/109798)
2. **"Handle is not initialized" in `ResourceLoader.Load` with `CacheMode.Reuse`:** fails within seconds under GC stress. **Still open in Godot 4.8 beta 2.** Related issues: [godotengine/godot#112067](https://github.com/godotengine/godot/issues/112067) and [godotengine/godot#83762](https://github.com/godotengine/godot/issues/83762)

The project targets Godot 4.8 beta 2. The version for Godot 4.7.2, where repro 1 still crashes, is commit [`9aecc58`](https://github.com/klootas/GodotGcRepro/tree/9aecc58). Repro 1 needed an AOT export to crash right away (see its "Running it"), while repro 2 fails in regular (JIT) release exports too.

# Repro 1: `PackedScene` freed during `Instantiate()` (fixed in Godot 4.8 beta 2)

This was fixed in Godot 4.8 beta 2 by [godotengine/godot#124714](https://github.com/godotengine/godot/pull/124714), which keeps the instance alive until each native call returns. The repro stays in the project as a regression test: on 4.8 beta 2 and later it should never crash. The description below is how it behaved before the fix (Godot 4.7.2 and earlier, see commit [`9aecc58`](https://github.com/klootas/GodotGcRepro/tree/9aecc58)).

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
```

The process then usually crashes with an access violation (`0xC0000005`).

In a real game we also saw `ERROR: FATAL: Index p_index = 2 is out of bounds (size() = 0).` (from `core/templates/cowdata.h`). It went away with the workaround below, so it's most likely the same bug: which error you get depends on what `Instantiate()` reads first from the freed scene. This project doesn't produce that one.

It doesn't happen in Debug builds or with a debugger attached, because locals are kept alive until the method returns there. With the regular JIT it is rare at first, because methods run unoptimized (tier 0) until they've been called a few dozen times. After that they're optimized and the window is open in a JIT build too (with tiered compilation turned off, a JIT export crashes right away like AOT). AOT builds are fully optimized from the start, so that's where it shows up immediately.

## How this project makes it reliable

The root script of `InstantiatedScene.tscn` forces a GC in its constructor (`InstantiatedScene.cs`). That puts a collection exactly inside the window, so it crashes on the first `Instantiate()` instead of once in a while.

## Running it

It needs an AOT export (the project has `PublishAot` set), which requires the .NET export templates for the Godot version and the Visual Studio C++ build tools. The contrast with the editor is part of the repro:

1. Run the project from the editor and click "Instantiate without GC.KeepAlive". It works: in a Debug build every local stays alive until its method returns, so the window never opens.
2. Export with the "Windows Desktop" preset, or run `.\export_and_run.ps1 -GodotExe "<path to Godot_v4.8-beta2_mono_win64.exe>"`, which exports and runs both repros headless, without and with their workarounds.
3. Start `Export\GodotGcRepro.exe` and click the buttons, or run it from the command line:
   - `GodotGcRepro.exe --headless -- --repro=instantiate`: on Godot 4.7.2 it crashed on the first iteration (exit code `-1073741819`, an access violation). On 4.8 beta 2 it runs all 20 iterations, exit code 0.
   - `GodotGcRepro.exe --headless -- --repro=instantiate-keepalive`: runs all 20 iterations, exit code 0.

In our runs on Godot 4.7.2: 5 out of 5 crashed without the workaround, and 5 out of 5 ran cleanly with it. On Godot 4.8 beta 2: 6 out of 6 ran cleanly without the workaround.

**It's not an AOT bug.** AOT only makes it show up right away. In a regular (JIT) export, without `PublishAot`, the first click usually doesn't crash, because the methods involved still run unoptimized (tier 0). Keep clicking "Instantiate without GC.KeepAlive" (each click instantiates the scene 20 times): once .NET has optimized the methods, it crashes there too. With `GC.KeepAlive` it never crashes, however often you click.

## The workaround (before Godot 4.8 beta 2)

Keep the wrapper alive until `Instantiate()` has returned:

```csharp
var node = scene.Instantiate();
GC.KeepAlive(scene);
```

`GC.KeepAlive` does nothing at runtime. Its only effect is that `scene` counts as used until that line, so the GC can't collect it during the call. This is the documented pattern for objects whose native handle is still in use ([GC.KeepAlive](https://learn.microsoft.com/dotnet/api/system.gc.keepalive)).

## The fix in Godot

[godotengine/godot#124714](https://github.com/godotengine/godot/pull/124714) makes the generated C# bindings call `GC.KeepAlive(this)` after each native call, which closes this window for every instance method, not just `Instantiate()`. Wrapper arguments aren't kept alive, so an object that's only passed as an argument (for example to a static method) still needs `GC.KeepAlive` after the call.

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

In #112067 the same exception is thrown from `PackedScene.Instantiate`, for a cached texture the scene references. It's the same failure: whenever Godot adds a reference to a cached object that has a C# wrapper, `CSharpInstance::refcount_incremented()` swaps the wrapper's weak handle for a strong one (`ScriptManagerBridge.SwapGCHandleForType`), and that fails if the wrapper was already collected. Here the cached object is the `PackedScene` itself, so it happens in `Load`. See also [godotengine/godot#124498](https://github.com/godotengine/godot/issues/124498) (the same failure branch, including the process abort) and [godotengine/godot#121018](https://github.com/godotengine/godot/issues/121018) (a duplicate).

This is the pattern of a game creating, say, choice buttons from the same scene file over and over. We first saw it there, in a real game.

## How this project makes it reliable

It loads `ReusedScene.tscn` (a single button) 50 times per frame for 20 seconds, while a background thread runs `GC.Collect()` and `GC.WaitForPendingFinalizers()` in a loop. That makes collections and finalizers land at any point of the main thread's work, so the race shows up within seconds instead of after hours of play.

## Running it

Same export as repro 1 (it also fails in a regular JIT export, without `PublishAot`), then:

- `GodotGcRepro.exe --headless -- --repro=reuse`: logs "Handle is not initialized" and then usually aborts (exit code `-1073740286`), or exits with code 1 if it survives to the end. Occasionally it hangs instead and never exits (a normal run takes 20 seconds).
- `GodotGcRepro.exe --headless -- --repro=reuse-keepalive`: keeps a reference to the loaded scene. About 90,000 loads, no errors, exit code 0.

In our runs on Godot 4.7.2: 10 out of 10 failed without the workaround (4 logged the error, 5 aborted, 1 hung), and 10 out of 10 ran cleanly with it.

On Godot 4.8 beta 2, with the fix for repro 1, it still fails: 6 out of 6 runs logged 44 to 119 "Handle is not initialized" errors each, and 3 of them didn't survive (2 aborted with `-1073740286`, 1 crashed with an access violation, `-1073741819`). With the workaround, 111,950 loads ran with no errors. So keeping the instance alive during native calls doesn't cover this race.

## The workaround

Keep a C# reference to resources you load or fetch from Godot repeatedly, so their wrapper is never collected while the object is still in use. Here: keep the loaded `PackedScene` in a field. Or avoid the cache for it (`CacheMode.Ignore`), so every load gives a fresh object with a fresh wrapper.

## Possible fix in Godot

Handing C# an object whose previous wrapper is being collected or finalized needs to be safe: either create a new wrapper when the old one is gone, or keep the old one alive until the handover is done.

We haven't verified this in the engine source, but the discussion in #112067 points at the same places: `ScriptManagerBridge.SwapGCHandleForType`, where the exception is thrown, and `CSharpInstance::refcount_incremented()` together with the `// TODO: Possible data race?` in `godotsharp_internal_unmanaged_instance_binding_create_managed()`. Proposed fixes in forks are linked from the issue.

This project can be used to verify a fix: with it, `--repro=reuse` should run its 20 seconds without "Handle is not initialized", as `--repro=reuse-keepalive` already does.
