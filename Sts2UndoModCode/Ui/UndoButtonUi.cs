using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using Sts2UndoMod.Sts2UndoModCode.Undo;

namespace Sts2UndoMod.Sts2UndoModCode.Ui;

/// <summary>
/// In-combat undo button placed near the energy display. Mirrors the Z hotkey.
/// Created on combat start, freed on combat end.
/// </summary>
internal static class UndoButtonUi
{
    private static Button? _button;
    private static Node? _hostParent;
    private static Node? _anchor;
    private static long _pulseStartMs;
    private static bool _lastDisabled;

    // Right-mouse-button drag state. Left click stays bound to "undo" so the
    // button keeps its primary action; relocation uses RMB to avoid conflict.
    private static bool _isDragging;
    private static Vector2 _dragGrabOffset;
    private static Vector2 _dragStartPos;
    private const float DragMinPixels = 3f;  // ignore micro-jitter on RMB-up

    // STS2-inspired palette: warm dark leather + gold filigree + parchment text.
    private static readonly Color BgNormal   = Color.FromHtml("#1F140B");
    private static readonly Color BgHover    = Color.FromHtml("#34220F");
    private static readonly Color BgPressed  = Color.FromHtml("#0F0905");
    private static readonly Color BgDisabled = Color.FromHtml("#1A130C");
    private static readonly Color BorderGold     = Color.FromHtml("#C8A45A");
    private static readonly Color BorderGoldHi   = Color.FromHtml("#F2D080");
    private static readonly Color BorderDim      = Color.FromHtml("#5A4828");
    private static readonly Color TextParchment  = Color.FromHtml("#F2E2B8");
    private static readonly Color TextHi         = Color.FromHtml("#FFF6D8");
    private static readonly Color TextDim        = Color.FromHtml("#7A6A48");

