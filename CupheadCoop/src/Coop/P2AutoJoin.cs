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
            _pending = false;

            try
            {
                if (!ResolveReflection()) return;

                var slots = (Array)_slotsField.GetValue(null);
                if (slots == null || slots.Length < 2)
                {
                    Log?.LogWarning("P2AutoJoin: playerSlots array not initialized yet — will retry");
                    _pending = true;
                    return;
                }

                var p2Slot = slots.GetValue(1);
                if (p2Slot == null)
                {
                    Log?.LogWarning("P2AutoJoin: playerSlots[1] is null — will retry");
                    _pending = true;
                    return;
                }

                var currentJoinState = _slotJoinStateField.GetValue(p2Slot);
                if (Equals(currentJoinState, _joinStateJoinedValue))
                {
                    Log?.LogInfo("P2AutoJoin: P2 already Joined; nothing to do");
                    return;
                }

                _slotCanJoinField.SetValue(p2Slot, true);
                _slotJoinStateField.SetValue(p2Slot, _joinStateJoinedValue);
                _slotControllerStateField.SetValue(p2Slot, _controllerStateNoController);
                _multiplayerField.SetValue(null, true);

                FireOnPlayerJoinedEvent();

                Log?.LogInfo("P2AutoJoin: forced P2 to Joined state and fired OnPlayerJoinedEvent");
            }
            catch (Exception ex)
            {
                Log?.LogError("P2AutoJoin failed: " + ex.GetType().Name + ": " + ex.Message);
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

        private static void FireOnPlayerJoinedEvent()
        {
            if (_eventField == null) return;
            var del = _eventField.GetValue(null) as MulticastDelegate;
            if (del == null) { Log?.LogInfo("P2AutoJoin: OnPlayerJoinedEvent has no subscribers"); return; }
            try
            {
                del.DynamicInvoke(global::PlayerId.PlayerTwo);
            }
            catch (Exception ex)
            {
                // A subscriber threw — log but continue, since some subscribers may have already run.
                Log?.LogWarning("P2AutoJoin: OnPlayerJoinedEvent subscriber threw: " + ex.GetType().Name);
            }
        }
    }
}
