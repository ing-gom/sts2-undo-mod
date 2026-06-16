using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2UndoMod.Sts2UndoModCode;

/// <summary>
/// Renders the mod inert during real-multiplayer / replay runs. The undo
/// system deep-clones the full combat state on every player action; in a
/// networked run the game already maintains its own NetFullCombatState
/// pipeline (CombatStateSynchronizer) and adding our per-action snapshot
/// pass on top of it made co-op sessions unplayably laggy. Undo across
/// synced turns is also semantically broken — you can't unsend a packet.
///
/// Gate logic uses RunManager.Instance.IsSinglePlayerOrFakeMultiplayer,
/// the same property the base game uses to short-circuit its own sync
/// codepaths. It is true only when NetService.Type == Singleplayer; Host,
/// Client, and Replay all return false (we want to bail in all three).
/// </summary>
internal static class MultiplayerGate
{
    private static bool _loggedDormantThisCombat;

    public static bool IsDormant()
    {
        try
        {
            var rm = RunManager.Instance;
            if (rm == null || !rm.IsInProgress) return false;
            // beta (game build 23575630): RunManager.IsSinglePlayerOrFakeMultiplayer
            // was removed. Reconstruct it from NetService.Type — undo stays active
            // for solo play (Singleplayer / None / no service); only the real
            // networked or replay modes (Host / Client / Replay) go dormant.
            var netType = rm.NetService?.Type;
            if (netType is not (NetGameType.Host or NetGameType.Client or NetGameType.Replay))
                return false;

            if (!_loggedDormantThisCombat)
            {
                _loggedDormantThisCombat = true;
                NetGameType? t = rm.NetService?.Type;
                UndoLogger.Info($"[Undo] dormant for this run — NetService.Type={t} (multiplayer/replay; snapshots + button disabled)");
            }
            return true;
        }
        catch
        {
            // Property access failed — fail open (treat as singleplayer) so a
            // future game-update API rename never silently disables undo for
            // every solo player.
            return false;
        }
    }

    /// <summary>
    /// Reset the once-per-combat log latch. Called from StartCombatInternal so
    /// the dormant-status line lands once per fight in a co-op run rather than
    /// spamming the log on every action ctor.
    /// </summary>
    public static void ResetForNewCombat() => _loggedDormantThisCombat = false;
}