    public static void Install()
    {
        if (_button != null && GodotObject.IsInstanceValid(_button)) return;

        var anchor = FindEnergyAnchor();
        if (anchor == null)
        {
            UndoLogger.Warn("[Ui] energy anchor not found — undo button skipped");
            return;
        }
        var parent = anchor.GetParent();
        if (parent == null) return;

        var btn = new Button
        {
            Text = "↶ Z",
            TooltipText = LocalizedTooltip(),
            CustomMinimumSize = new Vector2(64, 64),
            FocusMode = Control.FocusModeEnum.None,  // never steal keyboard focus
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        ApplyTheme(btn);
        btn.Pressed += OnPressed;
        btn.GuiInput += OnButtonGuiInput;

        // Default position: to the LEFT of the energy anchor, lowered by ~half
        // a button height from the previous (raised) placement.
        Vector2 anchorPos = ReadPosition(anchor);
        Vector2 anchorSize = ReadSize(anchor);
        Vector2 defaultPos = anchorPos + new Vector2(
            -btn.CustomMinimumSize.X - 20,
            -anchorSize.Y * 0.35f - 12 + btn.CustomMinimumSize.Y * 0.5f);

        // User-saved override (set by right-click drag, persisted to settings.json).
        var savedX = ModSettings.Data.IconX;
        var savedY = ModSettings.Data.IconY;
        btn.Position = (savedX is float sx && savedY is float sy)
            ? new Vector2(sx, sy)
            : defaultPos;

        parent.AddChild(btn);
        _button = btn;
        _hostParent = parent;
        _anchor = anchor;

        // Periodic poll: toggle Disabled based on undo availability + drive
        // pulse effect + refresh idle-anim cache from live creatures.
        var timer = new Godot.Timer
        {
            WaitTime = 0.1,    // 10 Hz
            Autostart = true,
            OneShot = false,
            ProcessCallback = Godot.Timer.TimerProcessCallback.Idle,
        };
        timer.Timeout += OnPollTick;
        btn.AddChild(timer);

        _pulseStartMs = System.Environment.TickCount64;
        _lastDisabled = false;

        UndoLogger.Info($"[Ui] undo button installed near {anchor.GetType().Name} at {btn.Position}");
    }

    private static void OnPollTick()
    {
        if (_button == null || !GodotObject.IsInstanceValid(_button)) return;

        // Refresh per-creature stable-loop cache off the timer.
        try { Snapshot.CombatSnapshot.RefreshIdleCacheFromLiveCreatures(); }
        catch { }

        // Reconcile HP bar visibility on revive-power creatures (IllusionPower
        // etc.). Vanilla's AnimateOut/AnimateIn for these creatures sometimes
        // doesn't fire across rapid death+revive cycles — leaves the HP bar
        // stuck visible after death (showing "DEAD" text from RefreshText) or
        // stuck hidden after revive. We can't intercept vanilla's flow without
        // breaking the revive itself, so we drift-correct from the timer.
        try { ReconcileReviveCreatureHpBars(); } catch { }

        bool canUndo = false;
        try { canUndo = UndoController.CanRestoreNowPublic(); } catch { }

        if (_button.Disabled != !canUndo)
        {
            _button.Disabled = !canUndo;
            _lastDisabled = !canUndo;
            if (_button.Disabled) _pulseStartMs = System.Environment.TickCount64;
            else _button.Modulate = Colors.White;
        }

        // Subtle alpha pulse while waiting (disabled).
        if (_button.Disabled)
        {
            double t = (System.Environment.TickCount64 - _pulseStartMs) / 1000.0;
            float alpha = 0.55f + 0.20f * (float)Math.Sin(t * Math.PI * 1.4);  // ~0.7 Hz pulse
            var m = _button.Modulate;
            m.A = alpha;
            _button.Modulate = m;
        }
    }

    /// <summary>
    /// Walk live creatures and ensure StateDisplay (HP bar / nameplate)
    /// visibility matches the model state for revive-power creatures (where
    /// vanilla's AnimateOut/AnimateIn sometimes fails to fire). Two correction
    /// cases:
    ///   - alive (IsDead==false) but bar hidden (Visible==false || A==0)
    ///     → AnimateIn never ran → force visible.
    ///   - dead (IsDead==true) but bar fully visible (Visible==true && A==1f)
    ///     → AnimateOut never ran → force hidden.
    /// Mid-tween states (A between 0 and 1) are left alone to avoid fighting
    /// an in-progress vanilla animation.
    ///
    /// Limited to revive-power creatures so normal creatures' StateDisplay
    /// lifecycle is untouched.
    /// </summary>
    private static void ReconcileReviveCreatureHpBars()
    {
        var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
        if (cm == null) return;
        var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
        if (room == null) return;
        var cs = Snapshot.ReflectionCache.CombatManagerStateField?.GetValue(cm)
            as MegaCrit.Sts2.Core.Combat.CombatState;
        if (cs == null) return;

        foreach (var c in cs.Creatures)
        {
            if (c == null) continue;
            var nc = room.GetCreatureNode(c);
            if (nc == null) continue;
            if (Patches.AnimDiePatch.FindReviveLikePower(nc) == null) continue;

            bool wantVisible = !c.IsDead;
            foreach (var n in WalkTree(nc))
            {
                if (n is not MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay sd) continue;
                try
                {
                    var mod = sd.Modulate;
                    if (wantVisible)
                    {
                        // Stuck hidden after revive → fully unhide.
                        if (!sd.Visible || mod.A <= 0.01f)
                        {
                            sd.Visible = true;
                            mod.A = 1f;
                            sd.Modulate = mod;
                        }
                    }
                    else
                    {
                        // Stuck fully visible after death (vanilla AnimateOut
                        // never ran) → fully hide. Mid-tween (0 < A < 1) means
                        // AnimateOut IS in progress; leave alone.
                        if (sd.Visible && mod.A >= 0.999f)
                        {
                            sd.Visible = false;
                            mod.A = 0f;
                            sd.Modulate = mod;
                        }
                    }
                }
                catch { }
            }
        }
    }

    private static IEnumerable<Node> WalkTree(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
    }

    public static void Uninstall()
    {
        if (_button == null) return;
        try
        {
            if (GodotObject.IsInstanceValid(_button))
            {
                _button.Pressed -= OnPressed;
                _button.GuiInput -= OnButtonGuiInput;
                _button.QueueFree();
            }
        }
        catch { }
        _button = null;
        _hostParent = null;
        _anchor = null;
        _isDragging = false;
    }

    private static void OnPressed()
    {
        // Suppress the implicit "left-click released over the button" undo
        // when the user just finished a right-click reposition — the Pressed
        // signal still fires for the LMB sequence even when interleaved with
        // RMB drag in some Godot input flows. Belt-and-braces.
        if (_isDragging) return;
        UndoController.Undo();
    }

    private static void OnButtonGuiInput(InputEvent ev)
    {
        if (_button == null || !GodotObject.IsInstanceValid(_button)) return;

        if (ev is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            if (mb.Pressed)
            {
                _isDragging = true;
                _dragStartPos = _button.Position;
                // Anchor the cursor inside the button so dragging feels natural
                // (button corner doesn't snap to mouse position).
                _dragGrabOffset = mb.Position;
                _button.AcceptEvent();
            }
            else  // released
            {
                if (_isDragging)
                {
                    _isDragging = false;
                    var moved = (_button.Position - _dragStartPos).Length();
                    if (moved >= DragMinPixels)
                    {
                        ModSettings.SetIconPosition(_button.Position.X, _button.Position.Y);
                        UndoLogger.Info($"[Ui] icon repositioned to {_button.Position} — saved");
                    }
                    _button.AcceptEvent();
                }
            }
            return;
        }

        if (ev is InputEventMouseMotion mm && _isDragging)
        {
            // mm.Position is relative to the button — subtract the grab offset
            // to keep the same anchor point under the cursor.
            _button.Position += mm.Position - _dragGrabOffset;
            _button.AcceptEvent();
        }
    }

    private static void ApplyTheme(Button btn)
    {
        btn.AddThemeStyleboxOverride("normal",   BuildPanel(BgNormal,   BorderGold,   2));
        btn.AddThemeStyleboxOverride("hover",    BuildPanel(BgHover,    BorderGoldHi, 3));
        btn.AddThemeStyleboxOverride("pressed",  BuildPanel(BgPressed,  BorderGoldHi, 3, pressed: true));
        btn.AddThemeStyleboxOverride("disabled", BuildPanel(BgDisabled, BorderDim,    2));
        btn.AddThemeStyleboxOverride("focus",    new StyleBoxEmpty());

        btn.AddThemeColorOverride("font_color",          TextParchment);
        btn.AddThemeColorOverride("font_hover_color",    TextHi);
        btn.AddThemeColorOverride("font_pressed_color",  TextParchment);
        btn.AddThemeColorOverride("font_disabled_color", TextDim);
        btn.AddThemeColorOverride("font_outline_color",  new Color(0, 0, 0, 0.85f));
        btn.AddThemeConstantOverride("outline_size", 4);
        btn.AddThemeFontSizeOverride("font_size", 22);
    }

    private static StyleBoxFlat BuildPanel(Color bg, Color border, int borderWidth, bool pressed = false)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
            ShadowColor = new Color(0, 0, 0, pressed ? 0.35f : 0.7f),
            ShadowSize = pressed ? 2 : 6,
            ShadowOffset = new Vector2(0, pressed ? 1 : 3),
            AntiAliasing = true,
        };
        sb.SetBorderWidthAll(borderWidth);
        return sb;
    }

    /// <summary>
    /// Locate the energy display node. Restricted to <c>NEnergyCounter</c>
    /// instances under <c>NCombatUi</c>. Earlier substring-match logic
    /// matched anything containing "Energy" (e.g. CardEnergyCost on each
    /// hand card, EnergySurge card name) which made the undo button
    /// reparent under whichever card the search found first — the button
    /// then dragged around with that card whenever it moved.
    /// </summary>
    private static Node? FindEnergyAnchor()
    {
        var ngame = NGame.Instance;
        if (ngame == null) return null;

        // Locate NCombatUi by walking; it doesn't expose a static Instance.
        NCombatUi? ui = null;
        foreach (var n in EnumerateTree(ngame))
        {
            if (n is NCombatUi found) { ui = found; break; }
        }

        Node? Pick(Node root)
        {
            foreach (var n in EnumerateTree(root))
            {
                // Only the canonical energy counter node, never card children.
                if (n.GetType().Name == "NEnergyCounter") return n;
            }
            return null;
        }

        if (ui != null)
        {
            var byUi = Pick(ui);
            if (byUi != null) return byUi;
        }
        return Pick(ngame);
    }

    private static Vector2 ReadPosition(Node n)
    {
        var prop = AccessTools.Property(n.GetType(), "Position");
        if (prop?.GetValue(n) is Vector2 v) return v;
        return Vector2.Zero;
    }

    private static Vector2 ReadSize(Node n)
    {
        var prop = AccessTools.Property(n.GetType(), "Size");
        if (prop?.GetValue(n) is Vector2 v) return v;
        var customMin = AccessTools.Property(n.GetType(), "CustomMinimumSize");
        if (customMin?.GetValue(n) is Vector2 c) return c;
        return new Vector2(64, 64);
    }

    private static string LocalizedTooltip()
    {
        // Korean locale codes from Godot are "ko" / "ko_KR" — prefix-match covers both.
        var locale = TranslationServer.GetLocale() ?? string.Empty;
        if (locale.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return "되돌리기 (Z) / 턴 되돌리기 (Shift+Z)\n우클릭 드래그로 위치 이동";
        return "Undo (Z) / Undo Turn (Shift+Z)\nRight-click + drag to move";
    }

    private static IEnumerable<Node> EnumerateTree(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
    }
}
