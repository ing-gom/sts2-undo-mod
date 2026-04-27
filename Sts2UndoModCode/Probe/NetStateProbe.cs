using HarmonyLib;
using System.Reflection;

namespace Sts2UndoMod.Sts2UndoModCode.Probe;

/// <summary>
/// Phase 2 probe focused on the NetFullCombatState pipeline — the multiplayer
/// snapshot/restore system we want to repurpose for undo.
///
/// Goals:
///   1. Dump every method declared on candidate snapshot types so we can pick
///      Capture/Apply/From/To-style entry points.
///   2. Scan the entire sts2 assembly for any method whose return type or
///      parameters reference NetFullCombatState — those are the boundary calls.
///   3. Install tracers on all methods of CombatStateSynchronizer plus any of
///      the boundary methods so a single combat run reveals what actually fires
///      in solo play.
/// </summary>
public static class NetStateProbe
{
    private static readonly string[] InterestingTypeNames =
    {
        "MegaCrit.Sts2.Core.Multiplayer.CombatStateSynchronizer",
        "MegaCrit.Sts2.Core.Multiplayer.CombatStateTracker",
        "MegaCrit.Sts2.Core.Combat.CombatStateTracker",
        "MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState",
        "MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb",
        "MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState",
        "MegaCrit.Sts2.Core.Combat.CombatState",
        "MegaCrit.Sts2.Core.Combat.CombatManager",
        "MegaCrit.Sts2.Core.Entities.Cards.CardPile",
    };

    private static int _tracerCount;

    public static void Install(Harmony harmony)
    {
        var sts2 = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "sts2");
        if (sts2 == null)
        {
            UndoLogger.Warn("[NetStateProbe] sts2 not loaded.");
            return;
        }

        UndoLogger.Probe("NET_BEGIN", "NetFullCombatState pipeline scan");

        // 1. Dump method shapes for the curated type list.
        foreach (var fullName in InterestingTypeNames)
        {
            var t = sts2.GetType(fullName);
            if (t == null)
            {
                UndoLogger.Probe("NET_TYPE_MISS", fullName);
                continue;
            }
            DumpMethods(t);
        }

        // 2. Find anything in sts2 that produces or consumes NetFullCombatState.
        var netFullCombatState = sts2.GetType("MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState");
        if (netFullCombatState != null)
        {
            ScanForBoundaryMethods(sts2, netFullCombatState, harmony);
        }

        // 3. Install tracers on every CombatStateSynchronizer method (it's tiny).
        var synchronizer = sts2.GetType("MegaCrit.Sts2.Core.Multiplayer.CombatStateSynchronizer");
        if (synchronizer != null) InstallAllMethodTracers(harmony, synchronizer);

        // 4. Install tracers on hook-emitting methods of CombatState — these are
        //    the per-card/per-turn boundaries we want to snapshot at.
        var combatState = sts2.GetType("MegaCrit.Sts2.Core.Combat.CombatState");
        if (combatState != null) InstallTracersByName(harmony, combatState, new[]
        {
            "BeforeCardPlayed", "AfterCardPlayed",
            "BeforeTurnEnd", "AfterTurnEnd",
            "BeforePlayerTurnStart", "AfterPlayerTurnStart",
            "BeforeCombatStart", "AfterCombatEnd",
            "InvokeHook", "RunHook",
        });

