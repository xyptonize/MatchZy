namespace MatchZyStateApi;

/// <summary>
/// The LANN MatchZy fork's match state, readable from other plugins.
///
/// WHY THIS EXISTS. The fork also publishes <c>matchzy_match_state</c> as a CSS FakeConVar, but a FakeConVar is
/// not an engine convar: <c>ConVar.Find</c> from another plugin returns null for it (only the server console /
/// RCON can read it). Cross-plugin state therefore has to go through a <c>PluginCapability&lt;T&gt;</c> whose
/// interface lives in a third, shared assembly.
///
/// HOW TO CONSUME (e.g. LannRanks):
///   * Reference MatchZyStateApi.dll with Private=false; never ship a copy.
///   * Keep every mention of these types in ONE adapter class whose methods are NoInlining and are called inside
///     try/catch, so a server without shared/MatchZyStateApi still loads your plugin (FileNotFoundException /
///     TypeLoadException surface at the call site, not at plugin construction).
///   * No type in your plugin may implement/derive from a contract type or have a field typed from one (store the
///     capability as object). CSS runs GetTypes() over EVERY loaded assembly on each plugin load, so one type that
///     cannot load without this DLL fails your plugin and every plugin loaded after it.
///   * Resolve <c>new PluginCapability&lt;IMatchZyState?&gt;(MatchZyStates.CapabilityName)</c> in
///     OnAllPluginsLoaded. <c>Get()</c> THROWS KeyNotFoundException when no MatchZy fork registered it (stock
///     MatchZy, or MatchZy not installed) and RETURNS NULL while MatchZy is unloaded/reloading.
///   * PULL, do not cache: call <c>Get()</c> each time and read <see cref="State"/>. CSS keeps capability
///     providers forever and returns the first one, so a cached object from before a MatchZy hot reload would
///     be frozen. <c>Get()</c> always goes through <see cref="MatchZyStateHost"/>, which tracks the live instance.
///   * <see cref="StateChanged"/> is a convenience for UI/logging only: a subscription is dropped when MatchZy
///     unloads and is NOT carried over to a reloaded MatchZy. Anything that must be right (scoring, rewards)
///     reads <see cref="State"/> at the moment it decides.
///
/// Every member is safe to call from any thread: the provider only stores and returns an int.
/// </summary>
public interface IMatchZyState
{
    /// <summary>
    /// 0 idle, 1 warmup (ready-up), 2 knife, 3 side-select, 4 live, 5 live-paused, 6 practice, 7 dry-run, 8 veto
    /// (see <see cref="MatchZyStates"/>). Recomputed by MatchZy every 0.25 s on the game thread; the value read
    /// here is the last computed one (at most 0.25 s old).
    /// </summary>
    int State { get; }

    /// <summary><see cref="MatchZyStates.Name"/> of <see cref="State"/>, e.g. "live".</summary>
    string StateName { get; }

    /// <summary>
    /// True for 4 (live) and 5 (live-paused). A pause requested mid-round is flagged at once but the game only
    /// freezes at the next freeze time, so the rest of that round is real match play; a tactical timeout reports
    /// 5 only while frozen.
    /// </summary>
    bool IsLive { get; }

    /// <summary>
    /// (oldState, newState), raised on the game thread from MatchZy's 0.25 s state timer when the value changes
    /// (changes inside one tick are coalesced). Handlers must be fast and must not throw; MatchZy isolates each
    /// handler. See the class remarks: not a substitute for reading <see cref="State"/>.
    /// </summary>
    event Action<int, int>? StateChanged;
}

/// <summary>State numbers and the capability name, shared by provider and consumers.</summary>
public static class MatchZyStates
{
    /// <summary>The capability name both sides agree on.</summary>
    public const string CapabilityName = "matchzy:state";

    public const int Idle = 0;
    public const int Warmup = 1;
    public const int Knife = 2;
    public const int SideSelect = 3;
    public const int Live = 4;
    public const int Paused = 5;
    public const int Practice = 6;
    public const int DryRun = 7;
    public const int Veto = 8;

