using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using Sts2UndoMod.Sts2UndoModCode.Snapshot;

namespace Sts2UndoMod.Sts2UndoModCode.Undo;

/// <summary>
/// Manages snapshot stack + restoration. Single stack, with `IsTurnBoundary`
/// markers so per-turn rewind is just "pop until previous turn boundary".
/// </summary>
internal static class UndoController
{
    /// <summary>
    /// Maximum snapshots retained. Each snapshot deep-clones every card,
    /// power, relic, dynamic var, and per-creature mutable state — so this
    /// directly drives memory pressure. 30 covers typical "undo a few moves"
    /// flows; users who need deeper rewind can raise it.
    /// Earlier value 80 was correlated with a "game gets sluggish after a
    /// long combat" report — at 80 entries with multi-creature combat the
    /// per-snapshot cost adds up.
    /// </summary>
    private const int MaxStackSize = 30;
    /// <summary>
    /// Tiny single-frame backstop. Real "is the game idle?" answer comes from
    /// state queries below (holder tweens, executor current action, etc.).
    /// </summary>
    private const int ActionCooldownMs = 50;

    private static readonly List<CombatSnapshot> Stack = new();
    public static bool IsRestoring { get; private set; }
    private static bool _turnBoundaryArmed;
    private static long _lastActionTimestampMs;

    public static int StackCount => Stack.Count;

    /// <summary>
    /// Mark the *next* successful snapshot as a turn boundary. Used by the
    /// CombatManager.StartTurn patch — the actual snapshot is deferred until
    /// the player does something, so we get a snapshot of the playable turn
    /// state, not the empty pre-draw state.
    /// </summary>
    public static void ArmTurnBoundary() => _turnBoundaryArmed = true;

    public static void TakeSnapshot(bool isTurnBoundary = false)
    {
        if (IsRestoring) return;
        if (!CanCaptureNow()) return;

        // Prune stale dict entries opportunistically. Cheap (single pass),
        // worth doing here so registries don't bloat capture-time logic.
        Patches.AnimDiePatch.PruneStaleDetached();

        bool boundary = isTurnBoundary || _turnBoundaryArmed;
        long startMs = Environment.TickCount64;
        var snap = CombatSnapshot.Capture(boundary);
        long elapsedMs = Environment.TickCount64 - startMs;
        if (snap == null) return;

        Stack.Add(snap);
        if (Stack.Count > MaxStackSize) Stack.RemoveAt(0);
        if (boundary) _turnBoundaryArmed = false;

        // Mark the start of an action so the cooldown guard kicks in. Set even
        // if capture failed — the action is still about to fire.
        _lastActionTimestampMs = Environment.TickCount64;

        // Track perf — alert when an individual capture exceeds 30ms.
        // Includes stack depth so we can correlate slowness with retention.
        if (elapsedMs >= 30)
            UndoLogger.Warn($"[Snapshot] slow capture {elapsedMs}ms stack={Stack.Count} idleCache={Snapshot.CombatSnapshot.IdleAnimCache.Count} detachedZombies={Patches.AnimDiePatch.DetachedZombies.Count}");
        else
            UndoLogger.Info($"[Snapshot] capture {elapsedMs}ms stack={Stack.Count} idleCache={Snapshot.CombatSnapshot.IdleAnimCache.Count}");
    }

    public static void Undo()
    {
        UndoLogger.Info($"[Undo] requested (single step) — stack={Stack.Count}");
        UndoSteps(1);
    }

    /// <summary>Undo until we hit a turn-boundary snapshot (or stack empties).</summary>
    public static void UndoTurn()
    {
        UndoLogger.Info($"[Undo] requested (turn) — stack={Stack.Count}");
        if (Stack.Count == 0) { UndoLogger.Info("[Undo] no-op: stack empty"); return; }
        // Walk backwards: pop until we find a snapshot marked as turn-boundary, OR
        // we run out of stack. The earliest turn-boundary we pop becomes the
        // restore target.
        int targetIndex = -1;
        for (int i = Stack.Count - 1; i >= 0; i--)
        {
            if (Stack[i].IsTurnBoundary) { targetIndex = i; break; }
        }
        if (targetIndex < 0) targetIndex = 0;
        UndoSteps(Stack.Count - targetIndex);
    }

