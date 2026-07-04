using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// Automated-test harness. Lets an off-screen agent run two Cuphead instances on one PC,
    /// jump both into a real boss level, script player inputs, and deterministically kill
    /// Player 2 — so gameplay-sync layers can be exercised without a human driving menus.
    ///
    /// WHY this lives behind config, all defaulting OFF: every entry here is a testing crutch
    /// with no place in production. With the defaults, <see cref="Tick"/> and the input hooks
    /// are pure no-ops (a couple of bool reads) and no game method is altered — the BlockSaves
    /// Harmony patch is the one exception, and even that early-returns to the original unless
    /// its flag is set.
    ///
    /// The single most important feature is <b>BlockSaves</b>: Cuphead's save files live in
    /// <c>%AppData%\Cuphead\*.sav</c> and are SHARED by every install on this PC, including the
    /// user's real save. All progress and settings-cloud writes funnel through
    /// <c>OnlineInterfaceSteam.SaveCloudData</c> (verified: <c>PlayerData.SaveCurrentFile</c> and
    /// <c>SettingsData.SaveToCloud</c> both call it); blocking that one method stops every .sav
    /// write. (<c>SettingsData.Save</c> writes Unity PlayerPrefs — registry on Windows — never a
    /// .sav file, so it is intentionally left alone.)
    /// </summary>
    internal static class TestHarness
    {
        public static ManualLogSource Log;

        // CupheadButton action ids (verified against the CupheadButton enum ordering):
        // MoveHorizontal=0, MoveVertical=1, Jump=2, Shoot=3.
        private const int ActionMoveHorizontal = 0;
        private const int ActionJump = 2;
        private const int ActionShoot = 3;

        // AutoPlay scripted pattern shape. A monotonic clock (Time.realtimeSinceStartup) drives
        // it so it's frame-rate independent and identical logic on host and client.
        private const float MoveHalfPeriodSec = 1.5f; // +1 for 1.5s, then -1 for 1.5s, repeat
        private const float JumpPeriodSec = 2f;        // one jump pulse every 2s
        private const float JumpPulseSec = 0.1f;       // pulse held for 100ms
        private const float ClientPhaseOffsetSec = 0.7f; // shift client's pattern so the two cups differ

        private const float AutoLoadSettleSec = 3f;    // extra settle after the session looks ready

        // Host P1 scripted button bitmask, advanced once per frame from Plugin.Update. Down/Up
        // edges derive from the current-vs-previous pair, mirroring CoopState's
        // CurrentButtons/PreviousButtons pattern. Only the host's local P1 needs edge derivation;
        // the client uploads an instantaneous bitmask and the host derives edges downstream.
        private static uint _p1Cur;
        private static uint _p1Prev;
        private static float _p1AxisX;

        // One-shot latches. Once fired (or found impossible), each stays done for the session.
        private static bool _autoLoadDone;
        private static bool _initRequested;
        private static float _autoLoadSettle;
        private static bool _killDone;
        private static float _killTimer;
        private static bool _blockLoggedOnce;

        private static void OnPlayerDataInit(bool success)
        {
            Log?.LogInfo("TestHarness: PlayerData.Init completed (success=" + success + ")");
        }

        // ---------------------------------------------------------------------------------
        // A. BlockSaves — manual Harmony patch resolved by name so a signature/type mismatch on
        // a different platform build degrades to a warning instead of crashing plugin init.
        // ---------------------------------------------------------------------------------
        public static void WireBlockSaves(Harmony harmony)
        {
            try
            {
                var type = AccessTools.TypeByName("OnlineInterfaceSteam");
                if (type == null)
                {
                    Log?.LogWarning("TestHarness: OnlineInterfaceSteam type not found — BlockSaves inactive on this build");
                    return;
                }
                var method = AccessTools.Method(type, "SaveCloudData");
                if (method == null)
                {
                    Log?.LogWarning("TestHarness: OnlineInterfaceSteam.SaveCloudData not found — BlockSaves inactive on this build");
                    return;
                }
                var prefix = new HarmonyMethod(typeof(TestHarness).GetMethod(
                    nameof(SaveCloudData_Prefix), BindingFlags.NonPublic | BindingFlags.Static));
                harmony.Patch(method, prefix: prefix);
                Log?.LogInfo("TestHarness: BlockSaves prefix attached to OnlineInterfaceSteam.SaveCloudData");
            }
            catch (Exception ex)
            {
                Log?.LogWarning("TestHarness: failed to attach BlockSaves patch (" +
                                ex.GetType().Name + ": " + ex.Message + ") — save-blocking inactive");
            }
        }

        // Prefix on OnlineInterfaceSteam.SaveCloudData(IDictionary<string,string>, SaveCloudDataHandler).
        // We only bind the `handler` parameter by name. When BlockSaves is on we skip the original
        // (no StreamWriter runs, so no .sav file is touched) and invoke the handler with success=true
        // so the game believes the save completed and its flow continues normally.
        private static bool SaveCloudData_Prefix(SaveCloudDataHandler handler)
        {
            if (!ModConfig.BlockSaves.Value) return true; // run the real save

            if (!_blockLoggedOnce)
            {
                _blockLoggedOnce = true;
                Log?.LogInfo("TestHarness: save write blocked (BlockSaves=true)");
            }
            try { if (handler != null) handler(true); } catch { }
            return false; // skip original — no disk write
        }

        // ---------------------------------------------------------------------------------
        // Per-frame entry point. Called from Plugin.Update after the network pumps.
        // ---------------------------------------------------------------------------------
        public static void Tick(Plugin plugin)
        {
            AdvanceAutoPlay();
            TickAutoLoad(plugin);
            TickKillP2();
        }

        // ---------------------------------------------------------------------------------
        // C. AutoPlay — scripted input.
        // ---------------------------------------------------------------------------------
        private static void AdvanceAutoPlay()
        {
            if (!ModConfig.AutoPlay.Value)
            {
                _p1Cur = 0; _p1Prev = 0; _p1AxisX = 0f;
                return;
            }
            float now = Time.realtimeSinceStartup;
            _p1Prev = _p1Cur;
            _p1Cur = EvalButtons(now, 0f);
            _p1AxisX = EvalAxisX(now, 0f);
        }

        // Instantaneous scripted button bitmask at time `now` shifted by `phase` seconds.
        // Shoot is always held; Jump is held only during the leading 100ms of each 2s window.
        private static uint EvalButtons(float now, float phase)
        {
            uint b = 1u << ActionShoot; // Shoot held always
            float jp = Mod(now + phase, JumpPeriodSec);
            if (jp < JumpPulseSec) b |= 1u << ActionJump;
            return b;
        }

        // Scripted MoveHorizontal axis: +1 for the first half period, -1 for the second.
        private static float EvalAxisX(float now, float phase)
        {
            float t = Mod(now + phase, MoveHalfPeriodSec * 2f);
            return t < MoveHalfPeriodSec ? 1f : -1f;
        }

        private static float Mod(float a, float m)
        {
            float r = a % m;
            return r < 0f ? r + m : r;
        }

        // Host P1 override, queried by RewiredPatches' postfixes. edge: 0=held, 1=down, 2=up.
        // Returns true (and fills `result`) only when AutoPlay is driving this host's P1 and the
        // action is one we script (Jump/Shoot) — every other read is left untouched. Allocation-free.
        internal static bool TryP1Button(int playerId, int actionId, int edge, ref bool result)
        {
            if (!ModConfig.AutoPlay.Value) return false;
            if (CoopState.Mode != CoopMode.Host) return false;
            int p1 = CoopState.RewiredPlayer1Id;
            if (p1 < 0 || playerId != p1) return false;
            if (actionId != ActionJump && actionId != ActionShoot) return false;

            uint bit = 1u << actionId;
            bool cur = (_p1Cur & bit) != 0u;
            bool prev = (_p1Prev & bit) != 0u;
            if (edge == 0) result = cur;
            else if (edge == 1) result = cur && !prev;
            else result = !cur && prev;
            return true;
        }

        // Host P1 axis override: only MoveHorizontal is scripted; everything else is untouched.
        internal static bool TryP1Axis(int playerId, int actionId, ref float result)
        {
            if (!ModConfig.AutoPlay.Value) return false;
            if (CoopState.Mode != CoopMode.Host) return false;
            int p1 = CoopState.RewiredPlayer1Id;
            if (p1 < 0 || playerId != p1) return false;
            if (actionId != ActionMoveHorizontal) return false;

            result = _p1AxisX;
            return true;
        }

        // Client upload override, called from Plugin after CaptureLocalInputForUpload. Substitutes
        // the scripted pattern (phase-shifted from the host's) into the frame that gets streamed to
        // the host as Player 2's input. Bypasses the focus gate on purpose — the automated agent
        // isn't focusing windows. Allocation-free.
        internal static void FillClientUpload()
        {
            if (!ModConfig.AutoPlay.Value) return;
            if (CoopState.Mode != CoopMode.Client) return;
            float now = Time.realtimeSinceStartup;
            CoopState.LocalButtons = EvalButtons(now, ClientPhaseOffsetSec);
            CoopState.LocalAxisX = EvalAxisX(now, ClientPhaseOffsetSec);
            CoopState.LocalAxisY = 0f;
        }

        // ---------------------------------------------------------------------------------
        // B. AutoLoadLevel — host-side one-shot level jump.
        // ---------------------------------------------------------------------------------
        private static void TickAutoLoad(Plugin plugin)
        {
            if (_autoLoadDone) return;
            string cfg = ModConfig.AutoLoadLevel.Value;
            if (string.IsNullOrEmpty(cfg)) return;              // feature off; re-checkable
            if (CoopState.Mode != CoopMode.Host) return;
            if (plugin == null || plugin.HostInstance == null || !plugin.HostInstance.HasClient) return;

            // In -batchmode nothing ever presses start, so the title screen never runs
            // PlayerData.Init and Initialized stays false forever. Kick it ourselves, once,
            // and keep waiting until the (synchronous for the Steam interface) load completes.
            if (!PlayerData.Initialized)
            {
                if (!_initRequested)
                {
                    _initRequested = true;
                    Log?.LogInfo("TestHarness: PlayerData not initialized (no human at the title screen) — calling PlayerData.Init");
                    try { PlayerData.Init(OnPlayerDataInit); }
                    catch (Exception ex)
                    {
                        Log?.LogError("TestHarness: PlayerData.Init failed: " + ex.GetType().Name + ": " + ex.Message);
                        _autoLoadDone = true; // can't proceed without save data
                    }
                }
                return;
            }

            _autoLoadSettle += Time.unscaledDeltaTime;
            if (_autoLoadSettle < AutoLoadSettleSec) return;

            _autoLoadDone = true; // one-shot from here regardless of outcome

            Levels level;
            try
            {
                level = (Levels)Enum.Parse(typeof(Levels), cfg.Trim(), true);
            }
            catch
            {
                Log?.LogWarning("TestHarness: AutoLoadLevel '" + cfg + "' is not a valid Levels name. Valid names: " +
                                string.Join(", ", Enum.GetNames(typeof(Levels))));
                return;
            }

            try
            {
                // Slot 0 is the harness's scratch save; BlockSaves should be on so this never
                // actually persists. Set it before LoadLevel so the level reads a consistent slot.
                PlayerData.CurrentSaveFileIndex = 0;
                // A human normally joins P1 by pressing start at the title screen — force it,
                // plus P2 (idempotent; the connect flow usually already did P2), so the level
                // spawns both cups.
                P2AutoJoin.ForceJoin(0, PlayerId.PlayerOne);
                P2AutoJoin.ForceJoin(1, PlayerId.PlayerTwo);
                Log?.LogInfo("TestHarness: auto-loading level '" + level + "'");
                // Every argument passed explicitly — net35 doesn't emit the C# optional-parameter
                // defaults for a cross-assembly call, so relying on Icon/Context defaults would
                // fail to compile / bind.
                SceneLoader.LoadLevel(level, SceneLoader.Transition.Fade, SceneLoader.Icon.Hourglass, (SceneLoader.Context)null);
            }
            catch (Exception ex)
            {
                Log?.LogError("TestHarness: LoadLevel failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------------------
        // D. KillP2AfterSec — host-side one-shot death test.
        // ---------------------------------------------------------------------------------
        private static void TickKillP2()
        {
            if (_killDone) return;
            int sec = ModConfig.KillP2AfterSec.Value;
            if (sec <= 0) return;
            if (CoopState.Mode != CoopMode.Host) return; // host-side only; client mirrors via death-sync

            if (!PlayerManager.DoesPlayerExist(PlayerId.PlayerTwo)) { _killTimer = 0f; return; }
            string scene;
            try { scene = SceneManager.GetActiveScene().name; }
            catch { return; }
            if (scene == null || !scene.StartsWith("scene_level")) { _killTimer = 0f; return; }

            _killTimer += Time.unscaledDeltaTime;
            if (_killTimer < sec) return;

            _killDone = true;
            KillP2();
        }

        private static void KillP2()
        {
            try
            {
                Log?.LogInfo("TestHarness: killing P2 (KillP2AfterSec elapsed)");
                var p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
                if (p2 == null || p2.stats == null)
                {
                    Log?.LogWarning("TestHarness: P2 controller/stats missing — cannot kill");
                    return;
                }

                // On the HOST, damage flows normally: DamagePatches' PlayerDamageReceiver.TakeDamage
                // prefix only short-circuits when Mode == Client (verified in DamagePatches.cs), so a
                // real hit lands here and runs the game's own death sequence
                // (OnDamageTaken -> PlayerStatsManager.TakeDamage -> OnStatsDeath -> OnPlayerDeathEvent
                //  -> death effect -> PlayerData.Data.Die).
                //
                // We drop P2 to 1 HP first because a *non-lethal* hit starts hit_cr(), which sets
                // hardInvincibility for 10 frames — so repeated same-frame hits can't stack. Killing
                // from exactly 1 HP guarantees the single Pit hit is lethal and runs the full death
                // sequence in one shot. OnPitKnockUp deals 1 Pit damage through the real receiver path.
                p2.stats.SetHealth(1);
                p2.stats.OnPitKnockUp();
                Log?.LogInfo("TestHarness: P2 health after kill = " + p2.stats.Health);
            }
            catch (Exception ex)
            {
                Log?.LogError("TestHarness: KillP2 failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