        UndoLogger.Probe("NET_END", $"installed {_tracerCount} pipeline tracers");
    }

    private static void DumpMethods(Type t)
    {
        UndoLogger.Probe("NET_TYPE", $"{t.FullName} (base={t.BaseType?.Name})");
        try
        {
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance
                                       | BindingFlags.DeclaredOnly);
            foreach (var m in methods.OrderBy(m => m.Name))
            {
                if (m.IsAbstract) continue;
                var args = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                UndoLogger.Probe("NET_METHOD",
                    $"  {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {t.Name}.{m.Name}({args})");
            }

            var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var c in ctors)
            {
                var args = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                UndoLogger.Probe("NET_CTOR", $"  {t.Name}.ctor({args})");
            }
        }
        catch (Exception ex)
        {
            UndoLogger.Probe("NET_TYPE_ERR", $"{t.FullName}: {ex.Message}");
        }
    }

    private static void ScanForBoundaryMethods(Assembly asm, Type netState, Harmony harmony)
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        var boundary = new List<MethodInfo>();
        foreach (var t in types)
        {
            if (t == null) continue;
            MethodInfo[] methods;
            try
            {
                methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance
                                       | BindingFlags.DeclaredOnly);
            }
            catch { continue; }

            foreach (var m in methods)
            {
                if (m.IsAbstract || m.IsGenericMethod) continue;
                bool involvesNet = m.ReturnType == netState
                                || m.GetParameters().Any(p => p.ParameterType == netState);
                if (!involvesNet) continue;

                boundary.Add(m);
                var args = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                UndoLogger.Probe("NET_BOUNDARY",
                    $"{t.FullName}::{m.Name} -> {m.ReturnType.Name}({args})");
            }
        }

        UndoLogger.Probe("NET_BOUNDARY_COUNT", $"{boundary.Count} boundary methods found");

        var prefix = AccessTools.Method(typeof(NetStateProbe), nameof(BoundaryTracer));
        if (prefix == null) return;
        foreach (var m in boundary)
        {
            try
            {
                harmony.Patch(m, prefix: new HarmonyMethod(prefix));
                _tracerCount++;
            }
            catch (Exception ex)
            {
                UndoLogger.Probe("NET_BOUNDARY_FAIL", $"{m.DeclaringType?.FullName}::{m.Name} — {ex.Message}");
            }
        }
    }

    private static void InstallAllMethodTracers(Harmony harmony, Type t)
    {
        var prefix = AccessTools.Method(typeof(NetStateProbe), nameof(SyncTracer));
        if (prefix == null) return;

        try
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Static | BindingFlags.Instance
                                           | BindingFlags.DeclaredOnly))
            {
                if (m.IsAbstract || m.IsGenericMethod) continue;
                // Skip property getters/setters — they're noise.
                if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) continue;
                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(prefix));
                    _tracerCount++;
                }
                catch { /* uninstrumentable */ }
            }
        }
        catch (Exception ex)
        {
            UndoLogger.Probe("NET_SYNC_ERR", $"{t.FullName}: {ex.Message}");
        }
    }

    private static void InstallTracersByName(Harmony harmony, Type t, string[] names)
    {
        var prefix = AccessTools.Method(typeof(NetStateProbe), nameof(HookTracer));
        if (prefix == null) return;

        foreach (var n in names)
        {
            var m = t.GetMethod(n, BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Static | BindingFlags.Instance);
            if (m == null) continue;
            try
            {
                harmony.Patch(m, prefix: new HarmonyMethod(prefix));
                _tracerCount++;
                UndoLogger.Probe("NET_HOOK_OK", $"{t.FullName}::{n}");
            }
            catch (Exception ex)
            {
                UndoLogger.Probe("NET_HOOK_FAIL", $"{t.FullName}::{n} — {ex.Message}");
            }
        }
    }

    public static void BoundaryTracer(MethodBase __originalMethod, object? __instance)
    {
        try
        {
            UndoLogger.Probe("NET_CALL_BND",
                $"{__originalMethod.DeclaringType?.FullName}::{__originalMethod.Name}");
        }
        catch { }
    }

    public static void SyncTracer(MethodBase __originalMethod)
    {
        try
        {
            UndoLogger.Probe("NET_CALL_SYNC",
                $"{__originalMethod.DeclaringType?.Name}::{__originalMethod.Name}");
        }
        catch { }
    }

    public static void HookTracer(MethodBase __originalMethod)
    {
        try
        {
            UndoLogger.Probe("NET_CALL_HOOK",
                $"{__originalMethod.DeclaringType?.Name}::{__originalMethod.Name}");
        }
        catch { }
    }
}
