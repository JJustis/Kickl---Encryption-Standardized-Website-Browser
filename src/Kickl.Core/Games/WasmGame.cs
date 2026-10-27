using Wasmtime;

namespace Kickl.Core.Games;

/// <summary>Hard ceilings for one game. Unclaimed names get the defaults; they can be raised, never removed.</summary>
public sealed record GameLimits(
    int MaxModuleBytes = 1 << 20,          // 1 MiB of compiled wasm
    long MaxMemoryBytes = 16L << 20,       // linear memory, including growth
    ulong InitFuel = 200_000_000,          // instructions allowed for init()
    ulong FrameFuel = 20_000_000,          // instructions allowed per frame()
    int MaxSaveBytes = 4096,               // per-site save data the reader's profile will keep
    int MaxStackBytes = 512 * 1024);

/// <summary>Bit flags for <see cref="GameInput.Buttons"/>.</summary>
public static class GameButtons
{
    public const int Left = 1, Right = 2, Up = 4, Down = 8, A = 16, B = 32, Start = 64;
}

public readonly record struct GameInput(int Buttons = 0, int PointerX = 0, int PointerY = 0, bool PointerDown = false);

/// <summary>The module breaks the kickl-game-1 contract, or asks for something games are never given.</summary>
public sealed class GameRejectedException(string message) : Exception(message);

/// <summary>
/// Runs one WebAssembly game in a sandbox with no capabilities.
///
/// ABI <c>kickl-game-1</c>. A game imports NOTHING: no host functions, no memory, no tables, no clock, no
/// randomness, no network, no files. It only exports:
///   memory                              its linear memory
///   kickl_abi() -> i32                  must return 1
///   init(seed: i32)                     called once, with a random seed chosen by the host
///   frame(dt_ms, buttons, pointer_x, pointer_y, pointer_down: i32)   called once per tick
///   framebuffer() -> i32                address of a 240x160 RGBA8 image in its memory
///   save_ptr() -> i32, save_len() -> i32    (optional) up to MaxSaveBytes the host may persist
///
/// Every call runs on a fixed fuel budget, so an endless loop traps instead of freezing the browser, and the
/// memory cap applies to growth too. A trap halts the game; it never reaches the host.
/// </summary>
public sealed class WasmGame : IDisposable
{
    public const int Width = 240;
    public const int Height = 160;
    public const int FrameBytes = Width * Height * 4;
    public const int Abi = 1;

    private readonly GameLimits _limits;
    private readonly Engine _engine;
    private readonly Store _store;
    private readonly Module _module;
    private readonly Instance _instance;
    private readonly Memory _memory;
    private readonly Action<int> _init;
    private readonly Action<int, int, int, int, int> _frame;
    private readonly Func<int> _framebuffer;
    private readonly Func<int>? _savePtr;
    private readonly Func<int>? _saveLen;
    private bool _started;
    private bool _disposed;

    public bool Halted { get; private set; }
    public string? HaltReason { get; private set; }

    private WasmGame(Engine engine, Store store, Module module, Instance instance, GameLimits limits)
    {
        _engine = engine;
        _store = store;
        _module = module;
        _instance = instance;
        _limits = limits;

        _memory = instance.GetMemory("memory") ?? throw new GameRejectedException("The game must export its memory as \"memory\".");
        var abi = instance.GetFunction<int>("kickl_abi") ?? throw new GameRejectedException("The game must export kickl_abi() -> i32.");
        _init = instance.GetAction<int>("init") ?? throw new GameRejectedException("The game must export init(seed: i32).");
        _frame = instance.GetAction<int, int, int, int, int>("frame")
                 ?? throw new GameRejectedException("The game must export frame(dt, buttons, pointer_x, pointer_y, pointer_down: i32).");
        _framebuffer = instance.GetFunction<int>("framebuffer") ?? throw new GameRejectedException("The game must export framebuffer() -> i32.");
        _savePtr = instance.GetFunction<int>("save_ptr");
        _saveLen = instance.GetFunction<int>("save_len");

        _store.Fuel = limits.InitFuel;
        if (abi() != Abi) throw new GameRejectedException($"This game speaks kickl-game ABI {abi()}; the browser only runs ABI {Abi}.");
    }

