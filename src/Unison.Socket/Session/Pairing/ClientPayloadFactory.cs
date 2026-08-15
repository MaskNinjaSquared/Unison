// =============================================================================
// ClientPayloadFactory
//
// Builds the ClientPayload that closes the handshake: the registration variant
// when there are no credentials yet, the login variant once a user is known.
//
// Everything it announces - user agent, web sub-platform, device properties,
// history sync appetite - is derived from the single Browser tuple in
// SocketConfig, so the identity we present is consistent by construction.
//
// Ports: rc14 generateLoginNode / generateRegistrationNode in
//        src/Utils/validate-connection.ts
// =============================================================================
using System;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Unison.Baileys.Client;
using Unison.Baileys.Crypto;
using Unison.Baileys.Protocol;
using Unison.Socket.Sync;

namespace Unison.Socket.Session.Pairing
{
    /// <summary>
    /// Builds the registration or login ClientPayload from the browser tuple in
    /// <see cref="SocketConfig"/>, so the announced identity comes from one place.
    /// </summary>
    public sealed class ClientPayloadFactory : IClientPayloadFactory
    {
        public global::Proto.ClientPayload Build(AuthState auth, SocketConfig config)
        {
            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            return auth.Me == null || string.IsNullOrEmpty(auth.Me.Id)
                ? BuildRegistration(auth, config)
                : BuildLogin(auth, config);
        }

        private static global::Proto.ClientPayload BuildRegistration(AuthState auth, SocketConfig config)
        {
            var payload = BuildCommon(config);
            payload.Passive = false;
            payload.Pull = false;
            payload.DevicePairingData = new global::Proto.ClientPayload.Types.DevicePairingRegistrationData
            {
                BuildHash = ByteString.CopyFrom(BuildVersionHash(config.Version)),
                DeviceProps = ByteString.CopyFrom(BuildDeviceProps(config).ToByteArray()),
                ERegid = ByteString.CopyFrom(EncodeBigEndian(auth.RegistrationId, 4)),
                EKeytype = ByteString.CopyFrom(CryptoUtils.KEY_BUNDLE_TYPE),
                EIdent = ByteString.CopyFrom(auth.SignedIdentityKey.Public),
                ESkeyId = ByteString.CopyFrom(EncodeBigEndian(auth.SignedPreKey.KeyId, 3)),
                ESkeyVal = ByteString.CopyFrom(auth.SignedPreKey.KeyPair.Public),
                ESkeySig = ByteString.CopyFrom(auth.SignedPreKey.Signature)
            };

            return payload;
        }

        private static global::Proto.ClientPayload BuildLogin(AuthState auth, SocketConfig config)
        {
            string user, server;
            int device;
            WA.JidDecode(auth.Me.Id, out user, out server, out device);

            var payload = BuildCommon(config);
            payload.Passive = true;
            payload.Pull = true;
            payload.LidDbMigrated = false;

            ulong username;
            if (ulong.TryParse(user, out username))
            {
                payload.Username = username;
            }

            // Protobuf treats 0 and unset differently here, so device 0 stays unset.
            if (device > 0)
            {
                payload.Device = (uint)device;
            }

            return payload;
        }

        private static global::Proto.ClientPayload BuildCommon(SocketConfig config)
        {
            var payload = new global::Proto.ClientPayload
            {
                ConnectType = global::Proto.ClientPayload.Types.ConnectType.WifiUnknown,
                ConnectReason = global::Proto.ClientPayload.Types.ConnectReason.UserActivated,
                UserAgent = BuildUserAgent(config)
            };

            if (!IsAndroid(config))
            {
                payload.WebInfo = BuildWebInfo(config);
            }

            if (!string.IsNullOrEmpty(config.PushName))
            {
                payload.PushName = config.PushName;
            }

            return payload;
        }

