using System;
using System.Reflection;
using BepInEx.Logging;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// M12: when a client connects, force-join P2 on the host even if no local controller is
    /// pressing buttons to trigger the normal join flow. Justin's keyboard is on his Mac —
    /// host has no way to know "P2 wants to join" without us synthesizing it.
    ///
    /// Approach: reflect into <see cref="PlayerManager"/>'s private state and apply the same
    /// transitions Cuphead's own Update would apply when a controller press is detected:
    ///  - <c>playerSlots[1].canJoin = true</c>
    ///  - <c>playerSlots[1].joinState = JoinState.Joined</c>
    ///  - <c>playerSlots[1].controllerState = ControllerState.NoController</c>
    ///  - <c>PlayerManager.Multiplayer = true</c>
    ///  - Fire <c>OnPlayerJoinedEvent(PlayerId.PlayerTwo)</c> via the backing delegate field
    ///    so level-end / level-load / spawn subscribers run their handlers.
    ///
    /// Idempotent: skips if P2 is already Joined. Triggered by <see cref="Trigger"/>; deferred
    /// to next <see cref="Plugin.Update"/> tick so reflection runs on the main thread regardless
    /// of where the network event fired.
    /// </summary>
    internal static class P2AutoJoin
    {
        public static ManualLogSource Log;
        private static volatile bool _pending;

        // Cached reflection handles. Resolved on first use, kept until plugin teardown.
        private static FieldInfo _slotsField;
        private static FieldInfo _multiplayerField;
        private static FieldInfo _eventField;
        private static FieldInfo _slotJoinStateField;
        private static FieldInfo _slotCanJoinField;
        private static FieldInfo _slotControllerStateField;
        private static object _joinStateJoinedValue;
        private static object _controllerStateNoController;
        private static bool _reflectionResolved;

        public static void Trigger() => _pending = true;

        public static void TickIfPending()
        {
            if (!_pending) return;
            if (!ModConfig.EnableAutoP2Join.Value) { _pending = false; return; }
            _pending = false;

            bool ok = true;
            // v1.2.0 HUD race fix: on the CLIENT, force-join P1 as well. The client user may still
            // be sitting on the title screen when the session establishes; if neither slot is Joined
            // when SceneSync drags them into the host's level, Level.Start → LevelHUD.LevelInit runs
            // with no players and dies, leaving the HUD permanently absent. Joining both slots here
            // — at session establish, before any level load — makes LevelInit see the same world it
            // would in real local co-op. Host path unchanged: its P1 is the human who started the game.
            if (CoopState.Mode == CoopMode.Client)
                ok &= ForceJoin(0, global::PlayerId.PlayerOne);
            ok &= ForceJoin(1, global::PlayerId.PlayerTwo);
            if (!ok) _pending = true; // retry next tick
        }

        /// <summary>
        /// Force a player slot to Joined and fire OnPlayerJoinedEvent, exactly as Cuphead's own
        /// join flow would. Idempotent. Returns false if PlayerManager state isn't ready yet
        /// (caller may retry). Also used by TestHarness to join P1 when no human pressed start.
        /// </summary>
        internal static bool ForceJoin(int slotIndex, global::PlayerId id)
        {
            try
            {
                if (!ResolveReflection()) return false;

                var slots = (Array)_slotsField.GetValue(null);
                if (slots == null || slots.Length <= slotIndex)
                {
                    Log?.LogWarning("P2AutoJoin: playerSlots array not initialized yet — will retry");
                    return false;
                }

                var slot = slots.GetValue(slotIndex);
                if (slot == null)
                {
                    Log?.LogWarning("P2AutoJoin: playerSlots[" + slotIndex + "] is null — will retry");
                    return false;
                }

                var currentJoinState = _slotJoinStateField.GetValue(slot);
                if (Equals(currentJoinState, _joinStateJoinedValue))
                {
                    Log?.LogInfo("P2AutoJoin: " + id + " already Joined; nothing to do");
                    return true;
                }

                _slotCanJoinField.SetValue(slot, true);
                _slotJoinStateField.SetValue(slot, _joinStateJoinedValue);
                _slotControllerStateField.SetValue(slot, _controllerStateNoController);
                _multiplayerField.SetValue(null, true);

                FireOnPlayerJoinedEvent(id);

                Log?.LogInfo("P2AutoJoin: forced " + id + " to Joined state and fired OnPlayerJoinedEvent");
                return true;
            }
            catch (Exception ex)
            {
                Log?.LogError("P2AutoJoin failed: " + ex.GetType().Name + ": " + ex.Message);
                return true; // don't retry-loop on a hard failure
            }
        }

        private static bool ResolveReflection()
        {
            if (_reflectionResolved) return true;

            var pmType = typeof(global::PlayerManager);
            _slotsField = pmType.GetField("playerSlots", BindingFlags.NonPublic | BindingFlags.Static);
            _multiplayerField = pmType.GetField("Multiplayer", BindingFlags.Public | BindingFlags.Static);
            // Public events have a private backing delegate field with the same name.
            _eventField = pmType.GetField("OnPlayerJoinedEvent", BindingFlags.NonPublic | BindingFlags.Static);

            if (_slotsField == null) { Log?.LogError("P2AutoJoin: PlayerManager.playerSlots field not found"); return false; }
            if (_multiplayerField == null) { Log?.LogError("P2AutoJoin: PlayerManager.Multiplayer field not found"); return false; }

            // Slot type is a private nested type. Probe it via an actual instance to find the
            // fields by name without needing to know the full nested-type path.
            var slots = (Array)_slotsField.GetValue(null);
            if (slots == null || slots.Length == 0)
            {
                // PlayerSlot[] is initialized in the static ctor with `new PlayerSlot[2] { new(), new() }`
                // so this should be non-null/non-empty by the time anything runs. Bail and retry.
                return false;
            }

            var slotType = slots.GetValue(0).GetType();
            _slotJoinStateField = slotType.GetField("joinState", BindingFlags.Public | BindingFlags.Instance);
            _slotCanJoinField = slotType.GetField("canJoin", BindingFlags.Public | BindingFlags.Instance);
            _slotControllerStateField = slotType.GetField("controllerState", BindingFlags.Public | BindingFlags.Instance);

            if (_slotJoinStateField == null || _slotCanJoinField == null || _slotControllerStateField == null)
            {
                Log?.LogError("P2AutoJoin: PlayerSlot fields not found (joinState/canJoin/controllerState)");
                return false;
            }

            _joinStateJoinedValue = Enum.Parse(_slotJoinStateField.FieldType, "Joined");
            _controllerStateNoController = Enum.Parse(_slotControllerStateField.FieldType, "NoController");

            _reflectionResolved = true;
            return true;
        }

        private static void FireOnPlayerJoinedEvent(global::PlayerId id)
        {
            if (_eventField == null) return;
            var del = _eventField.GetValue(null) as MulticastDelegate;
            if (del == null) { Log?.LogInfo("P2AutoJoin: OnPlayerJoinedEvent has no subscribers"); return; }
            try
            {
                del.DynamicInvoke(id);
            }
            catch (Exception ex)
            {
                // A subscriber threw — log but continue, since some subscribers may have already run.
                Log?.LogWarning("P2AutoJoin: OnPlayerJoinedEvent subscriber threw: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// v1.2.0 HUD fallback, the second half of the join-ordering fix above. If a level scene
    /// managed to load before the force-join landed, Level.Start's one call to
    /// <c>LevelHUD.LevelInit()</c> ran against an empty PlayerManager and died, and nothing in the
    /// game ever retries it — the HUD stays blank for the whole fight. Detection: LevelInit assigns
    /// the private, non-serialized <c>levelHudTemplate</c> as its very first act, so
    /// "<c>LevelHUD.Current</c> exists but its template is null" is a reliable init-never-completed
    /// probe. Once both player controllers exist, we invoke the (public) LevelInit ourselves — one
    /// shot per LevelHUD instance so a genuine mid-init state is never double-driven.
    /// </summary>
    internal static class HudFixup
    {
        public static ManualLogSource Log;

        private static int _handledHudId;
        private static FieldInfo _templateField;

        public static void Tick()
        {
            if (CoopState.Mode != CoopMode.Client) return;
            try
            {
                var hud = global::LevelHUD.Current;
                if (hud == null) return;
                int hudId = hud.GetInstanceID();
                if (hudId == _handledHudId) return;

                if (_templateField == null)
                {
                    _templateField = typeof(global::LevelHUD).GetField("levelHudTemplate",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_templateField == null)
                    {
                        Log?.LogWarning("HudFixup: LevelHUD.levelHudTemplate field not found — fallback disabled");
                        _handledHudId = hudId;
                        return;
                    }
                }

                if (_templateField.GetValue(hud) != null)
                {
                    // LevelInit already ran (the normal case) — nothing to fix for this HUD.
                    _handledHudId = hudId;
                    return;
                }

                // Template null: init never completed. Wait until both controllers exist so the
                // re-invoke can't hit the same NRE that killed the original call.
                if (!global::PlayerManager.DoesPlayerExist(global::PlayerId.PlayerOne)) return;
                if (!global::PlayerManager.DoesPlayerExist(global::PlayerId.PlayerTwo)) return;
                if (global::PlayerManager.GetPlayer(global::PlayerId.PlayerOne) == null) return;
                if (global::PlayerManager.GetPlayer(global::PlayerId.PlayerTwo) == null) return;

                _handledHudId = hudId; // one shot, even if LevelInit throws below
                hud.LevelInit();
                Log?.LogInfo("HudFixup: re-invoked LevelHUD.LevelInit (initial call raced the force-join)");
            }
            catch
            {
                // Scene-transition raciness — retry next frame (or never, if _handledHudId latched).
            }
        }
    }
}
