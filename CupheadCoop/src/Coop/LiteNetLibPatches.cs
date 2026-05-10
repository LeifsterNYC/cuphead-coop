using System.Reflection;
using HarmonyLib;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// LiteNetLib's NetManager constructor eagerly instantiates a NatPunchModule, whose own
    /// constructor calls NetPacketProcessor.SubscribeReusable&lt;NatIntroducePacket&gt;(). That
    /// path uses NetSerializer's reflection codepath which lands on
    /// System.Runtime.Serialization.FormatterServices — and Cuphead's Unity 2017 Mono
    /// distribution doesn't ship System.Runtime.Serialization.
    ///
    /// We never use NAT punch (ZeroTier creates a flat L2 overlay between the two PCs, and
    /// for plain LAN we have direct addressability), so the cleanest fix is to no-op
    /// NatPunchModule's constructor. The reference still gets stashed in NetManager, but
    /// since NatPunchEnabled stays false by default, no method on the half-built object is
    /// ever called.
    ///
    /// LiteNetLib.NatPunchModule and LiteNetLib.NetSocket are both internal, so we resolve
    /// them via reflection into the loaded LiteNetLib assembly rather than typeof().
    /// </summary>
    [HarmonyPatch]
    internal static class NatPunchModule_SkipCtor_Patch
    {
        [HarmonyTargetMethod]
        public static MethodBase Resolve()
        {
            var asm = typeof(LiteNetLib.NetManager).Assembly;
            var natPunch = asm.GetType("LiteNetLib.NatPunchModule");
            var netSocket = asm.GetType("LiteNetLib.NetSocket");
            if (natPunch == null || netSocket == null)
                throw new System.InvalidOperationException(
                    "Could not resolve NatPunchModule/NetSocket types in LiteNetLib — bumped versions?");
            return AccessTools.Constructor(natPunch, new[] { netSocket });
        }

        [HarmonyPrefix]
        public static bool Prefix() => false; // skip body
    }
}
