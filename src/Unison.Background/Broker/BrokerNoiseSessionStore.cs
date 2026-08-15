using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Unison.Baileys.Protocol;
using Windows.Storage;

namespace Unison.Background
{
    internal sealed class BrokerNoiseSessionSnapshot
    {
        public string SocketId { get; set; }
        public DateTime SavedUtc { get; set; }
        public NoiseSessionState State { get; set; }
    }

    /// <summary>
    /// Shared atomic store for the established Noise transport state. Both hosts
    /// use the same envelope, so the task never needs to initialize the app.
    /// </summary>
    internal static class BrokerNoiseSessionStore
    {
        private const string FileName = "socket-broker-noise-state.json";

        private sealed class Envelope
        {
            public int Version { get; set; }
            public string SocketId { get; set; }
            public DateTime SavedUtc { get; set; }
            public NoiseSessionState State { get; set; }
        }

        public static async Task SaveAsync(
            NoiseSessionState state,
            string socketId)
        {
            if (state == null || !state.IsValidEstablishedState())
            {
                throw new InvalidOperationException(
                    "Noise state is not an established session");
            }
            if (!BrokerOwnershipStore.IsManagedSocketId(socketId))
            {
                throw new InvalidOperationException(
                    "Noise state has an invalid broker socket id");
            }

            var envelope = new Envelope
            {
                Version = 3,
                SocketId = socketId,
                SavedUtc = DateTime.UtcNow,
                State = CloneState(state)
            };
            string json = JsonConvert.SerializeObject(envelope, Formatting.None);
            string temporaryName = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            StorageFile temporary = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                temporaryName,
                CreationCollisionOption.FailIfExists);
            try
            {
                await FileIO.WriteTextAsync(temporary, json);
                await temporary.RenameAsync(
                    FileName,
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

        public static async Task<BrokerNoiseSessionSnapshot> LoadSnapshotAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(
                    FileName);
                string json = await FileIO.ReadTextAsync(file);
                var envelope = JsonConvert.DeserializeObject<Envelope>(json);
                if (envelope == null ||
                    (envelope.Version != 1 &&
                     envelope.Version != 2 &&
                     envelope.Version != 3) ||
                    !BrokerOwnershipStore.IsManagedSocketId(envelope.SocketId) ||
                    envelope.State == null ||
                    !envelope.State.IsValidEstablishedState())
                {
                    return null;
                }

                return new BrokerNoiseSessionSnapshot
                {
                    SocketId = envelope.SocketId,
                    SavedUtc = envelope.SavedUtc,
                    State = CloneState(envelope.State)
                };
            }
            catch
            {
                return null;
            }
        }

        public static async Task ClearAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(
                    FileName);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        public static NoiseSessionState CloneState(NoiseSessionState state)
        {
            if (state == null) return null;
            return new NoiseSessionState
            {
                Version = state.Version,
                Hash = NoiseSessionState.CloneBytes(state.Hash),
                Salt = NoiseSessionState.CloneBytes(state.Salt),
                EncryptionKey = NoiseSessionState.CloneBytes(state.EncryptionKey),
                DecryptionKey = NoiseSessionState.CloneBytes(state.DecryptionKey),
                ReadCounter = state.ReadCounter,
                WriteCounter = state.WriteCounter,
                IsFinished = state.IsFinished,
                SentIntro = state.SentIntro,
                PendingInput = NoiseSessionState.CloneBytes(state.PendingInput)
            };
        }
    }
}
