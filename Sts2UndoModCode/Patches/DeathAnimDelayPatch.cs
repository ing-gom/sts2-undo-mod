using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace Sts2UndoMod.Sts2UndoModCode.Patches;

/// <summary>
/// Defers <c>NCreature.StartDeathAnim</c> by a short window so a fast undo can
/// abort the death animation before it ever runs. Without this, the async
/// <c>AnimDie</c> task starts immediately on kill and frees the creature's
/// body subtree synchronously between awaits — by the time undo arrives and
/// we cancel the cancellation token, the body is already gone and the in-place
/// revive path produces a half-dead shell.
///
/// Strategy:
///   1. Prefix on <c>StartDeathAnim</c> intercepts the call.
///   2. Schedules a <see cref="Godot.Timer"/> child on the NCreature with a
///      short wait. When the timer times out, we re-invoke <c>StartDeathAnim</c>
///      with a bypass flag set so our prefix lets it through.
///   3. If <see cref="AbortAllPending"/> is called within the window (e.g. from
///      <c>SnapshotRestorer.Restore</c>'s entry), the timer is stopped and
///      freed — the death anim never runs and the creature stays alive in tree.
///
/// Trade-off: the player sees a brief frozen pose at the moment of kill before
/// the death anim begins. <see cref="DeferSeconds"/> is tuned to be barely
/// noticeable. Slower undos (after the timer has fired) still need the
/// post-revive degraded-detection + AddCreature fallback.
/// </summary>
[HarmonyPatch(typeof(NCreature), "StartDeathAnim")]
public static class DeathAnimDelayPatch
{
    public const float DeferSeconds = 0.2f;

    private static readonly Dictionary<ulong, (Godot.Timer timer, NCreature creature, bool arg)> _pending
        = new();
    private static bool _bypass;

    [HarmonyPrefix]
    public static bool Prefix(NCreature __instance, bool __0)
    {
        // Re-entrant call from the deferred timer: let the original StartDeathAnim run.
        if (_bypass) return true;

        try
        {
            if (!Godot.GodotObject.IsInstanceValid(__instance)) return true;

            ulong id = __instance.GetInstanceId();
            // If this creature already has a pending defer, drop the new call —
            // game shouldn't double-trigger StartDeathAnim, but if it does we
            // honor the first one (with its original arg).
            if (_pending.ContainsKey(id)) return false;

            var timer = new Godot.Timer
            {
                OneShot = true,
                WaitTime = DeferSeconds,
            };
            __instance.AddChild(timer);

            // Capture by id, not by closure, so we can resolve the dict entry
            // at firing time even if external code re-keyed things.
            timer.Timeout += () => OnDeferredFire(id);

            _pending[id] = (timer, __instance, __0);
            timer.Start();

            UndoLogger.Info($"[DeathDefer] deferred StartDeathAnim on creature " +
                $"instId={id} arg={__0} for {DeferSeconds:F2}s");
            return false; // skip original; the timer will re-call it
        }
        catch (Exception ex)
        {
            UndoLogger.Warn($"[DeathDefer] defer setup failed: {ex.Message} — letting original run");
            return true;
        }
    }

    private static void OnDeferredFire(ulong id)
    {
        if (!_pending.TryGetValue(id, out var entry)) return;
        _pending.Remove(id);

        try { entry.timer.QueueFree(); } catch { }

        if (!Godot.GodotObject.IsInstanceValid(entry.creature))
        {
            UndoLogger.Info($"[DeathDefer] timer fired but creature instId={id} freed — skipping");
            return;
        }

        UndoLogger.Info($"[DeathDefer] timer fired — running real StartDeathAnim on instId={id}");
        _bypass = true;
        try { entry.creature.StartDeathAnim(entry.arg); }
        catch (Exception ex) { UndoLogger.Warn($"[DeathDefer] deferred StartDeathAnim threw: {ex.Message}"); }
        finally { _bypass = false; }
    }

    /// <summary>
    /// Undo entry — called from <c>SnapshotRestorer.Restore</c>. Cancels every
    /// pending death-anim timer so undo within the defer window prevents
    /// AnimDie from ever starting — the creature stays fully intact in the
    /// scene tree and the model-level revive is effectively a no-op (creature
    /// was never actually removed from cs.Creatures via the death path).
    /// Creatures are left ALIVE on purpose.
    /// </summary>
    public static int AbortAllPending()
    {
        if (_pending.Count == 0) return 0;
        int aborted = 0;
        foreach (var kv in _pending.ToList())
        {
            try
            {
                if (Godot.GodotObject.IsInstanceValid(kv.Value.timer))
                {
                    kv.Value.timer.Stop();
                    kv.Value.timer.QueueFree();
                }
                aborted++;
            }
            catch { }
        }
        _pending.Clear();
        UndoLogger.Info($"[DeathDefer] aborted {aborted} pending death anim(s) for undo");
        return aborted;
    }

    /// <summary>
    /// Combat-end entry — called from <c>PatchEndCombatInternal</c>. Single-enemy
    /// fights (and last-kill of any fight) trigger combat-end within ~13ms of the
    /// killing blow, well before our <see cref="DeferSeconds"/> timer fires. If we
    /// just QueueFree'd here the player would see the enemy vanish with no death
    /// anim at all — the killing blow on every solo encounter looked broken.
    ///
    /// Instead, fire the deferred <c>StartDeathAnim</c> immediately with the bypass
    /// flag so our replacement AnimDie (spine 'die' + 0.6s + detach) runs under the
    /// win banner. Visually noisier than a clean cut, but the player wanted the
    /// animation to play.
    /// </summary>
    public static int FlushForCombatEnd()
    {
        if (_pending.Count == 0) return 0;
        int flushed = 0;
        foreach (var kv in _pending.ToList())
        {
            try
            {
                if (Godot.GodotObject.IsInstanceValid(kv.Value.timer))
                {
                    kv.Value.timer.Stop();
                    kv.Value.timer.QueueFree();
                }
                if (Godot.GodotObject.IsInstanceValid(kv.Value.creature))
                {
                    _bypass = true;
                    try { kv.Value.creature.StartDeathAnim(kv.Value.arg); }
                    catch (Exception ex)
                    { UndoLogger.Warn($"[DeathDefer] flush StartDeathAnim threw on instId={kv.Key}: {ex.Message}"); }
                    finally { _bypass = false; }
                }
                flushed++;
            }
            catch { }
        }
        _pending.Clear();
        UndoLogger.Info($"[DeathDefer] flushed {flushed} pending death(s) for combat-end (anim allowed to run)");
        return flushed;
    }

    /// <summary>
    /// Reset entry (combat init) — called from <c>PatchCombatReset</c> to drop
    /// any state left over from a prior combat. By the time Reset runs the
    /// scene-level NCreature nodes are typically already gone, so we only
    /// need to zero the dictionary; defensively stop any lingering timers.
    /// </summary>
    public static void ClearAll() => AbortAllPending();
}
