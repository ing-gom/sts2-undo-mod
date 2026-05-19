using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Sts2UndoMod.Sts2UndoModCode.Snapshot;
using Sts2UndoMod.Sts2UndoModCode.Ui;
using Sts2UndoMod.Sts2UndoModCode.Undo;
using System.Reflection;

namespace Sts2UndoMod.Sts2UndoModCode.Patches;

// Snapshot is taken in a constructor prefix on each player-driven action that
// can mutate combat state. Patching is imperative (not attribute-based) so we
// can:
//   1. Log success/failure for every action type — easy to spot a skipped patch
//      when the game updates a ctor signature.
//   2. Patch ALL constructor overloads of each action type rather than guessing
//      at parameter types — fixes silent drop-out when the game adds an
//      optional bool to UsePotionAction or similar.

public static class SnapshotPatches
{
    public static void InstallAll(Harmony harmony)
    {
        var prefix = AccessTools.Method(typeof(SnapshotPatches), nameof(SnapshotPrefix));
        if (prefix == null) { UndoLogger.Warn("[Patch] SnapshotPrefix method not found"); return; }

        PatchAllCtors(harmony, typeof(PlayCardAction), prefix);
        PatchAllCtors(harmony, typeof(EndPlayerTurnAction), prefix);
        PatchAllCtors(harmony, typeof(UsePotionAction), prefix);
        PatchAllCtors(harmony, typeof(DiscardPotionGameAction), prefix);

        // Reset (combat-end) and StartTurn — separate handlers, attribute-patched below.
    }

    private static void PatchAllCtors(Harmony harmony, Type type, MethodInfo prefix)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (ctors.Length == 0)
        {
            UndoLogger.Warn($"[Patch] {type.Name}: no constructors found — snapshot won't fire on this action");
            return;
        }
        int patched = 0;
        foreach (var c in ctors)
        {
            try
            {
                harmony.Patch(c, prefix: new HarmonyMethod(prefix));
                patched++;
            }
            catch (Exception ex)
            {
                UndoLogger.Warn($"[Patch] {type.Name}.ctor({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name))}) failed: {ex.Message}");
            }
        }
        UndoLogger.Info($"[Patch] {type.Name}: patched {patched}/{ctors.Length} constructor(s)");
    }

    public static void SnapshotPrefix(MethodBase __originalMethod)
    {
        // Multiplayer / replay short-circuit. The base game runs its own
        // CombatStateSynchronizer in co-op; layering our deep-clone on top of
        // every action ctor stacks two state snapshots per move and makes
        // co-op sessions unplayably laggy. Bail before we touch anything.
        if (MultiplayerGate.IsDormant()) return;

        // RMB-window suppression: STS2 v0.104.0+ beta reportedly constructs
        // synthetic action instances (e.g. PlayCardAction) to compute the
        // right-click upgrade preview. Each construction would otherwise
        // trigger a full deep-clone capture; on slower machines the resulting
        // stall makes the preview never appear AND the right-click never
        // dismiss (input feels locked).
        //
        // Real player actions are driven by LMB drag/click; RMB is reserved
        // for preview/inspect. Skipping capture while RMB is held (or freshly
        // released) eliminates the storm without affecting any real card play
        // path. The grace window catches deferred preview callbacks that fire
        // a tick after the click.
        if (PatchNGameInput.IsInRmbWindow())
        {
            UndoLogger.Info($"[Snapshot] skipped (RMB preview window) — ctor: {__originalMethod.DeclaringType?.Name}.ctor({string.Join(", ", __originalMethod.GetParameters().Select(p => p.ParameterType.Name))})");
            return;
        }

        UndoController.TakeSnapshot();
        // Trigger log removed from hot path — was firing on every card play
        // and caused disk-I/O frame stutter. Snapshot/restore lifecycle is
        // still observable via [Restore] start lines on undo.
    }
}

[HarmonyPatch(typeof(CombatManager), "Reset", new[] { typeof(bool) })]
public static class PatchCombatReset
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        UndoController.ClearStacks();
        UndoButtonUi.Uninstall();
        CombatSnapshot.IdleAnimCache.Clear();
        DeathAnimDelayPatch.ClearAll();
        AnimDiePatch.ClearDetached();
    }
}

