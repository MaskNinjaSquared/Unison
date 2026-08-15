// =============================================================================
// SocketConfig
//
// Every tunable of a session in one place: endpoint, timeouts, client version,
// browser identity and pre-key policy. Values default to rc14's; where the
// legacy client disagreed the rc14 number wins and the comment says why.
//
// The Browser tuple matters more than it looks - it feeds the DeviceProps, the
// WebInfo sub-platform and the QR platform id at once, so the three can never
// contradict each other the way they do today.
//
// Ports: rc14 src/Defaults/index.ts
// =============================================================================
using System;

namespace Unison.Socket.Session
{
    /// <summary>
    /// Tunables for a socket session. Defaults are the rc14 values; where the legacy
    /// <c>SocketClient</c> disagreed, the rc14 number wins and the difference is called out.
    /// </summary>
    public sealed class SocketConfig
    {
        public Uri WaWebSocketUrl { get; set; } = new Uri("wss://web.whatsapp.com/ws/chat");

        public string Origin { get; set; } = "https://web.whatsapp.com";

        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>rc14 keepAliveIntervalMs. The legacy client pinged every 20s.</summary>
        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Extra slack before declaring the connection lost, as in rc14.</summary>
        public TimeSpan KeepAliveGrace { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan DefaultQueryTimeout { get; set; } = TimeSpan.FromSeconds(60);

        public TimeSpan QrTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>rc14 embedded version. Hosts may override with a freshly fetched one.</summary>
        public int[] Version { get; set; } = { 2, 3000, 1043857760 };

        /// <summary>rc14 default browser tuple: os, browser, version.</summary>
        public string[] Browser { get; set; } = { "Mac OS", "Chrome", "14.4.1" };

        public bool SyncFullHistory { get; set; } = true;

        /// <summary>
        /// rc14 INITIAL_PREKEY_COUNT. The legacy client uploaded 30, which starves the server
        /// and breaks E2E for new conversations once the batch runs out.
        /// </summary>
        public int InitialPreKeyCount { get; set; } = 812;

        /// <summary>rc14 MIN_PREKEY_COUNT. The legacy client replenished below 30.</summary>
        public int MinPreKeyCount { get; set; } = 5;

        public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public int MaxMsgRetryCount { get; set; } = 5;

        /// <summary>Reported as localeCountryIso31661Alpha2 in the client payload.</summary>
        public string CountryCode { get; set; } = "US";

        /// <summary>Optional push name sent with the client payload.</summary>
        public string PushName { get; set; }

        public string UserAgent { get; set; }
    }
}