    /// <summary>Same order as the fork's ComputeMatchState() and the matchzy_match_state help text.</summary>
    public static string Name(int state) => state switch
    {
        Idle => "idle",
        Warmup => "warmup",
        Knife => "knife",
        SideSelect => "side-select",
        Live => "live",
        Paused => "paused",
        Practice => "practice",
        DryRun => "dry-run",
        Veto => "veto",
        _ => "unknown",
    };

    /// <summary>Live match play: <see cref="Live"/> or <see cref="Paused"/> (see <see cref="IMatchZyState.IsLive"/>).</summary>
    public static bool IsLiveState(int state) => state == Live || state == Paused;
}

/// <summary>
/// Process-lifetime slot for the CURRENT provider. Only MatchZy writes it.
///
/// WHY. CSS's capability registry is append-only and <c>PluginCapability&lt;T&gt;.Get()</c> returns the FIRST
/// supplier ever registered under a name; nothing is removed when a plugin unloads (CSS 1.0.342..1.0.375). If
/// MatchZy registered <c>() =&gt; this</c>, a MatchZy hot reload would leave every consumer reading the dead
/// instance's frozen state (and pin the old plugin's AssemblyLoadContext). MatchZy therefore registers
/// <see cref="Supplier"/> - a delegate to a method in THIS assembly, which is never unloaded - and swaps the
/// instance here on Load/Unload. Consumers never call this class; they use the capability.
/// </summary>
public static class MatchZyStateHost
{
    private static IMatchZyState? _current;

    /// <summary>The live provider, or null while MatchZy is not loaded.</summary>
    public static IMatchZyState? Current => Volatile.Read(ref _current);

    /// <summary>What MatchZy registers with CSS: always returns the live provider (or null).</summary>
    public static readonly Func<IMatchZyState?> Supplier = () => Volatile.Read(ref _current);

    /// <summary>MatchZy Load: make <paramref name="provider"/> the live one.</summary>
    public static void Publish(IMatchZyState provider) => Volatile.Write(ref _current, provider);

    /// <summary>MatchZy Unload: clear the slot only if it still holds <paramref name="provider"/>.</summary>
    public static void Withdraw(IMatchZyState provider) => Interlocked.CompareExchange(ref _current, null, provider);
}

/// <summary>
/// The provider object MatchZy publishes. It lives HERE, not in MatchZy.dll, on purpose: CSS calls
/// GetExportedTypes()/DefinedTypes on each plugin and, on every plugin load, GetTypes() on EVERY loaded assembly
/// (CSS 1.0.375 PluginContext.Load). A class inside MatchZy.dll that implemented IMatchZyState could not be loaded
/// on a server without this DLL, which would fail MatchZy AND every plugin loaded after it. With the class here,
/// MatchZy.dll has no type built on a contract type and simply runs without the capability when the DLL is absent.
/// MatchZy feeds it from its 0.25 s state timer; it only stores an int and raises <see cref="StateChanged"/>.
/// </summary>
public sealed class MatchZyStateProvider : IMatchZyState
{
    private volatile int _state;
    private Action<int, int>? _changed;
    private readonly object _gate = new();

    public MatchZyStateProvider(int initialState) { _state = initialState; }

    public int State => _state;
    public string StateName => MatchZyStates.Name(_state);
    public bool IsLive => MatchZyStates.IsLiveState(_state);

    public event Action<int, int>? StateChanged
    {
        add { lock (_gate) _changed += value; }
        remove { lock (_gate) _changed -= value; }
    }

    /// <summary>
    /// Provider side only (MatchZy's state timer, game thread): store <paramref name="state"/> and, if it changed,
    /// raise StateChanged(old, new). Each handler is isolated: one that throws is reported and the rest still run.
    /// </summary>
    public void Set(int state)
    {
        int old = _state;
        if (old == state) return;
        _state = state;
        var handlers = _changed;
        if (handlers == null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try { ((Action<int, int>)d)(old, state); }
            catch (Exception e) { Console.WriteLine($"[MatchZyStateApi] StateChanged handler threw {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// <summary>Provider side only (MatchZy Unload): drop every subscriber so no plugin is pinned by this instance.</summary>
    public void ClearSubscribers() { lock (_gate) _changed = null; }
}