    private static void UndoSteps(int n)
    {
        if (n <= 0 || Stack.Count == 0)
        {
            UndoLogger.Info($"[Undo] no-op: n={n} stack={Stack.Count}");
            return;
        }
        if (!CanRestoreNow()) { UndoLogger.Info("[Undo] guard rejected"); return; }

        // The snapshot to restore is at Stack[Count - n] — keep popping until that.
        int target = Math.Max(0, Stack.Count - n);
        var snap = Stack[target];

        // Truncate stack down to but not including target — the restored state
        // matches the moment that snapshot was taken, so all snapshots taken
        // *after* it are no longer reachable.
        Stack.RemoveRange(target, Stack.Count - target);

        IsRestoring = true;
        try { SnapshotRestorer.Restore(snap); }
        catch (Exception ex) { UndoLogger.Warn($"[Undo] restore threw: {ex.Message}"); }
        finally { IsRestoring = false; }
    }

    public static void ClearStacks()
    {
        Stack.Clear();
        _turnBoundaryArmed = false;
        UndoLogger.Info("[Undo] stacks cleared");
    }

    private static bool CanCaptureNow()
    {
        // Combat must be in progress — never capture outside the
        // StartCombat → EndCombat window. If we somehow arrive here while
        // combat is ended (e.g. an action ctor fires during teardown), drop
        // any stack the previous combat left around so we don't preserve a
        // dangling reference to dead creatures / freed nodes.
        try
        {
            var cm = CombatManager.Instance;
            if (cm == null) { TruncateOutOfCombat("cm-null"); return false; }
            var inProgressProp = HarmonyLib.AccessTools.Property(typeof(CombatManager), "IsInProgress");
            if (inProgressProp?.GetValue(cm) is false)
            {
                TruncateOutOfCombat("not-in-progress");
                return false;
            }
        }
        catch { }

        var cs = CurrentCombatState();
        if (cs == null) { TruncateOutOfCombat("cs-null"); return false; }
        // Only capture during player phase — capturing mid-enemy-turn would
        // produce a snapshot the player can't safely return to.
        return cs.CurrentSide == CombatSide.Player;
    }

    /// <summary>
    /// Drop any leftover snapshots when we detect we're outside an active
    /// combat. ClearStacks is also called by EndCombatInternal/Reset patches,
    /// but this is the belt-and-suspenders fallback for the rare path where
    /// some action ctor fires after combat-end before our Reset patch runs.
    /// </summary>
    private static void TruncateOutOfCombat(string reason)
    {
        if (Stack.Count == 0) return;
        UndoLogger.Info($"[Undo] truncating {Stack.Count} stale snapshot(s) — out of combat ({reason})");
        Stack.Clear();
    }

    /// <summary>Public probe — used by UI to grey out the button when undo is unavailable.</summary>
    public static bool CanRestoreNowPublic() => Stack.Count > 0 && CanRestoreNow();

