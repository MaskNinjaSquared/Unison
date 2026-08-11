using System;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace Unison.Background
{
    internal sealed class BrokerOwnershipState
    {
        public int Version { get; set; }
        public string SocketId { get; set; }
        public string Generation { get; set; }
        public string Owner { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public bool ReconnectRequired { get; set; }
        public int SocketClosedCount { get; set; }
        public string LastReason { get; set; }
    }

    internal static class BrokerOwnershipStore
    {
        public static string CreateSocketId()
        {
            return SocketBrokerConstants.SocketIdPrefix + Guid.NewGuid().ToString("N");
        }

        public static bool IsManagedSocketId(string socketId)
        {
            return string.Equals(
                       socketId,
                       SocketBrokerConstants.LegacySocketId,
                       StringComparison.Ordinal) ||
                   string.Equals(
                       socketId,
                       SocketBrokerConstants.RegressionInProcessSocketId,
                       StringComparison.Ordinal) ||
                   (!string.IsNullOrEmpty(socketId) &&
                    socketId.StartsWith(
                        SocketBrokerConstants.SocketIdPrefix,
                        StringComparison.Ordinal));
        }

        public static BrokerOwnershipState Create(
            string socketId,
            string generation,
            string owner,
            string reason)
        {
            return new BrokerOwnershipState
            {
                Version = SocketBrokerConstants.OwnershipStateVersion,
                SocketId = socketId ?? string.Empty,
                Generation = generation ?? string.Empty,
                Owner = owner ?? string.Empty,
                UpdatedUtc = DateTime.UtcNow,
                ReconnectRequired = false,
                SocketClosedCount = 0,
                LastReason = reason ?? string.Empty
            };
        }

        public static async Task SaveAsync(BrokerOwnershipState state)
        {
            if (state == null ||
                !IsManagedSocketId(state.SocketId) ||
                string.IsNullOrWhiteSpace(state.Generation))
            {
                throw new InvalidOperationException("Invalid Socket Broker ownership state");
            }

            state.Version = SocketBrokerConstants.OwnershipStateVersion;
            state.UpdatedUtc = DateTime.UtcNow;
            string temporaryName = SocketBrokerConstants.OwnershipStateFile +
                                   "." + Guid.NewGuid().ToString("N") + ".tmp";
            StorageFile temporary = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                temporaryName,
                CreationCollisionOption.FailIfExists);
            try
            {
                await FileIO.WriteTextAsync(temporary, Serialize(state));
                await temporary.RenameAsync(
                    SocketBrokerConstants.OwnershipStateFile,
                    NameCollisionOption.ReplaceExisting);
                temporary = null;
            }
            finally
            {
                if (temporary != null)
                {
                    try { await temporary.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                }
            }
        }

        public static async Task<BrokerOwnershipState> LoadAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(
                    SocketBrokerConstants.OwnershipStateFile);
                string json = await FileIO.ReadTextAsync(file);
                BrokerOwnershipState state = Deserialize(json);
                if (state == null ||
                    state.Version != SocketBrokerConstants.OwnershipStateVersion ||
                    !IsManagedSocketId(state.SocketId))
                {
                    return null;
                }
                return state;
            }
            catch
            {
                return null;
            }
        }

        public static async Task MarkReconnectRequiredAsync(
            string socketId,
            string reason)
        {
            BrokerOwnershipState state = await LoadAsync();
            if (state == null ||
                !string.Equals(state.SocketId, socketId, StringComparison.Ordinal))
            {
                state = Create(
                    socketId,
                    Guid.NewGuid().ToString("N"),
                    "closed",
                    reason);
            }

            state.Owner = "closed";
            state.ReconnectRequired = true;
            state.SocketClosedCount = Math.Max(0, state.SocketClosedCount) + 1;
            state.LastReason = reason ?? "socket-closed";
            await SaveAsync(state);
            await BrokerLog.AppendAsync(
                "ownership",
                "reconnect-requested id=" + state.SocketId +
                " closeCount=" + state.SocketClosedCount +
                " reason=" + state.LastReason);
        }

        public static async Task ClearAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(
                    SocketBrokerConstants.OwnershipStateFile);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        private static string Serialize(BrokerOwnershipState state)
        {
            var json = new JsonObject();
            json.SetNamedValue(
                "version",
                JsonValue.CreateNumberValue(state.Version));
            json.SetNamedValue(
                "socketId",
                JsonValue.CreateStringValue(state.SocketId ?? string.Empty));
            json.SetNamedValue(
                "generation",
                JsonValue.CreateStringValue(state.Generation ?? string.Empty));
            json.SetNamedValue(
                "owner",
                JsonValue.CreateStringValue(state.Owner ?? string.Empty));
            json.SetNamedValue(
                "updatedUtc",
                JsonValue.CreateStringValue(
                    state.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
            json.SetNamedValue(
                "reconnectRequired",
                JsonValue.CreateBooleanValue(state.ReconnectRequired));
            json.SetNamedValue(
                "socketClosedCount",
                JsonValue.CreateNumberValue(state.SocketClosedCount));
            json.SetNamedValue(
                "lastReason",
                JsonValue.CreateStringValue(state.LastReason ?? string.Empty));
            return json.Stringify();
        }

        private static BrokerOwnershipState Deserialize(string json)
        {
            try
            {
                JsonObject parsed = JsonObject.Parse(json);
                DateTime updatedUtc;
                DateTime.TryParse(
                    parsed.GetNamedString("updatedUtc", string.Empty),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out updatedUtc);
                return new BrokerOwnershipState
                {
                    Version = (int)parsed.GetNamedNumber("version", 0),
                    SocketId = parsed.GetNamedString("socketId", string.Empty),
                    Generation = parsed.GetNamedString("generation", string.Empty),
                    Owner = parsed.GetNamedString("owner", string.Empty),
                    UpdatedUtc = updatedUtc,
                    ReconnectRequired = parsed.GetNamedBoolean("reconnectRequired", false),
                    SocketClosedCount = (int)parsed.GetNamedNumber("socketClosedCount", 0),
                    LastReason = parsed.GetNamedString("lastReason", string.Empty)
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
