using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2UndoMod.Sts2UndoModCode;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Sts2UndoMod";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; }
        = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        UndoLogger.TruncateProbeLog();
        var harmony = new Harmony(ModId);
        try
        {
            // Probe code (Sts2UndoModCode/Probe/*) intentionally NOT installed in V0.0.2.
            // It remains in the assembly for future diagnostics — call its Install methods
            // manually if you need to re-discover game internals after a game update.

            harmony.PatchAll(typeof(MainFile).Assembly);
            // Imperative ctor-patching for action snapshots — covers all
            // overloads so a game-update parameter change doesn't silently
            // drop our snapshot trigger (e.g. potions stop being undoable).
            Patches.SnapshotPatches.InstallAll(harmony);
            UndoLogger.Info("[Undo] V0.0.2 initialized.");
        }
        catch (Exception ex)
        {
            UndoLogger.Warn($"[Undo] init failed: {ex.Message}");
        }
    }
}
