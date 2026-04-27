using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using Sts2UndoMod.Sts2UndoModCode.Undo;

namespace Sts2UndoMod.Sts2UndoModCode.Patches;

/// <summary>
/// Hotkeys: Z = undo one step. Shift+Z = undo to previous turn boundary.
/// Reference impl uses Left/Right arrows; we picked Z so arrow keys remain free
/// for in-game navigation if the game uses them.
/// </summary>
[HarmonyPatch(typeof(NGame), "_Input")]
public static class PatchNGameInput
{
    [HarmonyPrefix]
    public static void Prefix(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode != Key.Z) return;

        if (key.ShiftPressed)
            UndoController.UndoTurn();
        else
            UndoController.Undo();
    }
}

