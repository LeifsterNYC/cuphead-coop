namespace CupheadCoop.Coop
{
    /// <summary>
    /// Host-authoritative pause sync. Host samples <c>PauseManager.state</c> each frame; the
    /// resulting bit goes into the <c>StateSnapshot</c>. Client compares it against its local
    /// <c>PauseManager.state</c> and calls <c>Pause()</c> / <c>Unpause()</c> to converge.
    ///
    /// Why call PauseManager directly rather than going through the pause GUI? PauseManager
    /// owns the actual freeze logic — it sets <c>CupheadTime.GlobalSpeed = 0</c>, pauses audio,
    /// and notifies registered AbstractPausableComponents. The pause MENU is a separate
    /// AbstractPauseGUI that we don't want to show on the client (the host owns the menu's
    /// "Resume" interaction). Calling Pause/Unpause directly freezes the simulation without
    /// the menu UI — exactly what a remote-controlled spectator wants.
    ///
    /// Client also needs to ignore its local pause input. Without that, hitting Pause locally
    /// would put the local game into a paused state that the host doesn't know about; the next
    /// snapshot would auto-unpause us, producing a flicker. Suppressing local pause input on
    /// the client closes that loop. Implemented via Harmony patch on PauseManager.Pause that
    /// no-ops when Mode == Client AND we haven't been triggered by an incoming snapshot.
    /// </summary>
    internal static class PauseSync
    {
        // Captured by HostCapture for the host's outgoing snapshot.
        public static bool LocalIsPaused;

        // True only while we're calling PauseManager.Pause from inside ApplyFromHost — lets the
        // suppression patch tell apart "client local input wants to pause" (block) from
        // "remote update wants to pause" (allow).
        public static bool RemoteDriven;

        public static void HostCapture()
        {
            LocalIsPaused = global::PauseManager.state == global::PauseManager.State.Paused;
        }

        public static void ApplyFromHost(bool remotePaused)
        {
            bool localPaused = global::PauseManager.state == global::PauseManager.State.Paused;
            if (localPaused == remotePaused) return;

            RemoteDriven = true;
            try
            {
                if (remotePaused) global::PauseManager.Pause();
                else global::PauseManager.Unpause();
            }
            finally
            {
                RemoteDriven = false;
            }
        }
    }
}
