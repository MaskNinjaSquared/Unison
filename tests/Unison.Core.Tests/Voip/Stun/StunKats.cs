using System.Text.Json;

namespace Unison.Core.Tests.Voip.Stun
{
    /// <summary>meowcaller stun/testdata/kats.json (MIT).</summary>
    internal sealed class StunKats
    {
        private static readonly Lazy<StunKats> Loaded = new Lazy<StunKats>(Load);

        public uint Ssrc { get; private set; }
        public string Tx { get; private set; } = "";
        public string RelayToken { get; private set; } = "";
        public string MiKey { get; private set; } = "";
        public uint Crc32Abc { get; private set; }
        public string AttrToken { get; private set; } = "";
        public string XorEndpoint { get; private set; } = "";
        public string NativeSenderSub { get; private set; } = "";
        public string MinimalMi { get; private set; } = "";
        public string WithFp { get; private set; } = "";
        public string WasmAllocate { get; private set; } = "";
        public string Ping { get; private set; } = "";
        public string VoipSenderSubscriptions { get; private set; } = "";
        public string ApkSenderSubscriptionsNoPid { get; private set; } = "";
        public string ApkSenderSubscriptionsPid { get; private set; } = "";
        public string ApkStreamDescriptors { get; private set; } = "";

        public static StunKats Instance => Loaded.Value;

        public byte[] TxBytes => Hex(Tx);

        public static byte[] Hex(string hex) => Convert.FromHexString(hex);

        public static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);

        private static StunKats Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Voip", "TestData", "stun", "kats.json");
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                JsonElement root = doc.RootElement;
                JsonElement stun = root.GetProperty("stun");
                JsonElement proto = root.GetProperty("stun_proto");
                return new StunKats
                {
                    Ssrc = root.GetProperty("inputs").GetProperty("ssrc").GetUInt32(),
                    Tx = stun.GetProperty("tx").GetString()!,
                    RelayToken = stun.GetProperty("relayToken").GetString()!,
                    MiKey = stun.GetProperty("miKey").GetString()!,
                    Crc32Abc = stun.GetProperty("crc32_abc").GetUInt32(),
                    AttrToken = stun.GetProperty("attr_token").GetString()!,
                    XorEndpoint = stun.GetProperty("xorEndpoint").GetString()!,
                    NativeSenderSub = stun.GetProperty("nativeSenderSub").GetString()!,
                    MinimalMi = stun.GetProperty("minimalMi").GetString()!,
                    WithFp = stun.GetProperty("withFp").GetString()!,
                    WasmAllocate = stun.GetProperty("wasmAllocate").GetString()!,
                    Ping = stun.GetProperty("ping").GetString()!,
                    VoipSenderSubscriptions = proto.GetProperty("voip_sender_subscriptions").GetString()!,
                    ApkSenderSubscriptionsNoPid = proto.GetProperty("apk_sender_subscriptions_nopid").GetString()!,
                    ApkSenderSubscriptionsPid = proto.GetProperty("apk_sender_subscriptions_pid").GetString()!,
                    ApkStreamDescriptors = proto.GetProperty("apk_stream_descriptors").GetString()!,
                };
            }
        }
    }
}
