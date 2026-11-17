# Godot C#: bindings that fail under garbage collection

A Godot (.NET) project with repros of Godot's C# bindings failing when the .NET GC runs at the wrong moment, and the evidence for one we found in a game but can't reproduce reliably yet. Only bugs that still exist are here:

1. **"Handle is not initialized" with `CacheMode.Reuse`:** fails within seconds under GC stress. **Still open in Godot 4.8 beta 3.** Related issues: [godotengine/godot#112067](https://github.com/godotengine/godot/issues/112067) and [godotengine/godot#83762](https://github.com/godotengine/godot/issues/83762)
2. **"Handle is not initialized" for a cached dependency with `CacheMode.Ignore`:** the scene is loaded fresh, but a texture it uses comes from the cache, and C# reads it from the sprite. Fails within seconds under GC stress, thousands of times per run. **Still open in Godot 4.8 beta 3.** This is the case in [godotengine/godot#112067](https://github.com/godotengine/godot/issues/112067).
3. **Disposing a `StringName` (or `NodePath`, Godot collections) passes Godot an unpinned pointer into the C# object:** crashed a game three times, on the same instruction, in runs with GC stress. **Still in Godot's source on `master`.** No reliable repro yet. Reported as [godotengine/godot#125385](https://github.com/godotengine/godot/issues/125385), which was closed unread because it was written with AI; it needs a report written by a person.

The project targets Godot 4.8 beta 3. An earlier repro, a `PackedScene` freed during `Instantiate()`, was fixed in 4.8 beta 2 by [godotengine/godot#124714](https://github.com/godotengine/godot/pull/124714) and has been removed; it's in commit [`697db0a`](https://github.com/klootas/GodotGcRepro/tree/697db0a) (and for Godot 4.7.2 in [`9aecc58`](https://github.com/klootas/GodotGcRepro/tree/9aecc58)).

Run the repros with the buttons, or from the command line: `GodotGcRepro.exe --headless -- --repro=<name>`, optionally with `--seconds=N` (20 by default). Exit code 0 means no error was logged, 1 that one was. Run from the editor, the errors show in the Debugger tab but aren't counted: Godot sends C# exceptions straight to the debugger when it's active (`ExceptionUtils.LogException`), past every logger.

# 1. "Handle is not initialized" with `CacheMode.Reuse` (reproducible)

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

Export with the "Windows Desktop" preset (AOT, which needs the .NET export templates and the Visual Studio C++ build tools; it fails in a regular JIT export too, without `PublishAot`), or run `.\export_and_run.ps1 -GodotExe "<path to Godot_v4.8-beta3_mono_win64.exe>"`, which exports and runs both variants headless. Then:

- `GodotGcRepro.exe --headless -- --repro=reuse`: logs "Handle is not initialized" and then usually aborts (exit code `-1073740286`), or exits with code 1 if it survives to the end. Occasionally it hangs instead and never exits (a normal run takes 20 seconds).
- `GodotGcRepro.exe --headless -- --repro=reuse-keepalive`: keeps a reference to the loaded scene. About 90,000 loads, no errors, exit code 0.

In our runs on Godot 4.7.2: 10 out of 10 failed without the workaround (4 logged the error, 5 aborted, 1 hung), and 10 out of 10 ran cleanly with it.

On Godot 4.8 beta 2, with the fix for the `Instantiate()` bug (see the top of this file), it still fails: 6 out of 6 runs logged 44 to 119 "Handle is not initialized" errors each, and 3 of them didn't survive (2 aborted with `-1073740286`, 1 crashed with an access violation, `-1073741819`). With the workaround, 111,950 loads ran with no errors. So keeping the instance alive during native calls doesn't cover this race. Two runs on Nov 17, 2026 (4.8 beta 2, AOT, .NET 10): `reuse` logged the error in `ResourceLoader.Load` both times, aborting once (exit code `-1073740791`) and exiting with 1 once, and `reuse-keepalive` ran 134,750 and 138,100 loads with no errors.

On Godot 4.8 beta 3 (AOT, .NET 10, Nov 17, 2026) it still fails: 4 out of 4 runs logged 35 to 244 "Handle is not initialized" errors each, in `ResourceLoader.Load`, and 2 of them aborted (`-1073740286`). With the workaround, 99,050 loads ran with no errors.

## The workaround

Keep a C# reference to resources you load or fetch from Godot repeatedly, so their wrapper is never collected while the object is still in use. Here: keep the loaded `PackedScene` in a field. Or avoid the cache for it (`CacheMode.Ignore`), so every load gives a fresh object with a fresh wrapper. Its dependencies still come from the cache, though, and the same race hit a game for those: a `TileSet` every instance of a scene shared, read from C# as each new one was instantiated, and textures read from sprites at a scene change while the next scene loaded the same ones. So don't read a shared resource back from Godot repeatedly either, or keep the wrappers you read.

## Possible fix in Godot

Handing C# an object whose previous wrapper is being collected or finalized needs to be safe: either create a new wrapper when the old one is gone, or keep the old one alive until the handover is done.

We haven't verified this in the engine source, but the discussion in #112067 points at the same places: `ScriptManagerBridge.SwapGCHandleForType`, where the exception is thrown, and `CSharpInstance::refcount_incremented()` together with the `// TODO: Possible data race?` in `godotsharp_internal_unmanaged_instance_binding_create_managed()`. Proposed fixes in forks are linked from the issue.

This project can be used to verify a fix: with it, `--repro=reuse` should run its 20 seconds without "Handle is not initialized", as `--repro=reuse-keepalive` already does.

# 2. "Handle is not initialized" for a cached dependency with `CacheMode.Ignore` (reproducible)

## The bug

```csharp
var scene = (PackedScene)ResourceLoader.Load(path, "PackedScene", ResourceLoader.CacheMode.Ignore);
var sprite = scene.Instantiate<Sprite2D>();
var texture = sprite.Texture;  // a new wrapper for the cached texture, dropped after use
```

`Ignore` loads the scene itself fresh, but its dependencies (here `Dot.png`) still come from the cache, so every copy of the scene uses the same texture. Reading it from C# makes a wrapper for it, which is dropped after use and collected later. Loading and instantiating the next copy adds a reference to the texture, which swaps that wrapper's weak handle for a strong one (`ScriptManagerBridge.SwapGCHandleForType`); if the wrapper is being collected or finalized right then, it fails as in repro 1:

```
ERROR: System.InvalidOperationException: Handle is not initialized.
   at Godot.Bridge.ScriptManagerBridge.SwapGCHandleForType(IntPtr oldGCHandlePtr, IntPtr* outNewGCHandlePtr, godot_bool createWeak)
   C# backtrace (most recent call first):
       [5] Godot.Resource Godot.ResourceLoader.Load(string, string, Godot.ResourceLoader+CacheMode)
   (or [5] Godot.Node Godot.PackedScene.Instantiate(Godot.PackedScene+GenEditState))
```

So `CacheMode.Ignore` (or `IgnoreDeep` for the scene only) is no way around repro 1 as soon as C# reads anything the copies share. A game hit it for a `TileSet` that every copy of a scene shared, read from C# as each new one was instantiated, and for textures read from sprites at a scene change while the next scene loaded the same ones.

## Running it

- `--repro=dependency`: loads `SpriteScene.tscn` (a `Sprite2D` with `Dot.png`) with `CacheMode.Ignore` 50 times per frame, reading the texture each time. Exits with code 1.
- `--repro=dependency-keepalive`: keeps the texture's wrapper in a field. No errors, exit code 0.

On Godot 4.8 beta 3 (AOT, .NET 10, Nov 17, 2026): 4 out of 4 runs logged 1,975 to 3,572 "Handle is not initialized" errors each, in `ResourceLoader.Load` and `PackedScene.Instantiate`. With the workaround, 107,400 loads ran with no errors.

## The workaround

Don't read a shared resource back from Godot repeatedly: keep its wrapper the first time (in a field, or a set that lives for the whole run), or get what you need without the resource (a tile layer's tile size from `MapToLocal` rather than its `TileSet`, a sprite's texture name from your own data rather than `sprite.Texture`).

# 3. Disposing a `StringName` passes Godot an unpinned pointer (no reliable repro yet)

## The bug

Disposing a `StringName`, by `Dispose()` or by its finalizer, ends in a native call that gets a raw pointer to the `godot_string_name` value inside the C# object:

```
StringName.Dispose(bool)
  -> NativeValue.DangerousSelfRef.Dispose()          // a ref to the struct field inside the StringName object
	-> NativeFuncs.godotsharp_string_name_destroy(ref godot_string_name p_self)
		 godot_string_name* p_self_ptr = (godot_string_name*)CustomUnsafe.AsPointer(ref p_self);
		 // ...native call with p_self_ptr
```

The conversion is generated by `UnmanagedCallbacksGenerator.AppendCustomUnsafeAsPointer` (`modules/mono/glue/GodotSharp/Godot.SourceGenerators.Internal/UnmanagedCallbacksGenerator.cs` on `master`) for every by-ref parameter of a Godot interop struct. It's a plain address (`value.GetUnsafeAddress()` in `CustomUnsafe.cs`), not a `fixed` statement, so nothing pins the object. When the struct lives on the stack that's fine. When it's a field of a managed object, which is the case for `StringName`, `NodePath` and the Godot collections, the GC may move the object once the thread enters native code (the transition lets a waiting GC run), and Godot then reads the old address.

## What we saw

A game (Godot 4.7.2, a VS run on CoreCLR .NET 11.0.0, with a background thread running `GC.Collect()` and the process on two cores) crashed three times within an hour, each time while disposing a node's name it had just read:

```csharp
using (var childName = child.Name)
	isMatch = childName == stringName;
```

The .NET runtime reports it as an access violation in `coreclr.dll` (`coreclr!ProcessCLRException+0x164`, the same offset every time), which is its handler ending the process. With WinDbg (`cdb` with Microsoft's public symbols), the first exception is in Godot:

```
08 Godot_v4_7_2_stable_mono_win64!NoHotPatch+0xda9be9              <- godotsharp_string_name_destroy
09 GodotSharp!ILStubClass.IL_STUB_PInvoke(Godot.NativeInterop.godot_string_name*)+0x8d
0a GodotSharp!Godot.NativeInterop.NativeFuncs.godotsharp_string_name_destroy+0x60
0b GodotSharp!Godot.NativeInterop.godot_string_name.Dispose+0x46
0c GodotSharp!Godot.StringName.Dispose+0x4b
0d GodotSharp!Godot.StringName.Dispose+0x2d
0e Tanagra_Godot!Tanagra.Godot.SceneHandler.FindNode<...Character>+0x7b7
```

```
00007ff6`6213c810 803d091f9a0500  cmp     byte ptr [...],0          ; StringName::configured
00007ff6`6213c817 740f            je      ...
00007ff6`6213c819 48833900        cmp     qword ptr [rcx],0         ; p_self->_data, rcx = p_self
																	; ds:000002b3`811d4578=????????????????
```

`rcx` is the pointer the C# side passed, and the memory it points to isn't mapped any more. The managed wrapper that computed it (Godot 4.7.2's `GodotSharp`, JIT, from the same dump) takes the address first and calls native code after:

```
Godot.NativeInterop.NativeFuncs.godotsharp_string_name_destroy(Godot.NativeInterop.godot_string_name ByRef)
00007fff`6e4b1f2e 488b4d10        mov     rcx,qword ptr [rbp+10h]        ; ref p_self (tracked by the GC)
00007fff`6e4b1f32 ff15a8553e00    call    CustomUnsafe.AsPointer(godot_string_name ByRef)
00007fff`6e4b1f38 488945e0        mov     qword ptr [rbp-20h],rax        ; the raw pointer (not tracked)
00007fff`6e4b1f3c 488b45e0        mov     rax,qword ptr [rbp-20h]
00007fff`6e4b1f40 488945f0        mov     qword ptr [rbp-10h],rax
00007fff`6e4b1f44 48b858641a12b3020000 mov rax,2B3121A6458h
00007fff`6e4b1f4e 488b8010040000  mov     rax,qword ptr [rax+410h]       ; the native function
00007fff`6e4b1f55 488945e8        mov     qword ptr [rbp-18h],rax
00007fff`6e4b1f59 4c8b55e8        mov     r10,qword ptr [rbp-18h]
00007fff`6e4b1f5d 488b4df0        mov     rcx,qword ptr [rbp-10h]        ; p_self_ptr
00007fff`6e4b1f61 49bb987d7d6eff7f0000 mov r11,7FFF6E7D7D98h
00007fff`6e4b1f6b e83044ba5f      call    coreclr!GenericPInvokeCalliHelper
```

The GC heap was intact (`verifyheap`: 74,178 objects, no errors) and a GC was in progress on the stress thread. The same game also crashed with `ArgumentException: Disposable not registered` in `DisposablesTracker.UnregisterDisposable` from `StringName.Finalize` and `NodePath.Finalize`, and earlier with an access violation in `godotsharp_array_size` while reading a `Godot.Collections.Array`. Both fit the same hole, but we haven't shown that they are.

## Why there's no repro

The crash needs the old memory to be gone or reused before Godot reads it. Godot's native code normally runs within nanoseconds of the transition, while the GC it lets run takes far longer to move anything, so the read nearly always sees the old, still valid copy. It only fails when the thread is preempted right there. A loop reading and disposing millions of node names under the same GC stress (on one and two cores, with a 64 kB gen0 and a thread allocating all the time, JIT and AOT, Godot 4.8 beta 2) ran 60 s without an error, while the game hit it within an hour.

## The workaround

Make no `StringName` or `NodePath` in code that runs often, and keep the ones you need for the whole run, so none is ever disposed: compare names with ones made once, look nodes up through a `NodePath` made once (`GetNodeOrNull(path)` on each node instead of reading every node's `Name`), and give nodes names from kept `StringName`s. A `string` argument is safe: Godot copies it into a native string on the stack, which doesn't move.

It was reported as [godotengine/godot#125385](https://github.com/godotengine/godot/issues/125385) (Godot 4.7.2 on Linux, crashes in finalizers of `Array`, `StringName` and `NodePath`), which was closed without being looked at because the report was written with AI. The Godot maintainers ask for a new report written by a person.

## Possible fix in Godot

Pin the object for the call: generate `fixed (godot_string_name* p_self_ptr = &p_self)` instead of `CustomUnsafe.AsPointer(ref p_self)`. A `fixed` on a ref to a field pins the object that holds it for as long as the pointer is used. Or keep the native value of `StringName`, `NodePath` and the collections in native memory rather than in a field of the managed object.

# Related issues and pull requests

Still open:
- [godotengine/godot#112067](https://github.com/godotengine/godot/issues/112067): "Handle is not initialized" when instantiating a `PackedScene` that uses a cached texture. Repros 1 and 2.
- [godotengine/godot#83762](https://github.com/godotengine/godot/issues/83762): random crashes on scene changes with `FATAL: Condition "gchandle.is_released()" is true`, the abort that can follow repro 1.
- [godotengine/godot#123707](https://github.com/godotengine/godot/issues/123707) (confirmed): `ArgumentException: Disposable not registered` from a finalizer (`Variant`'s `Disposer`), the same error a game got from `StringName.Finalize` and `NodePath.Finalize` (repro 3). [godotengine/godot#125168](https://github.com/godotengine/godot/pull/125168) (open pull request) makes `Dispose` safe to call twice and throws on use after dispose.

Closed, but describing these bugs:
- [godotengine/godot#125385](https://github.com/godotengine/godot/issues/125385): the unpinned pointers of repro 3, closed because it was written with AI.
- [godotengine/godot#124498](https://github.com/godotengine/godot/issues/124498) and [godotengine/godot#124587](https://github.com/godotengine/godot/issues/124587): the broken binding state behind "Handle is not initialized" (a weak handle whose target is gone) and the abort that follows, with the code paths in `csharp_script.cpp`, both closed as not planned.
- [godotengine/godot#125198](https://github.com/godotengine/godot/issues/125198): wrappers passed as arguments aren't kept alive during native calls, which [godotengine/godot#124714](https://github.com/godotengine/godot/pull/124714) doesn't cover. Closed as fixed by #124714, but its reporter still reproduced it on `master` afterwards. A loop passing new `StyleBoxFlat`s to `AddThemeStyleboxOverride` ran 20 s here without an error, so there's no repro of it in this project.
- [godotengine/godot#121018](https://github.com/godotengine/godot/issues/121018): a duplicate of #112067.
- [godotengine/godot#109798](https://github.com/godotengine/godot/issues/109798) and [godotengine/godot#124714](https://github.com/godotengine/godot/pull/124714): the `PackedScene` freed during `Instantiate()`, fixed in 4.8 beta 2.