    private static bool CanRestoreNow()
    {
        if (IsRestoring) return false;

        // Combat must actually be in progress. After CombatWon / CombatEnded
        // fires there's a window before CombatManager.Reset() runs where Stack
        // is still populated and CombatState is still readable — without this
        // gate, Z would happily restore a snapshot from the just-finished combat.
        try
        {
            var cm = CombatManager.Instance;
            if (cm == null)
            {
                UndoLogger.Info("[Undo] blocked: CombatManager null");
                return false;
            }
            var inProgressProp = HarmonyLib.AccessTools.Property(typeof(CombatManager), "IsInProgress");
            if (inProgressProp?.GetValue(cm) is false)
            {
                UndoLogger.Info("[Undo] blocked: combat not in progress");
                return false;
            }
        }
        catch { }

        // Block until all creatures are in a stable loop. Restoring while a
        // transient (attack/hurt/cast) is mid-play would either snap the visual
        // mid-anim or leave residue — wait for everything to settle.
        if (Snapshot.CombatSnapshot.AnyCreatureMidTransient())
        {
            UndoLogger.Info("[Undo] blocked: creature mid-transient anim");
            return false;
        }

        // Block while any creature death anim / NMonsterDeathVfx is still
        // playing. Dead creatures aren't in cs.Creatures so AnyCreatureMidTransient
        // can't see them. Undoing mid-vfx races with the async detach branch
        // and leaves the body invisible after revive (the body Reparent + tween
        // tear-down are separate ticks, so even our rescue can't always win).
        // Wait for all in-flight RunReplacementDeathAnim tasks to settle.
        if (Patches.AnimDiePatch.InFlightCount > 0)
        {
            UndoLogger.Info($"[Undo] blocked: {Patches.AnimDiePatch.InFlightCount} death anim(s) in flight");
            return false;
        }

        // Cooldown after any action ctor — prevents race where action moved
        // from queue to executor's current-action slot, leaving queue.IsEmpty
        // but the executor still spawning VFX (damage numbers / particles).
        long elapsed = Environment.TickCount64 - _lastActionTimestampMs;
        if (elapsed < ActionCooldownMs)
        {
            UndoLogger.Info($"[Undo] blocked: action cooldown {elapsed}ms < {ActionCooldownMs}ms");
            return false;
        }

        var cs = CurrentCombatState();
        if (cs == null) { UndoLogger.Info("[Undo] blocked: CombatState null"); return false; }

        // Block once the player's turn has begun ending — the gap between
        // EndPlayerTurn and the enemy turn animation start. CombatManager
        // exposes CurrentSide / IsPlayPhase / Ending* / IsEnemyTurnStarted to
        // identify this window.
        try
        {
            var cm = CombatManager.Instance;
            if (cm != null)
            {
                var isPlayPhase = HarmonyLib.AccessTools.Property(typeof(CombatManager), "IsPlayPhase")?.GetValue(cm);
                var endingP1 = HarmonyLib.AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseOne")?.GetValue(cm);
                var endingP2 = HarmonyLib.AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseTwo")?.GetValue(cm);
                var enemyTurnStarted = HarmonyLib.AccessTools.Property(typeof(CombatManager), "IsEnemyTurnStarted")?.GetValue(cm);

                if (isPlayPhase is false)
                {
                    UndoLogger.Info("[Undo] blocked: not in play phase");
                    return false;
                }
                if (endingP1 is true || endingP2 is true)
                {
                    UndoLogger.Info($"[Undo] blocked: turn ending (P1={endingP1} P2={endingP2})");
                    return false;
                }
                if (enemyTurnStarted is true)
                {
                    UndoLogger.Info("[Undo] blocked: enemy turn started");
                    return false;
                }
            }
        }
        catch { }

        if (cs.CurrentSide != CombatSide.Player)
        {
            UndoLogger.Info($"[Undo] blocked: CurrentSide={cs.CurrentSide}");
            return false;
        }

        // Don't undo mid-transition.
        try
        {
            if (NGame.Instance?.Transition?.InTransition == true)
            {
                UndoLogger.Info("[Undo] blocked: NGame.Transition.InTransition");
                return false;
            }
        }
        catch { }

        // Action queue must be idle so we don't undo into half-executed flows.
        try
        {
            var aqs = RunManager.Instance?.ActionQueueSet;
            if (aqs?.IsEmpty == false)
            {
                UndoLogger.Info("[Undo] blocked: ActionQueueSet not empty");
                return false;
            }
        }
        catch { }

        // Block while NCardPlayQueue still has cards mid-animation.
        try
        {
            var pq = NCardPlayQueue.Instance;
            if (pq != null)
            {
                var queueField = HarmonyLib.AccessTools.Field(typeof(NCardPlayQueue), "_playQueue");
                if (queueField?.GetValue(pq) is System.Collections.ICollection col && col.Count > 0)
                {
                    UndoLogger.Info($"[Undo] blocked: NCardPlayQueue has {col.Count} pending");
                    return false;
                }
            }
        }
        catch { }

        // Block while NCardPlay is in active try-play state.
        try
        {
            var hand = NPlayerHand.Instance;
            var currentPlay = hand != null
                ? ReflectionCache.HandCurrentCardPlayField?.GetValue(hand)
                : null;
            if (currentPlay != null)
            {
                var tryingField = HarmonyLib.AccessTools.Field(currentPlay.GetType(), "_isTryingToPlayCard");
                if (tryingField?.GetValue(currentPlay) is true)
                {
                    UndoLogger.Info("[Undo] blocked: NCardPlay._isTryingToPlayCard == true");
                    return false;
                }
            }
        }
        catch { }

        // Block while ANY hand-card-holder is mid-position-tween. Compare
        // current Position vs _targetPosition — they match (within epsilon)
        // when the holder is at rest. This is more reliable than the cancel-
        // token field which may be holder-lifetime-scoped, not per-tween.
        try
        {
            var hand = NPlayerHand.Instance;
            if (hand != null && IsAnyHolderAnimating(hand)) return false;  // logs inside
        }
        catch { }

        // Block while the executor has a current action (action moved out of
        // queue but still executing — VFX-spawning window).
        try
        {
            if (HasInFlightExecutorAction()) return false;  // logs inside
        }
        catch { }

        UndoLogger.Info("[Undo] guards passed");
        return true;
    }

