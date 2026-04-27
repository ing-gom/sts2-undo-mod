using HarmonyLib;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Sts2UndoMod.Sts2UndoModCode.Probe;

/// <summary>
/// Phase 1 diagnostic: discovers candidate combat-related types and methods in sts2.dll
/// and writes them to the probe log. This output drives where Phase 2 (snapshot + restore)
/// will install Harmony patches.
///
/// Strategy:
///   1. Scan sts2 assembly for types whose name OR namespace contains combat keywords.
///   2. For each match, dump declared instance methods + field shapes.
///   3. Also install a global "first call" tracer on a curated list of method-name
///      patterns (PlayCard, EndTurn, StartCombat, etc.) so we can confirm at runtime
///      which ones actually fire during a battle, in what order.
/// </summary>
public static class CombatTypeProbe
{
    // Type/namespace keywords that suggest combat involvement.
    private static readonly string[] TypeKeywords =
    {
        "Combat", "Battle", "Encounter", "Turn", "Player", "Hero",
        "Hand", "Deck", "Draw", "Discard", "Exhaust",
        "Energy", "Card", "Pile",
        "Enemy", "Monster", "Intent",
        "Buff", "Debuff", "Power", "Status",
    };

    // Method-name fragments that, when patched, will trace combat lifecycle.
    private static readonly string[] MethodKeywords =
    {
        "PlayCard", "OnPlayCard", "QueueCard", "UseCard",
        "StartTurn", "BeginTurn", "EndTurn", "OnEndTurn",
        "StartCombat", "BeginCombat", "EndCombat", "OnCombatStart", "OnCombatEnd",
        "OnPlayerDeath", "Die", "OnDeath",
        "DrawCards", "ShuffleDeck",
    };

    // Skip extremely noisy types/namespaces — we only want gameplay code.
    private static readonly Regex IgnoreRegex = new(
        @"^(System|Microsoft|Godot|Sentry|HarmonyLib|MonoMod|MegaCrit\.Sts2\.Core\.Logging|MegaCrit\.Sts2\.Core\.Modding|JetBrains)\b",
        RegexOptions.Compiled);

    private static int _tracerCount;

    public static void Install(Harmony harmony)
    {
        Assembly? sts2 = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "sts2");
        if (sts2 == null)
        {
            UndoLogger.Warn("[Probe] sts2.dll not found in loaded assemblies — probe disabled.");
            return;
        }

        UndoLogger.Probe("ASSEMBLY", $"sts2 location: {sts2.Location}");

        Type[] types;
        try { types = sts2.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        var matched = types
            .Where(t => t != null && t.FullName != null && !IgnoreRegex.IsMatch(t.FullName))
            .Where(MatchesTypeKeyword)
            .OrderBy(t => t.FullName)
            .ToArray();

        UndoLogger.Probe("TYPES_BEGIN", $"{matched.Length} candidate combat types");
        foreach (var t in matched) DumpType(t);
        UndoLogger.Probe("TYPES_END", "");

        // Phase 1.5: install lifecycle tracers on candidate methods.
        InstallTracers(harmony, matched);
        UndoLogger.Probe("TRACERS", $"installed {_tracerCount} lifecycle tracers");
    }

    private static bool MatchesTypeKeyword(Type t)
    {
        var name = t.FullName ?? t.Name;
        return TypeKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static void DumpType(Type t)
    {
        try
        {
            var fields = t
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => !f.Name.Contains("k__BackingField"))
                .Select(f => $"{f.FieldType.Name} {f.Name}")
                .ToArray();

            UndoLogger.Probe("TYPE", $"{t.FullName} : {t.BaseType?.Name ?? "?"} (fields={fields.Length})");
            foreach (var f in fields.Take(40))
                UndoLogger.Probe("FIELD", $"  {t.Name}.{f}");
            if (fields.Length > 40)
                UndoLogger.Probe("FIELD", $"  ... ({fields.Length - 40} more truncated)");
        }
        catch (Exception ex)
        {
            UndoLogger.Probe("TYPE_ERR", $"{t.FullName}: {ex.Message}");
        }
    }

    private static void InstallTracers(Harmony harmony, Type[] types)
    {
        var prefix = AccessTools.Method(typeof(CombatTypeProbe), nameof(Tracer));
        if (prefix == null) return;

        foreach (var t in types)
        {
            MethodInfo[] methods;
            try
            {
                methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            }
            catch { continue; }

            foreach (var m in methods)
            {
                if (m.IsAbstract || m.IsGenericMethod) continue;
                if (!MethodKeywords.Any(k => m.Name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                // Skip noisy accessor/validator-style methods — we only want lifecycle.
                if (IsNoisyAccessor(m.Name)) continue;

                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(prefix));
                    _tracerCount++;
                    UndoLogger.Probe("TRACER_OK", $"{t.FullName}::{m.Name}");
                }
                catch (Exception ex)
                {
                    UndoLogger.Probe("TRACER_FAIL", $"{t.FullName}::{m.Name} — {ex.Message}");
                }
            }
        }
    }

    private static bool IsNoisyAccessor(string name)
        => name.StartsWith("get_", StringComparison.Ordinal)
        || name.StartsWith("set_", StringComparison.Ordinal)
        || name.StartsWith("Is", StringComparison.Ordinal)
        || name.StartsWith("Can", StringComparison.Ordinal)
        || name.StartsWith("AutoDisable", StringComparison.Ordinal);

    // Harmony prefix — must be static, must NOT throw. Just records the call.
    public static void Tracer(MethodBase __originalMethod, object? __instance)
    {
        try
        {
            UndoLogger.Probe("CALL",
                $"{__originalMethod.DeclaringType?.FullName}::{__originalMethod.Name}" +
                (__instance != null ? $" on {__instance.GetType().Name}" : ""));
        }
        catch { /* tracer must never throw */ }
    }
}
