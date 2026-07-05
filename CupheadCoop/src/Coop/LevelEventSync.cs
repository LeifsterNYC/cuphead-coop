using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v1.2.0 wave 2 (design section C) — client-side consumers of the level-lifecycle stream that
    /// the host now latches (see <see cref="HostLevelFlags"/> and the streamed IsDead HP mirror).
    /// None of these fire naturally on the client because the game events they hang off — the death
    /// cascade, the win cinematic, a same-name level reload — all run host-side only. This drives the
    /// client's stock UI from the host's authoritative state WITHOUT ever taking the save-writing
    /// paths (guarded by <see cref="ClientSaveGuard"/>).
    ///
    /// Three one-shot-per-level behaviors, gated by <c>[Sync] EnableLevelEvents</c>:
    ///   1. Both-dead game-over: reflection-set <c>Level.playerIsDead = true</c> so the game's own
    ///      Level.Update runs _OnLose → LevelEnd.Lose → the stock retry card. (Level.Update also
    ///      requires both local players' IsDead within a 5-frame window; the HP mirror pushing 0 via
    ///      SetHealth makes IsDead true, so we only flip the field once both are actually dead.)
    ///   2. Win: on the RemoteLevelWon latch's rising edge, run the cosmetic LevelKOAnimation only —
    ///      never LevelEnd.Win / Level.zHack_OnWin (those write saves). SceneSync follows the host
    ///      into scene_win for the scoreboard.
    ///   3. Level reload: consume the LevelReload pulse → SceneLoader.ReloadLevel(). A same-name
    ///      reload is invisible to SceneSync's name diff, so the two cooperate cleanly (see below).
    /// </summary>
    internal static class LevelEventSync
    {
        public static ManualLogSource Log;

        // Hysteresis on the streamed both-dead condition so a mid-fight revive blip (one player at 0
        // HP for a frame during a parry-revive) can't trip the game-over card.
        private const float BothDeadHoldSec = 0.5f;

        private static string _lastScene = "";
        private static bool _gameOverTriggered; // one-shot per level load
        private static bool _wonHandled;        // one-shot per level load
        private static float _bothDeadSince = -1f;

        private static FieldInfo _playerIsDeadField;

        // Diagnostics (item 6): current both-dead read, for the LEVELSTATE dump line.
        public static bool BothDeadNow { get; private set; }

        public static void Tick()
        {
            if (CoopState.Mode != CoopMode.Client) return;
            if (!ModConfig.EnableLevelEvents.Value) return;

            string scene;
            try { scene = SceneManager.GetActiveScene().name ?? ""; }
            catch { return; }

            // New scene → reset the per-level one-shots. Covers both a normal level change and a
            // reload landing us back in a fresh Level instance.
            if (scene != _lastScene)
            {
                _lastScene = scene;
                _gameOverTriggered = false;
                _wonHandled = false;
                _bothDeadSince = -1f;
                BothDeadNow = false;
            }

            // ---- 3. Level reload (host pressed Retry) ----
            // Consume the one-shot pulse. Only act inside a level scene — the pulse is meaningless on
            // a menu. Interaction with SceneSync: a retry is a SAME-NAME reload, so SceneSync's
            // name-diff (local==remote) never fires for it; this pulse is the only trigger. Conversely
            // a host scene CHANGE (win screen, world map, quit-to-menu) shows a different name and is
            // handled solely by SceneSync. The two are disjoint by construction — no double reload.
            if (CoopState.ConsumeLevelReload())
            {
                if (scene.StartsWith("scene_level"))
                {
                    Log?.LogInfo("LevelEventSync: host reloaded the level — client SceneLoader.ReloadLevel()");
                    try { global::SceneLoader.ReloadLevel(); }
                    catch (System.Exception ex) { Log?.LogWarning("LevelEventSync: ReloadLevel failed: " + ex.Message); }
                    return; // scene is tearing down; skip the rest this frame
                }
            }

            if (!scene.StartsWith("scene_level")) return;

            // ---- 2. Win / knockout banner ----
            if (CoopState.RemoteLevelWon)
            {
                if (!_wonHandled)
                {
                    _wonHandled = true;
                    PlayWinKO();
                }
                return; // won takes precedence over the game-over path
            }

            // ---- 1. Both-dead game-over ----
            bool bothStreamedDead = CoopState.RemoteP1IsDead && CoopState.RemoteP2IsDead;
            BothDeadNow = bothStreamedDead || CoopState.RemoteLevelLost;
            if (_gameOverTriggered) return;

            // Authoritative path: the host latched LevelEnd.Lose. This is the signal that actually
            // fires in practice — the per-player IsDead stream almost never observes a death (the
            // controller is removed the same frame IsDead flips, between two 30 Hz samples). Force
            // both LOCAL players dead so Level.Update's players[].IsDead gate passes, then flip the
            // field. Verified live: first real fight ended host-side with zero IsDead=true snapshots.
            if (CoopState.RemoteLevelLost)
            {
                ForceLocalDeaths();
                TriggerGameOver();
                return;
            }

            // Heuristic fallback: both streamed IsDead held long enough (only reachable when a
            // snapshot did catch the death window — rare but free to keep).
            if (bothStreamedDead)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (_bothDeadSince < 0f) _bothDeadSince = now;
                else if (now - _bothDeadSince > BothDeadHoldSec && BothLocalDead())
                    TriggerGameOver();
            }
            else
            {
                _bothDeadSince = -1f;
            }
        }

        // Drive both local players' HP to 0 through the game's own SetHealth (fires
        // OnHealthChangedEvent so the HUD empties too). Makes AbstractPlayerController.IsDead true,
        // which Level.Update requires within 5 frames of playerIsDead flipping.
        private static void ForceLocalDeaths()
        {
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var id = i == 0 ? global::PlayerId.PlayerOne : global::PlayerId.PlayerTwo;
                    if (!global::PlayerManager.DoesPlayerExist(id)) continue;
                    var p = global::PlayerManager.GetPlayer(id);
                    if (p != null && p.stats != null && p.stats.Health > 0)
                        p.stats.SetHealth(0);
                }
            }
            catch
            {
                // Mid-transition raciness — TriggerGameOver's field flip still runs; Level.Update
                // just won't fire _OnLose until the players report dead, which the HP mirror
                // converges to anyway.
            }
        }

        // Both LOCAL controllers must report IsDead, because Level.Update gates _OnLose on
        // players[0].IsDead && players[1].IsDead within a 5-frame window after playerIsDead flips.
        // The HP mirror (SetHealth(0)) drives IsDead, so waiting for it guarantees the card appears.
        private static bool BothLocalDead()
        {
            try
            {
                if (!global::PlayerManager.DoesPlayerExist(global::PlayerId.PlayerOne)) return false;
                if (!global::PlayerManager.DoesPlayerExist(global::PlayerId.PlayerTwo)) return false;
                var p1 = global::PlayerManager.GetPlayer(global::PlayerId.PlayerOne);
                var p2 = global::PlayerManager.GetPlayer(global::PlayerId.PlayerTwo);
                return p1 != null && p2 != null && p1.IsDead && p2.IsDead;
            }
            catch { return false; }
        }

        private static void TriggerGameOver()
        {
            try
            {
                var lvl = global::Level.Current;
                if (lvl == null) return;
                if (_playerIsDeadField == null)
                {
                    _playerIsDeadField = typeof(global::Level).GetField("playerIsDead",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_playerIsDeadField == null)
                    {
                        Log?.LogWarning("LevelEventSync: Level.playerIsDead field not found — game-over disabled");
                        _gameOverTriggered = true; // don't retry-spin
                        return;
                    }
                }

                // Setting the field is all it takes: Level.Update sees playerIsDead && both IsDead and
                // runs the stock lose flow. ClientSaveGuard blocks the PlayerData.SaveCurrentFile that
                // _OnLose would otherwise write.
                _playerIsDeadField.SetValue(lvl, true);
                _gameOverTriggered = true;
                Log?.LogInfo("LevelEventSync: both players dead — set Level.playerIsDead to run the stock game-over card");
            }
            catch (System.Exception ex)
            {
                Log?.LogWarning("LevelEventSync: TriggerGameOver failed: " + ex.Message);
                _gameOverTriggered = true;
            }
        }

        private static void PlayWinKO()
        {
            try
            {
                if (global::Level.Current == null) return;
                // Cosmetic only — mirrors the KO portion of LevelEnd.win_cr without any of its
                // save-writing / scene-transition tail. The knockout SFX (bell/announcer) arrive via
                // AudioSync from the host; SceneSync follows the host into scene_win for the scoreboard.
                var ko = global::LevelKOAnimation.Create(false);
                if (ko != null)
                {
                    ko.StartCoroutine(ko.anim_cr());
                    Log?.LogInfo("LevelEventSync: host won — playing cosmetic LevelKOAnimation on client");
                }
            }
            catch (System.Exception ex)
            {
                Log?.LogWarning("LevelEventSync: PlayWinKO failed: " + ex.Message);
            }
        }

        public static void Reset()
        {
            _lastScene = "";
            _gameOverTriggered = false;
            _wonHandled = false;
            _bothDeadSince = -1f;
            BothDeadNow = false;
            ClientSaveGuard.ResetLog();
        }
    }

    /// <summary>
    /// CRITICAL SAFETY. On the client, the co-op session is host-authoritative and the client must
    /// NEVER persist a save — its progress is a spectator view of the host's run. The stock game-over
    /// flow the client triggers (<see cref="LevelEventSync"/>) reaches <c>Level._OnLose</c>, which
    /// calls <c>PlayerData.SaveCurrentFile()</c>. This prefix skips every SaveCurrentFile while
    /// Mode == Client, enforcing the invariant regardless of which code path reached it. When the
    /// client disconnects (Mode → Off) saves behave normally again.
    /// </summary>
    [HarmonyPatch(typeof(global::PlayerData), nameof(global::PlayerData.SaveCurrentFile))]
    internal static class ClientSaveGuard
    {
        // Level.Update can drive _OnLose (and thus SaveCurrentFile) on several frames in a row, so
        // throttle the log to once per client session to avoid flooding.
        private static bool _loggedOnce;

        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (CoopState.Mode != CoopMode.Client) return true;
            if (!_loggedOnce)
            {
                _loggedOnce = true;
                LevelEventSync.Log?.LogInfo("ClientSaveGuard: blocking PlayerData.SaveCurrentFile on client (co-op is host-authoritative; logged once)");
            }
            return false;
        }

        // Re-arm the one-shot log for the next session.
        internal static void ResetLog() { _loggedOnce = false; }
    }
}
