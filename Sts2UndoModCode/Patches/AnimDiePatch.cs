using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace Sts2UndoMod.Sts2UndoModCode.Patches;

/// <summary>
/// Replace <c>NCreature.AnimDie(bool, CancellationToken)</c> with our own
/// implementation that hides the body via modulate-alpha=0 instead of freeing
/// it. The original AnimDie's destructive cleanup (QueueFree on body, null
/// out _body) cannot be cleanly reversed by undo:
///   - cancel triggers AnimDie's finally chain which still frees body
///   - clearing the task field doesn't stop the running async task
///   - even after our cancel/StartReviveAnim experiments, the natural
///     completion of AnimDie disposes body within ~5s of death start
///
/// By replacing AnimDie at the Harmony level we cleanly intercept the
/// destruction. Body stays alive in the scene tree (just transparent).
/// On undo, restoring Modulate.A=1 makes the creature visible again
/// instantly. On combat end, normal scene tear-down frees everything.
///
/// Visual trade-off: we lose the original death animation (spine 'die'
/// track + collapse tween + fade). Replacement uses a simple alpha=0 set.
/// We keep the spine 'die' pose visible for ~0.5s before hiding so the
/// player at least sees a death indication. Tween or sleep-based fade
/// is intentionally avoided to keep the patch simple and side-effect-free.
/// </summary>
[HarmonyPatch(typeof(NCreature), "AnimDie")]
public static class AnimDiePatch
{
    /// <summary>
    /// Fallback wait if we cannot read the actual spine 'die' animation length.
    /// </summary>
    public const float DieVisibleFallbackSeconds = 1.5f;

    // Re-enabled fade tween 2026-04-28 with stricter lifecycle:
    //   - StartFadeOutTween kills any prior tween for this creature BEFORE
    //     storing the new one (the previous accumulation bug came from
    //     dict[creature] = newTween overwriting the old without killing it).
    //   - TryInPlaceRevive kills + clears + resets body.Modulate.A on revive.
    //   - ClearDetached kills all on combat end.

    /// <summary>
    /// NCreatures we've detached on death, keyed by their model Creature.
    /// Used by SnapshotRestorer.TryInPlaceRevive to find the zombie even
    /// when normal scene-tree walk and `_removingCreatureNodes` list don't
    /// contain it (we removed it from EnemyContainer's tree).
    /// </summary>
    public static readonly Dictionary<Creature, NCreature> DetachedZombies = new();