// Combat-end fires before Reset. Drop the stack and remove the button as soon
// as combat finishes (won/lost/escaped) so the keyboard hotkey can't reach a
// stale snapshot during the post-combat transition.
[HarmonyPatch(typeof(CombatManager), "EndCombatInternal")]
public static class PatchEndCombatInternal
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        UndoLogger.Info("[Patch] EndCombatInternal — clearing undo stack");
        UndoController.ClearStacks();
        UndoButtonUi.Uninstall();
        CombatSnapshot.IdleAnimCache.Clear();
        // Combat ended with deferred death(s) still pending — model is dead but
        // their visuals would otherwise stay on screen as corpses behind the
        // win banner. Flush forces the visuals to QueueFree.
        DeathAnimDelayPatch.FlushForCombatEnd();
        // Clear detached NCreatures we held for revive — combat over, no more
        // undos possible, free the dead zombie nodes.
        AnimDiePatch.ClearDetached();
    }
}

// Belt-and-suspenders: when a new combat starts, defensively wipe any leftover
// snapshot from a previous combat's stack. EndCombatInternal + Reset already
// clear, but if an unforeseen path (e.g. immediate map → fight transition with
// a hot-reload) skipped them, the new combat could otherwise inherit stale
// refs to freed creatures / nodes from the prior fight.
[HarmonyPatch(typeof(CombatManager), "StartCombatInternal")]
public static class PatchStartCombatInternal
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        UndoController.ClearStacks();
        CombatSnapshot.IdleAnimCache.Clear();
        AnimDiePatch.ClearDetached();
        DeathAnimDelayPatch.ClearAll();
        // Re-arm the once-per-combat dormant-status log so a co-op session
        // logs "[Undo] dormant" once per fight (not on every action ctor).
        MultiplayerGate.ResetForNewCombat();
        UndoLogger.Info("[Patch] StartCombatInternal — wiped previous-combat residue");
    }
}

[HarmonyPatch(typeof(CombatManager), "StartTurn")]
public static class PatchStartTurn
{
    [HarmonyPostfix]
    public static void Postfix(CombatManager __instance)
    {
        try
        {
            // Multiplayer / replay: skip button install + turn-boundary arming.
            // Snapshots are gated below at SnapshotPrefix, but installing the
            // UI button would still attach a 10 Hz Godot Timer + state polling
            // that adds noise to a co-op session for no benefit (stack stays
            // empty, button would just pulse-disabled forever).
            if (MultiplayerGate.IsDormant()) return;

            var cs = ReflectionCache.CombatManagerStateField.GetValue(__instance) as CombatState;
            if (cs?.CurrentSide == CombatSide.Player)
            {
                UndoController.ArmTurnBoundary();
                UndoButtonUi.Install();
            }
            // Refresh per-creature stable-loop cache. Turn start is a settled
            // moment — every creature is in its true contextual loop (idle/block/
            // low_hp). Without this, cache stays stuck on whatever was observed
            // at the very first snapshot of combat and goes stale.
            CombatSnapshot.RefreshIdleCacheFromLiveCreatures();
        }
        catch (Exception ex) { UndoLogger.Warn($"[Patch] StartTurn: {ex.Message}"); }
    }
}

// AI auto-play coverage. Sibling mods like Sts2CombatAI (Vakuu auto-play)
// drive the player's turn by calling CardModel.SpendResources() and
// CardCmd.AutoPlay() directly — they never construct a PlayCardAction, so
// the ctor-based snapshot patches above silently miss every AI-driven card
// play and undo finds an empty stack. Patching SpendResources fills that
// gap: it's the universal pre-play boundary called by both vanilla
// PlayCardAction.Execute (where the executor is busy → skipped here, the
// ctor patch already captured) and bespoke AI flows (executor idle → we
// capture here, before any state mutation).
//
// The IsActionExecutorBusy gate is what makes this safe to add. Without
// it, manual card plays would snapshot twice (once at ctor, once at
// SpendResources mid-Execute), splitting one logical play into two undo
// entries AND the second snapshot would capture a half-applied state.
[HarmonyPatch(typeof(CardModel), nameof(CardModel.SpendResources))]
public static class PatchCardSpendResources
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        if (MultiplayerGate.IsDormant()) return;
        if (PatchNGameInput.IsInRmbWindow()) return;
        if (IsActionExecutorBusy()) return;

        UndoController.TakeSnapshot();
    }

    private static bool IsActionExecutorBusy()
    {
        try
        {
            var executor = RunManager.Instance?.ActionExecutor;
            if (executor == null) return false;
            // Mirror UndoController.HasInFlightExecutorAction's field probe — the
            // game's ActionExecutor stores the in-flight action under one of
            // these names depending on version; any non-null value means we're
            // mid-execution and another action's ctor already captured.
            foreach (var name in new[] { "_currentAction", "_executingAction", "_action" })
            {
                var f = AccessTools.Field(executor.GetType(), name);
                if (f?.GetValue(executor) != null) return true;
            }
            return false;
        }
        catch { return false; }
    }
}
