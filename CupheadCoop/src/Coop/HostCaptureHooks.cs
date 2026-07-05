using HarmonyLib;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v13 host-side edge accumulators. Player animation on the client is largely poll-driven from
    /// the streamed <see cref="Net.PlayerSnapshot.Flags"/>, but two things are edge-driven on the
    /// real controller and cannot be reconstructed from a poll: the muzzle-flash trigger
    /// (<c>OnWeaponFire</c>) and the hit reaction (<c>OnDamageTaken</c>). We latch those edges here
    /// as they happen on the host, then <see cref="Net.CoopHost.TickStateSnapshot"/> drains them into
    /// the NEXT outgoing snapshot's <see cref="Net.PlayerSnapshot.Pulses"/> byte and clears them.
    ///
    /// Draining happens at the snapshot send rate (30 Hz), NOT per frame — so accumulating here
    /// (rather than in the per-frame HostCapture) is what stops a fire/hit that lands between two
    /// sends from being dropped.
    /// </summary>
    internal static class HostPlayerPulses
    {
        private static byte _p1;
        private static byte _p2;

        public static void Fire(global::PlayerId id, byte bit)
        {
            if (CoopState.Mode != CoopMode.Host) return;
            if (id == global::PlayerId.PlayerOne) _p1 |= bit;
            else if (id == global::PlayerId.PlayerTwo) _p2 |= bit;
        }

        /// <summary>Read and clear P1's accumulated pulses. Called once per outgoing snapshot.</summary>
        public static byte ConsumeP1() { byte v = _p1; _p1 = 0; return v; }
        public static byte ConsumeP2() { byte v = _p2; _p2 = 0; return v; }

        // DamageTaken cannot be a Harmony postfix on PlayerDamageReceiver.TakeDamage: that method
        // early-returns during i-frames / SuperInvincible / CanTakeDamage=false, so a postfix would
        // latch false hits every frame an enemy overlaps an invulnerable cup. The accurate edge is
        // the base DamageReceiver.OnDamageTaken event, which only fires after those guards pass and
        // the damage actually commits — the same event the game's own animation controller keys its
        // hit reaction off. We subscribe per player receiver, lazily from the host's per-frame flag
        // sample (ScenePuppetry.SampleFlags), re-subscribing when a scene load creates a new receiver.
        private static global::PlayerDamageReceiver _hookedP1;
        private static global::PlayerDamageReceiver _hookedP2;
        private static global::DamageReceiver.OnDamageTakenHandler _onP1Damage;
        private static global::DamageReceiver.OnDamageTakenHandler _onP2Damage;

        public static void EnsureDamageHook(global::PlayerId id, global::PlayerDamageReceiver dr)
        {
            if (dr == null) return;
            if (id == global::PlayerId.PlayerOne)
            {
                if (ReferenceEquals(dr, _hookedP1)) return;
                if (_onP1Damage == null)
                    _onP1Damage = delegate { Fire(global::PlayerId.PlayerOne, CoopState.PulseDamageTaken); };
                dr.OnDamageTaken += _onP1Damage;
                _hookedP1 = dr;
            }
            else if (id == global::PlayerId.PlayerTwo)
            {
                if (ReferenceEquals(dr, _hookedP2)) return;
                if (_onP2Damage == null)
                    _onP2Damage = delegate { Fire(global::PlayerId.PlayerTwo, CoopState.PulseDamageTaken); };
                dr.OnDamageTaken += _onP2Damage;
                _hookedP2 = dr;
            }
        }

        public static void Reset()
        {
            _p1 = 0;
            _p2 = 0;
            // Detach from any still-alive receivers so a later re-host doesn't double-subscribe
            // (harmless — Fire ORs the same bit — but tidy). Event add/remove is pure CLR, safe
            // even if Unity has destroyed the underlying GameObject.
            try
            {
                if (_hookedP1 != null && _onP1Damage != null) _hookedP1.OnDamageTaken -= _onP1Damage;
                if (_hookedP2 != null && _onP2Damage != null) _hookedP2.OnDamageTaken -= _onP2Damage;
            }
            catch
            {
                // Teardown raciness — refs are dropped below regardless.
            }
            _hookedP1 = null;
            _hookedP2 = null;
        }
    }

    /// <summary>
    /// v13 host-side level-lifecycle latch. LevelWon is set true when the host enters the win
    /// cinematic (<c>LevelEnd.Win</c>) and held until the scene changes, so the client can play its
    /// own cosmetic KO animation (wave 2) without ever calling the save-writing win path itself.
    /// LevelReload is a one-shot pulse set when the host calls <c>SceneLoader.ReloadLevel()</c> — a
    /// same-name reload that <see cref="SceneSync"/>'s name-diff cannot detect.
    /// </summary>
    internal static class HostLevelFlags
    {
        private static bool _won;
        private static bool _lost;
        private static bool _reloadPulse;
        private static string _lastScene = "";

        public static void OnLevelWin()
        {
            if (CoopState.Mode != CoopMode.Host) return;
            _won = true;
        }

        // Latched from LevelEnd.Lose. This is the ONLY reliable both-dead signal for the client:
        // the per-player IsDead stream is racy because Cuphead removes a dead player's controller
        // the same frame IsDead flips, so a 30 Hz snapshot usually never observes it true.
        public static void OnLevelLose()
        {
            if (CoopState.Mode != CoopMode.Host) return;
            _lost = true;
        }

        public static void OnReload()
        {
            if (CoopState.Mode != CoopMode.Host) return;
            _reloadPulse = true;
            _won = false;  // replaying — the previous outcome no longer applies
            _lost = false;
        }

        /// <summary>Build the LevelFlags byte for the outgoing snapshot and clear the one-shot pulse.
        /// Also drops the outcome latches on a scene change so they don't bleed into the next level.</summary>
        public static byte BuildAndClear()
        {
            string scene = SceneSync.LocalSceneName ?? "";
            if (scene != _lastScene)
            {
                _lastScene = scene;
                _won = false;
                _lost = false;
            }
            byte f = 0;
            if (_won) f |= CoopState.LevelFlagWon;
            if (_lost) f |= CoopState.LevelFlagLost;
            if (_reloadPulse) f |= CoopState.LevelFlagReload;
            _reloadPulse = false;
            return f;
        }

        /// <summary>Read the current win latch — for the LEVELSTATE diagnostic line only.</summary>
        public static bool Won { get { return _won; } }

        /// <summary>Read the current lost latch — consumed by <see cref="HostLoseWatchdog"/> to stop re-arming.</summary>
        public static bool Lost { get { return _lost; } }

        public static void Reset() { _won = false; _lost = false; _reloadPulse = false; _lastScene = ""; }
    }

    /// <summary>
    /// Host-side game-over watchdog. Cuphead's own lose gate is fragile in co-op: Level.Update only
    /// checks "both players IsDead" for 4 frames after a DeathEvent re-arms <c>playerIsDead</c>, then
    /// self-clears — and the death sequence pauses/disables the Level component itself, which can
    /// freeze <c>playerDeathDelayFrames</c> past its window so the gate early-returns forever even
    /// after the component is re-enabled (verified live: gate inputs all true, component re-enabled,
    /// flag never consumed). With the out-of-frame ghost-revive mechanic cycling deaths (die → ghost
    /// floats off-screen → auto-revive steals partner HP → die again), both players end up dead with
    /// no further DeathEvent and the game never shows its game-over card. Rather than fight the gate,
    /// when hosting: if both players stop existing for a sustained window and the level isn't won,
    /// invoke <c>Level._OnLose()</c> directly (one-shot). That runs the exact stock lose flow —
    /// _OnLevelEnd → OnLose → LevelEnd.Lose (→ our Lost latch, which the client mirrors) → save.
    /// </summary>
    internal static class HostLoseWatchdog
    {
        private const float BothGoneArmSec = 2f;

        private static float _bothGoneSecs;
        private static string _lastScene = "";
        private static bool _loseInvoked;
        private static System.Reflection.MethodInfo _onLoseMethod;

        public static void Tick(float dt)
        {
            if (CoopState.Mode != CoopMode.Host) return;

            string scene = SceneSync.LocalSceneName ?? "";
            if (scene != _lastScene)
            {
                _lastScene = scene;
                _bothGoneSecs = 0f;
                _loseInvoked = false;
            }
            if (_loseInvoked) return;
            if (!scene.StartsWith("scene_level")) return;
            if (HostLevelFlags.Won || HostLevelFlags.Lost) return; // outcome already reached

            bool bothGone;
            try
            {
                bothGone = !global::PlayerManager.DoesPlayerExist(global::PlayerId.PlayerOne)
                        && !global::PlayerManager.DoesPlayerExist(global::PlayerId.PlayerTwo);
            }
            catch { return; }

            if (!bothGone) { _bothGoneSecs = 0f; return; }

            _bothGoneSecs += dt;
            if (_bothGoneSecs < BothGoneArmSec) return;

            try
            {
                var lvl = global::Level.Current;
                if (lvl == null) return;
                if (_onLoseMethod == null)
                {
                    _onLoseMethod = typeof(global::Level).GetMethod("_OnLose",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (_onLoseMethod == null)
                    {
                        Plugin.LogStatic?.LogWarning("HostLoseWatchdog: Level._OnLose not found");
                        _loseInvoked = true; // don't spam the lookup every frame
                        return;
                    }
                }
                // The death sequence can leave PauseManager paused and the Level component disabled;
                // clear both so the game-over UI sequence the lose flow kicks off can actually run.
                if (global::PauseManager.state == global::PauseManager.State.Paused)
                    global::PauseManager.Unpause();
                if (!lvl.enabled) lvl.enabled = true;

                _loseInvoked = true; // set BEFORE the call — if _OnLose throws mid-way, never re-run it
                Plugin.LogStatic?.LogInfo("HostLoseWatchdog: both players gone " +
                                          BothGoneArmSec + "s with no stock game-over — invoking Level._OnLose()");
                DumpGateInputs(lvl);
                // Minutes after a death the dead players' components are half-torn-down, and a
                // single throwing event subscriber aborts the whole multicast chain inside _OnLose
                // before LevelEnd.Lose ever runs (observed live: LevelPlayerWeaponManager.OnLevelEnd
                // → WeaponPrefabs.GetWeapon → KeyNotFoundException). Rewrap the level-end/lose
                // events so each subscriber runs in its own try/catch, then run the stock flow.
                WrapEventSafe(lvl, "OnLevelEndEvent");
                WrapEventSafe(lvl, "OnPreLoseEvent");
                WrapEventSafe(lvl, "OnLoseEvent");
                _onLoseMethod.Invoke(lvl, null);
            }
            catch (System.Exception ex)
            {
                Plugin.LogStatic?.LogWarning("HostLoseWatchdog: _OnLose invoke failed: " + ex);
            }
        }

        public static void Reset() { _bothGoneSecs = 0f; _lastScene = ""; _loseInvoked = false; }

        // Replace a Level Action-event's backing field with a wrapper that invokes each original
        // subscriber in its own try/catch, so one half-torn-down subscriber can't abort the rest
        // of the stock lose flow. Only ever applied on the one-shot watchdog path.
        private static void WrapEventSafe(global::Level lvl, string eventField)
        {
            try
            {
                var f = typeof(global::Level).GetField(eventField,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var del = f != null ? f.GetValue(lvl) as System.Delegate : null;
                if (del == null) return;
                var subs = del.GetInvocationList();
                string name = eventField;
                System.Action safe = delegate
                {
                    for (int i = 0; i < subs.Length; i++)
                    {
                        try { ((System.Action)subs[i])(); }
                        catch (System.Exception e)
                        {
                            Plugin.LogStatic?.LogWarning("HostLoseWatchdog: " + name +
                                                         " subscriber threw " + e.GetType().Name + " — skipped");
                        }
                    }
                };
                f.SetValue(lvl, safe);
            }
            catch (System.Exception ex)
            {
                Plugin.LogStatic?.LogWarning("HostLoseWatchdog: WrapEventSafe(" + eventField + ") failed: " + ex.GetType().Name);
            }
        }

        // One-shot diagnostic: log exactly what Level.Update's lose gate will read, so a failure to
        // fire is attributable (null slot vs _isReviving stuck vs HP nonzero vs Multiplayer false).
        private static void DumpGateInputs(global::Level lvl)
        {
            try
            {
                var playersField = typeof(global::Level).GetField("players",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?? typeof(global::Level).GetField("players",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var arr = playersField != null ? playersField.GetValue(lvl) as System.Collections.IList : null;
                string s = "HostLoseWatchdog gate: Multiplayer=" + global::PlayerManager.Multiplayer;
                if (arr == null) { s += " Level.players=<null or not found>"; }
                else
                {
                    for (int i = 0; i < arr.Count && i < 2; i++)
                    {
                        var p = arr[i] as global::AbstractPlayerController;
                        if (p == null) { s += " p" + i + "=null"; continue; }
                        int hp = -99;
                        try { if (p.stats != null) hp = p.stats.Health; } catch { }
                        s += " p" + i + "(IsDead=" + p.IsDead + " hp=" + hp + ")";
                    }
                }
                Plugin.LogStatic?.LogInfo(s);
            }
            catch (System.Exception ex)
            {
                Plugin.LogStatic?.LogInfo("HostLoseWatchdog gate dump failed: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Harmony hooks that feed the two host-side accumulators above. All are host-only (gated inside
    /// the sinks by <see cref="CoopState.Mode"/>), so on the client and in single-player they no-op.
    /// </summary>
    [HarmonyPatch]
    internal static class HostCaptureHooks
    {
        // LevelPlayerWeaponManager.TriggerWeaponFire() invokes OnWeaponFire() — the exact signal the
        // animation controller subscribes to for the muzzle flash. Postfix it to latch the edge.
        [HarmonyPatch(typeof(global::LevelPlayerWeaponManager), "TriggerWeaponFire")]
        [HarmonyPostfix]
        private static void LevelWeaponFire_Postfix(global::LevelPlayerWeaponManager __instance)
        {
            if (__instance != null && __instance.player != null)
                HostPlayerPulses.Fire(__instance.player.id, CoopState.PulseWeaponFired);
        }

        [HarmonyPatch(typeof(global::ArcadePlayerWeaponManager), "TriggerWeaponFire")]
        [HarmonyPostfix]
        private static void ArcadeWeaponFire_Postfix(global::ArcadePlayerWeaponManager __instance)
        {
            if (__instance != null && __instance.player != null)
                HostPlayerPulses.Fire(__instance.player.id, CoopState.PulseWeaponFired);
        }

        // DamageTaken is deliberately NOT a Harmony patch — see HostPlayerPulses.EnsureDamageHook
        // for why the OnDamageTaken event subscription is the accurate edge.

        // LevelEnd.Win(...) — the authoritative win cinematic entry (writes saves). Latch the win
        // flag so the client can render a cosmetic KO without touching the save path.
        [HarmonyPatch(typeof(global::LevelEnd), "Win")]
        [HarmonyPostfix]
        private static void LevelEndWin_Postfix()
        {
            HostLevelFlags.OnLevelWin();
        }

        // LevelEnd.Lose(bool, bool) — the authoritative game-over entry (shows the retry card).
        // Latched so the client can run its own stock card; without this the client can't know,
        // since the IsDead stream misses the one-frame death window (see HostLevelFlags.OnLevelLose).
        [HarmonyPatch(typeof(global::LevelEnd), "Lose")]
        [HarmonyPostfix]
        private static void LevelEndLose_Postfix()
        {
            HostLevelFlags.OnLevelLose();
        }

        [HarmonyPatch(typeof(global::SceneLoader), nameof(global::SceneLoader.ReloadLevel))]
        [HarmonyPostfix]
        private static void ReloadLevel_Postfix()
        {
            HostLevelFlags.OnReload();
        }
    }
}
