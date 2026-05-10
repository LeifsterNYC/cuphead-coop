using System;
using BepInEx.Logging;
using UnityEngine.SceneManagement;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// M11: scene-name sync. Host captures its active scene each tick; client force-loads
    /// the same scene when it sees a mismatch. Without this, host and client navigate menus
    /// independently and never end up in the same level until manually coordinated.
    ///
    /// Loud-but-correct UX: when the client connects mid-menu, the client's Cuphead jumps
    /// straight to wherever the host is. Acceptable since the client is meant to be a
    /// spectator-with-input, not an independent player.
    ///
    /// Skips transition scenes like <c>scene_load_helper</c> — they're momentary and will
    /// change again before the LoadScene completes anyway.
    /// </summary>
    internal static class SceneSync
    {
        public static ManualLogSource Log;

        // Host's last sampled scene; goes into StateSnapshot.
        public static string LocalSceneName = "";

        // Names of Cuphead's transition / loading scenes that we deliberately don't follow.
        // Following them would cause flicker because they change again in a frame or two.
        private static readonly string[] _skipScenes =
        {
            "scene_load_helper",
        };

        public static void HostCapture()
        {
            try
            {
                LocalSceneName = SceneManager.GetActiveScene().name ?? "";
            }
            catch
            {
                LocalSceneName = "";
            }
        }

        public static void ApplyFromHost(string remoteScene)
        {
            if (string.IsNullOrEmpty(remoteScene)) return;
            string local;
            try { local = SceneManager.GetActiveScene().name; }
            catch { return; }
            if (local == remoteScene) return;

            for (int i = 0; i < _skipScenes.Length; i++)
                if (remoteScene == _skipScenes[i]) return;

            // Avoid spamming LoadScene if we're ALREADY in a transition scene — that means
            // a load is in progress and our request would queue up.
            for (int i = 0; i < _skipScenes.Length; i++)
                if (local == _skipScenes[i]) return;

            Log?.LogInfo("SceneSync: host on '" + remoteScene + "', client on '" + local + "' — loading host's scene");
            try
            {
                SceneManager.LoadScene(remoteScene);
            }
            catch (Exception ex)
            {
                Log?.LogWarning("SceneSync.LoadScene('" + remoteScene + "') failed: " + ex.Message);
            }
        }
    }
}