    /// <summary>
    /// Drop entries whose NCreature has been freed by Godot. Without periodic
    /// pruning these dict entries accumulate across combats / kill+revive
    /// cycles, slowly inflating capture-time tree-walks that key off this
    /// registry.
    /// </summary>
    public static int PruneStaleDetached()
    {
        if (DetachedZombies.Count == 0) return 0;
        var stale = new List<Creature>();
        foreach (var kv in DetachedZombies)
        {
            try
            {
                if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value))
                    stale.Add(kv.Key);
            }
            catch { stale.Add(kv.Key); }
        }
        foreach (var k in stale) DetachedZombies.Remove(k);
        return stale.Count;
    }

    /// <summary>
    /// In-flight fade-out tweens we created on each NCreature. Keyed by the
    /// NCreature instance — when undo revives the creature, the tween is still
    /// lerping `modulate:a → 0`, so we kill it and reset modulate. Without
    /// this, the revived body progressively fades to invisible while the
    /// player wonders why the sprite looks transparent (semi-translucent
    /// graphic with floor showing through, mistakable for a stuck overlay).
    /// </summary>
    public static readonly Dictionary<NCreature, Tween> ActiveFadeTweens = new();

    /// <summary>
    /// Count of `RunReplacementDeathAnim` async tasks currently in-flight.
    /// Used by `UndoController` to block undo while any death anim / fade
    /// VFX is still playing — undoing mid-VFX has TOCTOU races where the
    /// async continuation reaches the detach branch right after our restore
    /// re-attached the body, leaving the visual gone.
    /// </summary>
    public static int InFlightCount;

    /// <summary>
    /// Combat-end / Reset entry to free our held references and let the
    /// detached NCreatures be GC'd. Without this, dead bodies linger
    /// across combats.
    /// </summary>
    public static void ClearDetached()
    {
        if (DetachedZombies.Count == 0 && ActiveFadeTweens.Count == 0) return;
        int freed = 0;
        foreach (var kv in DetachedZombies)
        {
            try
            {
                if (GodotObject.IsInstanceValid(kv.Value))
                {
                    kv.Value.QueueFree();
                    freed++;
                }
            }
            catch { }
        }
        DetachedZombies.Clear();

        // Kill any still-running fade tweens too — combat-tear-down would
        // free the creatures anyway, but lingering tweens have caused issues
        // (animating freed nodes throws). Defensive Kill before clear.
        foreach (var kv in ActiveFadeTweens)
        {
            try
            {
                var tw = kv.Value;
                if (tw != null && GodotObject.IsInstanceValid(tw) && tw.IsValid()) tw.Kill();
            }
            catch { }
        }
        ActiveFadeTweens.Clear();

        // Reset in-flight counter — combat tear-down should mean nothing is
        // waiting on our async chain anymore. If we leave it positive, the
        // next combat's first undo will be blocked unnecessarily.
        InFlightCount = 0;

        UndoLogger.Info($"[AnimDie] cleared {freed} detached zombies on combat end");
    }

    /// <summary>
    /// Monster type names whose vanilla AnimDie runs special logic (phase
    /// transitions, spawn-on-death, custom revive) that breaks when we detach
    /// the NCreature out of the scene tree. Reported symptom: phase-1 dies,
    /// the body disappears, and the game freezes because the phase-2 spawn /
    /// revive logic can no longer find the node it expected.
    ///
    /// Matched against `monster.GetType().Name` (no namespace). Pass-through
    /// to vanilla AnimDie for these — undo across their death isn't supported,
    /// but the fight stops freezing.
    /// </summary>
    public static readonly HashSet<string> SkipReplacementMonsterTypes = new()
    {
        "TestSubject", // 실험체 — phase-1 → phase-2 transition; reported 2026-04-29
    };

    [HarmonyPrefix]
    public static bool Prefix(NCreature __instance, bool __0, ref System.Threading.Tasks.Task __result)
    {
        try
        {
            // Some monsters don't use a one-shot 'die' spine track — Centipede
            // (만각지네) for example only has `dead_loop` and a self-revive
            // mechanism in the game's own AnimDie. Replacing AnimDie for them
            // breaks the revive path: our replacement detaches the NCreature,
            // then the game's revive logic can't find the node and the combat
            // freezes. Skip our replacement and let vanilla AnimDie run for
            // those monsters.
            if (!HasDieAnimation(__instance))
            {
                UndoLogger.Info($"[AnimDie] no 'die' spine anim on instId={__instance.GetInstanceId()} monster={GetMonsterTypeName(__instance)} — passing through to vanilla AnimDie");
                return true; // run original
            }

            // Some monsters have a normal 'die' spine anim but their vanilla
            // AnimDie still runs phase-transition / spawn-on-death logic that
            // our detach interferes with. Skip our replacement for those by
            // type name (see SkipReplacementMonsterTypes).
            var monsterTypeName = GetMonsterTypeName(__instance);
            if (monsterTypeName != null && SkipReplacementMonsterTypes.Contains(monsterTypeName))
            {
                UndoLogger.Info($"[AnimDie] monster={monsterTypeName} on skiplist — passing through to vanilla AnimDie");
                return true; // run original
            }

            __result = RunReplacementDeathAnim(__instance, __0);
            return false; // skip original AnimDie — never frees body
        }
        catch (Exception ex)
        {
            UndoLogger.Warn($"[AnimDie] replacement setup failed, falling back to original: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Best-effort `monster.GetType().Name` for the entity bound to this
    /// NCreature. Used both for diagnostic logging and for the skiplist
    /// lookup. Returns null if anything in the chain is missing — caller
    /// treats null as "don't apply skiplist".
    /// </summary>
    private static string? GetMonsterTypeName(NCreature creature)
    {
        try
        {
            var monster = creature?.Entity?.Monster;
            return monster?.GetType().Name;
        }
        catch { return null; }
    }

    /// <summary>
    /// True if the creature's spine skeleton has a `die` animation. Used to
    /// decide between our reversible AnimDie replacement and letting vanilla
    /// AnimDie run for monsters with custom death/revive flows that depend
    /// on spine state we don't model (e.g. `dead_loop` self-revive).
    /// </summary>
    private static bool HasDieAnimation(NCreature creature)
    {
        try
        {
            var visualsType = Snapshot.ReflectionCache.NCreatureVisualsType;
            if (visualsType == null) return true; // assume yes; safer to keep current behavior
            var visuals = creature.Visuals;
            if (visuals == null) return true;
            var spineBodyProp = HarmonyLib.AccessTools.Property(visualsType, "SpineBody");
            var megaSprite = spineBodyProp?.GetValue(visuals);
            if (megaSprite == null) return true;
            // MegaSprite.HasAnimation(string) returns bool.
            var hasAnim = HarmonyLib.AccessTools.Method(megaSprite.GetType(), "HasAnimation",
                new[] { typeof(string) });
            if (hasAnim == null) return true;
            return hasAnim.Invoke(megaSprite, new object[] { "die" }) is bool b ? b : true;
        }
        catch { return true; }
    }

    private static async System.Threading.Tasks.Task RunReplacementDeathAnim(
        NCreature creature, bool argZero)
    {
        if (creature == null || !GodotObject.IsInstanceValid(creature)) return;

        InFlightCount++;
        try
        {
            await RunReplacementDeathAnimInner(creature, argZero);
        }
        finally
        {
            InFlightCount--;
        }
    }

    private static async System.Threading.Tasks.Task RunReplacementDeathAnimInner(
        NCreature creature, bool argZero)
    {
        UndoLogger.Info($"[AnimDie] replacement starting on instId={creature.GetInstanceId()} monster={GetMonsterTypeName(creature) ?? "?"} arg={argZero}");

        // Play spine 'die' track for visual feedback. Best-effort — if any
        // step fails we just skip to the modulate hide.
        float dieDuration = TryPlaySpineDie(creature);

        // Phase 1: wait for the FULL spine 'die' anim to play out — the user
        // wants the "쓰러지고 (collapse)" pose to be visible end-to-end. No
        // cap; long-anim monsters (3-segment centipede etc.) get their full
        // closure.
        float spineWait = dieDuration > 0f ? dieDuration : DieVisibleFallbackSeconds;
        try
        {
            var tree = creature.GetTree();
            if (tree != null)
            {
                var timer = tree.CreateTimer(spineWait);
                await creature.ToSignal(timer, "timeout");
            }
        }
        catch (Exception ex)
        {
            UndoLogger.Warn($"[AnimDie] spine wait failed: {ex.Message}");
        }

        if (!GodotObject.IsInstanceValid(creature)) return;

        // Phase 2: trigger NMonsterDeathVfx (the "재에 날라가듯" ash/cinder
        // dissolve) and DETACH IMMEDIATELY after starting it. The vfx
        // reparents the body into its own SubViewport before dissolving, so
        // the body keeps rendering with the cinder shader even after NCreature
        // (HP bar, intent shell) is detached. This way the ash effect is
        // visible without holding the entire NCreature subtree on-screen for
        // the full 2.5s vfx duration.
        TriggerDeathFadeVfxFireAndForget(creature);

        if (!GodotObject.IsInstanceValid(creature)) return;

        // Revive-during-anim guard. SnapshotRestorer.TryInPlaceRevive may have
        // pulled the creature back to alive while we were waiting on the
        // timer — Godot SceneTreeTimer fires regardless of any cancellation
        // and our continuation has no token to honour. If the underlying
        // entity is no longer dead, abort the detach: doing it would
        // RemoveChild a fully revived NCreature, leaving the model alive but
        // the visuals (sprite, HP bar, intent) gone. User-reported symptom:
        // "after a few kill+undo cycles the enemy disappears entirely while
        // the card targeting still works".
        try
        {
            var entityCheck = creature.Entity;
            if (entityCheck != null && !entityCheck.IsDead)
            {
                UndoLogger.Info($"[AnimDie] revived during wait — skipping detach for instId={creature.GetInstanceId()}");
                return;
            }
        }
        catch { /* if we can't tell, fall through to the original detach */ }

        // Detach the entire NCreature from its parent (typically
        // EnemyContainer or AllyContainer). The whole creature subtree —
        // HP bar, intent display, hitbox, body, spine — disappears in a
        // single step. Hiding only body leaves the HP bar / death indicator
        // / hitbox visible at the slot, which is the wrong visual for a
        // dead enemy. The original game's AnimDie eventually QueueFree's
        // NCreature for the same effect; we reversibly detach instead so
        // undo can AddChild it back. Existing TryInPlaceRevive code
        // re-attaches when GetParent()==null on revive.
        try
        {
            // Track the entity → NCreature mapping so revive can locate the
            // zombie even though it's no longer in the scene tree.
            try
            {
                var entity = creature.Entity;
                if (entity != null) DetachedZombies[entity] = creature;
            }
            catch (Exception ex) { UndoLogger.Warn($"[AnimDie] register detached failed: {ex.Message}"); }

            var parent = creature.GetParent();
            if (parent != null)
            {
                parent.RemoveChild(creature);
                UndoLogger.Info($"[AnimDie] detached NCreature from {parent.GetType().Name}:'{parent.Name}' (registered for revive)");
            }
            else
            {
                UndoLogger.Info($"[AnimDie] NCreature already orphaned");
            }
        }
        catch (Exception ex)
        {
            UndoLogger.Warn($"[AnimDie] detach NCreature failed: {ex.Message}");
        }

        UndoLogger.Info($"[AnimDie] replacement complete on instId={creature.GetInstanceId()} — NCreature detached");
    }

    /// <summary>
    /// Tween body.Modulate.Alpha 1 → 0 over <paramref name="duration"/> seconds
    /// so the visual sprite fades out concurrently with the spine 'die' anim.
    /// Strict lifecycle: kill any prior tween for this creature before storing
    /// the new one (the prior accumulation bug came from overwriting the dict
    /// without killing the leftover tween, leaving it driving modulate→0
    /// past revive).
    /// </summary>
    private static void StartFadeOutTween(NCreature creature, float duration)
    {
        try
        {
            // Kill leftover tween from a prior death of this same creature.
            if (ActiveFadeTweens.TryGetValue(creature, out var existing))
            {
                try
                {
                    if (existing != null && GodotObject.IsInstanceValid(existing) && existing.IsValid())
                        existing.Kill();
                }
                catch { }
                ActiveFadeTweens.Remove(creature);
            }

            var body = creature.Body;
            if (body is not Godot.CanvasItem bodyCi) return;

            // Reset body alpha to current full value first — if a previous
            // partial fade left it at e.g. 0.5, the new tween starting at 0.5
            // would skip the visible fade.
            var m = bodyCi.Modulate;
            bodyCi.Modulate = new Godot.Color(m.R, m.G, m.B, 1f);

            var tween = creature.CreateTween();
            tween.TweenProperty(bodyCi, "modulate:a", 0f, duration);
            ActiveFadeTweens[creature] = tween;
        }
        catch (Exception ex) { UndoLogger.Warn($"[AnimDie] fade tween: {ex.Message}"); }
    }

    /// <summary>
    /// Trigger NMonsterDeathVfx (the "ash blowing away" cinder dissolve) and
    /// return immediately without awaiting completion. The vfx reparents
    /// `creature.Body` into its own SubViewport — once that's done the body
    /// keeps rendering with the cinder shader regardless of whether NCreature
    /// is still in the tree. Caller can detach NCreature right after, and
    /// the ash effect plays out on its own (~2.5s shader threshold tween).
    /// </summary>
    private static void TriggerDeathFadeVfxFireAndForget(NCreature creature)
    {
        try
        {
            var entity = creature.Entity;
            var monster = entity?.Monster;
            if (monster == null) return;

            var shouldFadeProp = HarmonyLib.AccessTools.Property(monster.GetType(), "ShouldFadeAfterDeath");
            bool shouldFade = shouldFadeProp?.GetValue(monster) is bool b && b;
            if (!shouldFade) return;

            var bodyVisible = creature.Body?.IsVisibleInTree() ?? false;
            if (!bodyVisible) return;

            var vfxType = HarmonyLib.AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Nodes.Vfx.NMonsterDeathVfx");
            if (vfxType == null) return;

            var createMethod = HarmonyLib.AccessTools.Method(vfxType, "Create",
                new[] { typeof(NCreature), typeof(System.Threading.CancellationToken) });
            if (createMethod == null) return;
            var token = new System.Threading.CancellationTokenSource().Token;
            var vfx = createMethod.Invoke(null, new object[] { creature, token });
            if (vfx is not Godot.Node vfxNode) return;

            var parent = creature.GetParent();
            if (parent == null) return;

            var addSafely = HarmonyLib.AccessTools.Method(parent.GetType(), "AddChildSafely");
            try
            {
                if (addSafely != null) addSafely.Invoke(null, new object[] { parent, vfxNode });
                else parent.AddChild(vfxNode);
            }
            catch
            {
                try { parent.AddChild(vfxNode); } catch { return; }
            }
            try { parent.MoveChild(vfxNode, creature.GetIndex()); } catch { }

            var playMethod = HarmonyLib.AccessTools.Method(vfxType, "PlayVfx");
            if (playMethod == null) return;

            // Fire-and-forget — invoke PlayVfx, do NOT await. The vfx already
            // reparents the body into its subviewport in the synchronous
            // portion of the method, so by the time we return the body is
            // independent of NCreature and dissolves on its own timeline.
            try { playMethod.Invoke(vfx, null); } catch { }
        }
        catch (Exception ex)
        {
            UndoLogger.Warn($"[AnimDie] death fade vfx (fire-and-forget) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Set spine track 0 to 'die' (non-looping) and return the animation's
    /// real duration in seconds. 0 means we couldn't query it — caller falls
    /// back to <see cref="DieVisibleFallbackSeconds"/>.
    /// </summary>

    private static float TryPlaySpineDie(NCreature creature)
    {
        try
        {
            var visualsType = Snapshot.ReflectionCache.NCreatureVisualsType;
            if (visualsType == null) return 0f;
            var visuals = creature.Visuals;
            if (visuals == null) return 0f;
            var spineProp = Snapshot.ReflectionCache.NCVSpineAnimationProp;
            var spine = spineProp?.GetValue(visuals);
            if (spine == null) return 0f;
            var setAnim = Snapshot.ReflectionCache.SpineSetAnimationMethod;
            // SetAnimation(name, loop, track) — non-loop die. Returns the track entry.
            var trackEntry = setAnim?.Invoke(spine, new object[] { "die", false, 0 });
            if (trackEntry == null) return 0f;

            // Read the actual duration via MegaTrackEntry.GetAnimation().GetDuration().
            var anim = Snapshot.ReflectionCache.TrackGetAnimationMethod?.Invoke(trackEntry, null);
            if (anim == null) return 0f;
            var durObj = Snapshot.ReflectionCache.AnimationGetDurationMethod?.Invoke(anim, null);
            if (durObj is float f && f > 0f) return f;
            return 0f;
        }
        catch (Exception ex)
        {
            UndoLogger.Warn($"[AnimDie] spine die track failed: {ex.Message}");
            return 0f;
        }
    }
}