        private static global::Proto.ClientPayload.Types.UserAgent BuildUserAgent(SocketConfig config)
        {
            return new global::Proto.ClientPayload.Types.UserAgent
            {
                AppVersion = new global::Proto.ClientPayload.Types.UserAgent.Types.AppVersion
                {
                    Primary = (uint)config.Version[0],
                    Secondary = (uint)config.Version[1],
                    Tertiary = (uint)config.Version[2]
                },
                Platform = IsAndroid(config)
                    ? global::Proto.ClientPayload.Types.UserAgent.Types.Platform.Android
                    : global::Proto.ClientPayload.Types.UserAgent.Types.Platform.Web,
                ReleaseChannel = global::Proto.ClientPayload.Types.UserAgent.Types.ReleaseChannel.Release,
                OsVersion = "0.1",
                Device = "Desktop",
                OsBuildNumber = "0.1",
                LocaleLanguageIso6391 = "en",
                Mnc = "000",
                Mcc = "000",
                LocaleCountryIso31661Alpha2 = config.CountryCode
            };
        }

        private static global::Proto.ClientPayload.Types.WebInfo BuildWebInfo(SocketConfig config)
        {
            var subPlatform = global::Proto.ClientPayload.Types.WebInfo.Types.WebSubPlatform.WebBrowser;

            // Only a full-sync Desktop companion may claim a native sub-platform.
            if (config.SyncFullHistory && string.Equals(BrowserAt(config, 1), "Desktop", StringComparison.Ordinal))
            {
                var os = BrowserAt(config, 0);
                if (string.Equals(os, "Mac OS", StringComparison.Ordinal))
                {
                    subPlatform = global::Proto.ClientPayload.Types.WebInfo.Types.WebSubPlatform.Darwin;
                }
                else if (string.Equals(os, "Windows", StringComparison.Ordinal))
                {
                    subPlatform = global::Proto.ClientPayload.Types.WebInfo.Types.WebSubPlatform.Win32;
                }
            }

            return new global::Proto.ClientPayload.Types.WebInfo { WebSubPlatform = subPlatform };
        }

        private static global::Proto.DeviceProps BuildDeviceProps(SocketConfig config)
        {
            return new global::Proto.DeviceProps
            {
                Os = BrowserAt(config, 0),
                PlatformType = GetPlatformType(BrowserAt(config, 1)),
                RequireFullSync = config.SyncFullHistory,
                Version = new global::Proto.DeviceProps.Types.AppVersion
                {
                    Primary = 10,
                    Secondary = 15,
                    Tertiary = 7
                },
                HistorySyncConfig = HistorySyncConfigFactory.Create()
            };
        }

        private static global::Proto.DeviceProps.Types.PlatformType GetPlatformType(string browserName)
        {
            if (string.IsNullOrEmpty(browserName))
            {
                return global::Proto.DeviceProps.Types.PlatformType.Chrome;
            }

            if (browserName.IndexOf("android", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return global::Proto.DeviceProps.Types.PlatformType.AndroidPhone;
            }

            try
            {
                return (global::Proto.DeviceProps.Types.PlatformType)Enum.Parse(
                    typeof(global::Proto.DeviceProps.Types.PlatformType), browserName, true);
            }
            catch (ArgumentException)
            {
                return global::Proto.DeviceProps.Types.PlatformType.Chrome;
            }
        }

        private static bool IsAndroid(SocketConfig config)
        {
            var browserName = BrowserAt(config, 1);
            return browserName.IndexOf("android", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BrowserAt(SocketConfig config, int index)
        {
            return config.Browser != null && config.Browser.Length > index
                ? (config.Browser[index] ?? string.Empty)
                : string.Empty;
        }

        /// <summary>MD5 of the dot-joined version, as rc14 does before sending buildHash.</summary>
        private static byte[] BuildVersionHash(int[] version)
        {
            using (var md5 = MD5.Create())
            {
                return md5.ComputeHash(Encoding.UTF8.GetBytes(string.Join(".", Array.ConvertAll(version, v => v.ToString()))));
            }
        }

        private static byte[] EncodeBigEndian(int value, int length)
        {
            var bytes = new byte[length];
            var remaining = (uint)value;

            for (var i = length - 1; i >= 0; i--)
            {
                bytes[i] = (byte)(remaining & 0xff);
                remaining >>= 8;
            }

            return bytes;
        }
    }
}