    public static WasmGame Load(ReadOnlySpan<byte> wasm, GameLimits? limits = null)
    {
        limits ??= new GameLimits();
        if (wasm.Length == 0) throw new GameRejectedException("The game is empty.");
        if (wasm.Length > limits.MaxModuleBytes)
            throw new GameRejectedException($"The game is {wasm.Length:N0} bytes; the limit here is {limits.MaxModuleBytes:N0}.");

        var engine = NewEngine(limits);
        try
        {
            Module module;
            try { module = Module.FromBytes(engine, "game", wasm); }
            catch (WasmtimeException ex) { throw new GameRejectedException("Not a valid WebAssembly module: " + ex.Message); }
            return Instantiate(engine, module, limits);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    /// <summary>Tests write games as text; the shipped path only accepts compiled bytes.</summary>
    internal static WasmGame LoadText(string wat, GameLimits? limits = null)
    {
        limits ??= new GameLimits();
        var engine = NewEngine(limits);
        try
        {
            Module module;
            try { module = Module.FromText(engine, "game", wat); }
            catch (WasmtimeException ex) { throw new GameRejectedException("Not a valid WebAssembly module: " + ex.Message); }
            return Instantiate(engine, module, limits);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private static Engine NewEngine(GameLimits limits) => new(new Config()
        .WithFuelConsumption(true)
        .WithWasmThreads(false)
        .WithMaximumStackSize(limits.MaxStackBytes));

    private static WasmGame Instantiate(Engine engine, Module module, GameLimits limits)
    {
        if (module.Imports.Count > 0)
        {
            var first = module.Imports[0];
            module.Dispose();
            throw new GameRejectedException(
                $"The game asks the host for \"{first.ModuleName}.{first.Name}\". Games are given no host functions, memory or tables.");
        }

        var store = new Store(engine);
        try
        {
            store.SetLimits(memorySize: limits.MaxMemoryBytes, tableElements: 10_000, instances: 1, tables: 1, memories: 1);
            store.Fuel = limits.InitFuel;
            Instance instance;
            try { instance = new Linker(engine).Instantiate(store, module); }
            catch (WasmtimeException ex) { throw new GameRejectedException("The game could not start: " + ex.Message); }
            return new WasmGame(engine, store, module, instance, limits);
        }
        catch
        {
            store.Dispose();
            module.Dispose();
            throw;
        }
    }

    /// <summary>Run init() once with the seed the host picked.</summary>
    public void Start(int seed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started || Halted) return;
        _started = true;
        Guarded(() =>
        {
            _store.Fuel = _limits.InitFuel;
            _init(seed);
            CheckFrameBuffer();
        });
    }

    /// <summary>Advance one tick. Returns false once the game has halted.</summary>
    public bool Frame(int dtMs, GameInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Halted) return false;
        if (!_started) Start(Environment.TickCount);
        if (Halted) return false;

        var buttons = input.Buttons;
        var x = Math.Clamp(input.PointerX, 0, Width - 1);
        var y = Math.Clamp(input.PointerY, 0, Height - 1);
        Guarded(() =>
        {
            _store.Fuel = _limits.FrameFuel;
            _frame(Math.Clamp(dtMs, 0, 1000), buttons, x, y, input.PointerDown ? 1 : 0);
        });
        return !Halted;
    }

    /// <summary>Copy the current 240x160 RGBA image. Does nothing once the game halted with no valid image.</summary>
    public bool CopyFrame(Span<byte> rgba)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (rgba.Length < FrameBytes) throw new ArgumentException($"Need {FrameBytes} bytes.", nameof(rgba));
        try
        {
            var ptr = CheckFrameBuffer();
            _memory.GetSpan(ptr, FrameBytes).CopyTo(rgba);
            return true;
        }
        catch (Exception ex) when (ex is WasmtimeException or GameRejectedException)
        {
            Halt("framebuffer: " + ex.Message);
            return false;
        }
    }

    /// <summary>The save area the game exposes, trimmed to MaxSaveBytes; empty if it has none.</summary>
    public byte[] ReadSave()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_savePtr is null || _saveLen is null || Halted) return [];
        try
        {
            var ptr = _savePtr();
            var len = Math.Min(_saveLen(), _limits.MaxSaveBytes);
            if (len <= 0 || ptr < 0 || (long)ptr + len > _memory.GetLength()) return [];
            return _memory.GetSpan(ptr, len).ToArray();
        }
        catch (WasmtimeException)
        {
            return [];
        }
    }

    /// <summary>
    /// Put the reader's saved bytes into the game's save area before init() runs, so the game can read them
    /// back. Anything longer than the area the game declares is ignored.
    /// </summary>
    public void RestoreSave(byte[] saved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started || Halted || _savePtr is null || _saveLen is null || saved.Length == 0) return;
        Guarded(() =>
        {
            var ptr = _savePtr();
            var room = Math.Min(Math.Min(_saveLen(), _limits.MaxSaveBytes), saved.Length);
            if (room <= 0 || ptr < 0 || (long)ptr + room > _memory.GetLength()) return;
            saved.AsSpan(0, room).CopyTo(_memory.GetSpan(ptr, room));
        });
    }

    private int CheckFrameBuffer()
    {
        var ptr = _framebuffer();
        if (ptr < 0 || (long)ptr + FrameBytes > _memory.GetLength())
            throw new GameRejectedException($"framebuffer() points outside the game's memory ({ptr}).");
        return ptr;
    }

    private void Guarded(Action call)
    {
        try
        {
            call();
        }
        catch (WasmtimeException ex)
        {
            Halt(TrapReason(ex.Message));
        }
        catch (GameRejectedException ex)
        {
            Halt(ex.Message);
        }
    }

    /// <summary>Wasmtime's message is a backtrace; keep the line that says what went wrong.</summary>
    private static string TrapReason(string message)
    {
        var lines = message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var trap = lines.FirstOrDefault(l => l.Contains("wasm trap", StringComparison.OrdinalIgnoreCase)
                                             || l.Contains("fuel", StringComparison.OrdinalIgnoreCase)
                                             || l.Contains("call stack", StringComparison.OrdinalIgnoreCase));
        if (trap is not null) return trap;
        return lines.LastOrDefault(l => !l.StartsWith("error while executing", StringComparison.OrdinalIgnoreCase))
               ?? "the game trapped";
    }

    private void Halt(string reason)
    {
        Halted = true;
        HaltReason = reason.Length > 200 ? reason[..200] : reason;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        _module.Dispose();
        _engine.Dispose();
    }
}
