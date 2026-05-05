using CupheadCoop.Coop;
using UnityEngine;

namespace CupheadCoop
{
    /// <summary>
    /// Tiny top-left IMGUI status panel so the tester can see at a glance whether the mod is live,
    /// whether discovery worked, and whether input is flowing. Invisible when nothing interesting
    /// is happening (Off mode + no debug flag).
    /// </summary>
    internal class CoopOverlay : MonoBehaviour
    {
        private GUIStyle _style;
        private GUIStyle _bgStyle;
        private Texture2D _bgTex;

        private void EnsureStyles()
        {
            if (_style != null) return;
            _bgTex = new Texture2D(1, 1);
            _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
            _bgTex.Apply();
            _bgStyle = new GUIStyle { normal = { background = _bgTex } };
            _style = new GUIStyle
            {
                fontSize = 12,
                normal = { textColor = Color.white },
                padding = new RectOffset(6, 6, 4, 4),
                richText = false
            };
        }

        private void OnGUI()
        {
            // Don't render anything until we have something to say.
            bool active = CoopState.Mode != CoopMode.Off || ModConfig.DebugForceP2WalkRight.Value;
            if (!active) return;

            EnsureStyles();

            string mode = CoopState.Mode.ToString();
            string p1 = CoopState.RewiredPlayer1Id < 0 ? "?" : CoopState.RewiredPlayer1Id.ToString();
            string p2 = CoopState.RewiredPlayer2Id < 0 ? "?" : CoopState.RewiredPlayer2Id.ToString();
            string remote = CoopState.HasRemoteInput ? "rx" : "--";
            string seq = CoopState.LastAppliedSequence.ToString();
            string btns = CoopState.CurrentButtons.ToString("X");
            string ax = CoopState.AxisX.ToString("0.00") + "," + CoopState.AxisY.ToString("0.00");
            string forced = ModConfig.DebugForceP2WalkRight.Value ? "  [DEBUG: P2 walks right]" : "";

            string line1 = "CupheadCoop v" + Plugin.Version + "  mode=" + mode + forced;
            string line2 = "rewired p1=" + p1 + " p2=" + p2 + "   net=" + remote +
                          " seq=" + seq + " btns=" + btns + " axes=" + ax;

            const float pad = 8f;
            const float w = 520f;
            float h = 36f;
            var rect = new Rect(pad, pad, w, h);
            GUI.Box(rect, GUIContent.none, _bgStyle);
            GUI.Label(new Rect(rect.x, rect.y, w, 18), line1, _style);
            GUI.Label(new Rect(rect.x, rect.y + 16, w, 18), line2, _style);
        }
    }
}