    private static System.Reflection.FieldInfo? _holderTargetPosField;
    private static bool _holderFieldInitialized;
    private const float HolderRestEpsilonSq = 4f;  // ~2 pixels squared

    private static bool IsAnyHolderAnimating(NPlayerHand hand)
    {
        if (!_holderFieldInitialized && hand.ActiveHolders.Count > 0)
        {
            var holderType = hand.ActiveHolders[0].GetType();
            _holderTargetPosField = HarmonyLib.AccessTools.Field(holderType, "_targetPosition");
            _holderFieldInitialized = true;
        }
        if (_holderTargetPosField == null) return false;

        foreach (var holder in hand.ActiveHolders)
        {
            // Holder is its own type — read Position via reflection so we don't
            // depend on its base class.
            object? holderObj = holder;
            var posProp = HarmonyLib.AccessTools.Property(holderObj.GetType(), "Position");
            if (posProp?.GetValue(holderObj) is not Godot.Vector2 current) continue;

            if (_holderTargetPosField.GetValue(holderObj) is not Godot.Vector2 target) continue;

            if (current.DistanceSquaredTo(target) > HolderRestEpsilonSq)
            {
                UndoLogger.Info($"[Undo] blocked: holder still moving ({current} → {target})");
                return true;
            }
        }
        return false;
    }

    private static bool HasInFlightExecutorAction()
    {
        var executor = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.ActionExecutor;
        if (executor == null) return false;
        // Try common field names for the "currently executing" slot.
        foreach (var name in new[] { "_currentAction", "_executingAction", "_action" })
        {
            var f = HarmonyLib.AccessTools.Field(executor.GetType(), name);
            var v = f?.GetValue(executor);
            if (v != null)
            {
                UndoLogger.Info($"[Undo] blocked: executor.{name} non-null");
                return true;
            }
        }
        return false;
    }

    private static CombatState? CurrentCombatState()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return null;
        return ReflectionCache.CombatManagerStateField.GetValue(cm) as CombatState;
    }
}
