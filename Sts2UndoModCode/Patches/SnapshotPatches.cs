using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
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
