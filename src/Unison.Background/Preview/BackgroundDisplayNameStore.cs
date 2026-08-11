using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Unison.Baileys.Protocol;
using Windows.Storage;

namespace Unison.Background
{
    internal sealed class BackgroundDisplayNameSnapshot
    {
        public IDictionary<string, string> Names { get; set; }
        public string OwnPnJid { get; set; }
        public string OwnLidJid { get; set; }
    }

    /// <summary>
    /// Small foreground-produced map used only to improve toast titles. It avoids
    /// loading chat collections or contact services in the external host.
    /// </summary>
    internal static class BackgroundDisplayNameStore
    {
        private const string FileName =
            "socket-broker-display-names-v673b.json";

        private sealed class Envelope
        {
            public int Version { get; set; }
            public DateTime SavedUtc { get; set; }
            public Dictionary<string, string> Names { get; set; }
            public string OwnPnJid { get; set; }
            public string OwnLidJid { get; set; }
        }

        public static async Task SaveAsync(
            IDictionary<string, string> names,
            string ownPnJid = null,
            string ownLidJid = null)
        {
            var normalized = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in names ??
                     new Dictionary<string, string>())
            {
                AddName(normalized, pair.Key, pair.Value);
            }

            var envelope = new Envelope
            {
                Version = 1,
                SavedUtc = DateTime.UtcNow,
                Names = normalized,
                OwnPnJid = ownPnJid,
                OwnLidJid = ownLidJid
            };
            string json = JsonConvert.SerializeObject(
                envelope,
                Formatting.None);
            string temporaryName =
                FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            StorageFile temporary =
                await ApplicationData.Current.LocalFolder.CreateFileAsync(
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
                    try
                    {
                        await temporary.DeleteAsync(
                            StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static async Task<IDictionary<string, string>> LoadAsync()
        {
            BackgroundDisplayNameSnapshot snapshot =
                await LoadSnapshotAsync();
            return snapshot.Names;
        }

        public static async Task<BackgroundDisplayNameSnapshot>
            LoadSnapshotAsync()
        {
            try
            {
                StorageFile file =
                    await ApplicationData.Current.LocalFolder.GetFileAsync(
                        FileName);
                string json = await FileIO.ReadTextAsync(file);
                var envelope = JsonConvert.DeserializeObject<Envelope>(json);
                if (envelope == null ||
                    envelope.Version != 1 ||
                    envelope.Names == null)
                {
                    return CreateEmptySnapshot();
                }

                return new BackgroundDisplayNameSnapshot
                {
                    Names = new Dictionary<string, string>(
                        envelope.Names,
                        StringComparer.OrdinalIgnoreCase),
                    OwnPnJid = envelope.OwnPnJid,
                    OwnLidJid = envelope.OwnLidJid
                };
            }
            catch
            {
                return CreateEmptySnapshot();
            }
        }

        public static async Task ClearAsync()
        {
            try
            {
                StorageFile file =
                    await ApplicationData.Current.LocalFolder.GetFileAsync(
                        FileName);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        private static void AddName(
            IDictionary<string, string> target,
            string jid,
            string name)
        {
            if (string.IsNullOrWhiteSpace(jid) ||
                string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string value = name.Trim();
            string normalized = WA.NormalizeDeviceJid(jid);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                target[normalized] = value;
            }
            string baseJid = WA.GetBaseJid(normalized);
            if (!string.IsNullOrWhiteSpace(baseJid))
            {
                target[baseJid] = value;
            }
        }

        private static BackgroundDisplayNameSnapshot
            CreateEmptySnapshot()
        {
            return new BackgroundDisplayNameSnapshot
            {
                Names = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
