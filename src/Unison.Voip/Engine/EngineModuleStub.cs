// =============================================================================
// Engine module scaffolds — meowcaller map (stun / relay / srtp / mlow).
// Bodies stay NotSupported until the Opus-reviewed port starts (plan Fase 4).
// =============================================================================
using System;

namespace Unison.Voip.Engine
{
    /// <summary>Placeholder for meowcaller <c>stun/</c>.</summary>
    public static class StunModule
    {
        public const string MeowcallerPath = "stun/";

        public static void EnsureNotImplemented() =>
            throw new NotSupportedException(
                "STUN is scaffolded; port from meowcaller/stun after Opus review.");
    }

    /// <summary>Placeholder for meowcaller <c>relay/</c>.</summary>
    public static class RelayModule
    {
        public const string MeowcallerPath = "relay/";

        public static void EnsureNotImplemented() =>
            throw new NotSupportedException(
                "Relay is scaffolded; port from meowcaller/relay after Opus review.");
    }

    /// <summary>Placeholder for meowcaller <c>srtp/</c> (WARP / SFrame).</summary>
    public static class SrtpModule
    {
        public const string MeowcallerPath = "srtp/";

        public static void EnsureNotImplemented() =>
            throw new NotSupportedException(
                "SRTP/WARP is scaffolded; port from meowcaller/srtp after Opus review.");
    }

    /// <summary>Placeholder for meowcaller <c>mlow/</c> codec.</summary>
    public static class MlowModule
    {
        public const string MeowcallerPath = "mlow/";

        public static void EnsureNotImplemented() =>
            throw new NotSupportedException(
                "MLow is scaffolded; port from meowcaller/mlow after Opus review.");
    }
}
