using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core.Capabilities;
using MatchZyStateApi;

namespace MatchZy
{
    // LANN: publishes the match state as the "matchzy:state" capability (shared/MatchZyStateApi/MatchZyStateApi.dll).
    //
    // This is the ONLY MatchZy file that mentions a MatchZyStateApi type, and only inside method bodies. MatchZy must
    // keep working on a server that has this MatchZy.dll but no shared/MatchZyStateApi:
    //   * no type in MatchZy.dll derives from / implements a contract type (the provider class lives in the
    //     contract) and no field is typed from it - CSS enumerates the types of every loaded assembly on each
    //     plugin load, so one unloadable type would break MatchZy and every plugin loaded after it;
    //   * every method that touches a contract type is NoInlining and is only reached through the wrappers below,
    //     which catch: a missing assembly throws FileNotFoundException when the callee is JIT-compiled, i.e. inside
    //     the wrapper's try, never at plugin load;
    //   * after a failed Publish nothing else is attempted (Changed/Withdraw are no-ops).
    internal static class MatchStateCapability
    {
        private static object? _provider; // MatchZyStateApi.MatchZyStateProvider

        /// <summary>"ok" or why the capability is not available (for the load log / diagnostics).</summary>
        public static string Detail { get; private set; } = "not published";

        public static bool Published => _provider != null;

        /// <summary>Load: create the provider, make it current and register the capability. Never throws.</summary>
        public static bool Publish(int initialState)
        {
            try
            {
                PublishCore(initialState);
                Detail = "ok";
                return true;
            }
            catch (Exception e)
            {
                _provider = null;
                Detail = $"{e.GetType().Name}: {e.Message.ReplaceLineEndings(" ").Trim()}";
                return false;
            }
        }

        /// <summary>State timer (game thread): store the new value and raise StateChanged. Never throws.</summary>
        public static void Changed(int newState)
        {
            if (_provider == null) return;
            try { ChangedCore(newState); }
            catch (Exception e) { Console.WriteLine($"[MatchZy] matchzy:state update failed: {e.GetType().Name}: {e.Message}"); }
        }

        /// <summary>Unload: clear the shared slot (if it is still ours) and drop subscribers. Never throws.</summary>
        public static void Withdraw()
        {
            if (_provider == null) return;
            try { WithdrawCore(); }
            catch (Exception e) { Console.WriteLine($"[MatchZy] matchzy:state withdraw failed: {e.GetType().Name}: {e.Message}"); }
            _provider = null;
            Detail = "withdrawn";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PublishCore(int initialState)
        {
            var provider = new MatchZyStateProvider(initialState);
            MatchZyStateHost.Publish(provider);
            // Register the contract's Supplier, not `() => provider`: CSS keeps suppliers forever and returns the
            // first one, so it must always resolve to the CURRENT MatchZy instance (see MatchZyStateHost).
            Capabilities.RegisterPluginCapability(new PluginCapability<IMatchZyState?>(MatchZyStates.CapabilityName), MatchZyStateHost.Supplier);
            _provider = provider;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ChangedCore(int newState)
        {
            if (_provider is MatchZyStateProvider p) p.Set(newState);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WithdrawCore()
        {
            if (_provider is not MatchZyStateProvider p) return;
            MatchZyStateHost.Withdraw(p);
            p.ClearSubscribers();
        }
    }
}
