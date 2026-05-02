using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using Sts2UndoMod.Sts2UndoModCode.Undo;

namespace Sts2UndoMod.Sts2UndoModCode.Patches;

/// <summary>
/// Hotkeys: Z = undo one step. Shift+Z = undo to previous turn boundary.
/// Reference impl uses Left/Right arrows; we picked Z so arrow keys remain free
/// for in-game navigation if the game uses them.
///
/// Also tracks RMB (right-mouse-button) state so SnapshotPatches can suppress
/// snapshot capture during right-click upgrade preview. STS2 v0.104.0+ beta
/// reportedly constructs synthetic action instances (PlayCardAction etc.) to
/// compute the preview, which our ctor-prefix would otherwise treat as a real
/// player action and run a full deep-clone capture on every preview — locking
/// the preview UI on slower machines.
/// </summary>
[HarmonyPatch(typeof(NGame), "_Input")]
public static class PatchNGameInput
{
    /// <summary>
    /// True while the right mouse button is held down anywhere in the game.
    /// Read by SnapshotPatches.SnapshotPrefix to skip capture during the
    /// right-click upgrade preview window.
    /// </summary>
    public static bool RmbHeld { get; private set; }

    /// <summary>TickCount64 of the most recent RMB up event. Snapshot prefix
    /// also skips capture for a short grace window after release, so a ctor
    /// fired by a deferred preview callback right after RMB-up still gets
    /// suppressed.</summary>
    public static long RmbReleasedAtMs { get; private set; }

    /// <summary>How long after RMB-up to keep suppressing snapshot capture.</summary>
    public const long RmbGraceMs = 250;

    [HarmonyPrefix]
    public static void Prefix(InputEvent inputEvent)
    {
        // Track RMB up/down state. Cheap — runs on every input event.
        if (inputEvent is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            if (mb.Pressed) RmbHeld = true;
            else
            {
                RmbHeld = false;
                RmbReleasedAtMs = System.Environment.TickCount64;
            }
        }

        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode != Key.Z) return;

        if (key.ShiftPressed)
            UndoController.UndoTurn();
        else
            UndoController.Undo();
    }

    /// <summary>True if RMB is currently held OR was released within the
    /// grace window. Used as the snapshot-suppression gate.</summary>
    public static bool IsInRmbWindow()
        => RmbHeld
           || (System.Environment.TickCount64 - RmbReleasedAtMs) <= RmbGraceMs;
}

