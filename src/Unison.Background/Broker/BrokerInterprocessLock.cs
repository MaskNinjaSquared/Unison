using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace Unison.Background
{
    /// <summary>
    /// Package-local cross-process lease. Creation/rename of the lock file is the
    /// atomic arbitration point shared by the foreground app and external task.
    /// </summary>
    internal static class BrokerInterprocessLock
    {
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(75);

        public static async Task<BrokerInterprocessLease> AcquireAsync(
            string owner,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            string token = Guid.NewGuid().ToString("N");
            DateTime deadlineUtc = DateTime.UtcNow.Add(timeout);
            string safeOwner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner.Trim();

            while (DateTime.UtcNow < deadlineUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StorageFile candidate = null;
                try
                {
                    string candidateName = SocketBrokerConstants.OwnershipLockFile +
                                           "." + token + ".tmp";
                    candidate = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                        candidateName,
                        CreationCollisionOption.FailIfExists);
                    await FileIO.WriteTextAsync(
                        candidate,
                        CreateLeaseJson(token, safeOwner, DateTime.UtcNow));
                    await candidate.RenameAsync(
                        SocketBrokerConstants.OwnershipLockFile,
                        NameCollisionOption.FailIfExists);

                    await BrokerLog.AppendAsync(
                        "lock",
                        "ownership-lock-acquired owner=" + safeOwner +
                        " token=" + ShortToken(token));
                    return new BrokerInterprocessLease(candidate, token, safeOwner);
                }
                catch
                {
                    if (candidate != null)
                    {
                        try { await candidate.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                    }
                }

                await TryBreakStaleLeaseAsync();
                await Task.Delay(80, cancellationToken);
            }

            await BrokerLog.AppendAsync(
                "lock",
                "ownership-lock-timeout owner=" + safeOwner +
                " timeoutMs=" + Math.Max(0, (int)timeout.TotalMilliseconds));
            return null;
        }

        private static async Task TryBreakStaleLeaseAsync()
        {
            try
            {
                StorageFile current = await ApplicationData.Current.LocalFolder.GetFileAsync(
                    SocketBrokerConstants.OwnershipLockFile);
                string json = await FileIO.ReadTextAsync(current);
                DateTime acquiredUtc;
                if (!TryReadLeaseUtc(json, out acquiredUtc) ||
                    DateTime.UtcNow - acquiredUtc <= StaleAfter)
                {
                    return;
                }

                string staleName = SocketBrokerConstants.OwnershipLockFile +
                                   ".stale-" + Guid.NewGuid().ToString("N");
                await current.RenameAsync(staleName, NameCollisionOption.FailIfExists);
                await BrokerLog.AppendAsync(
                    "lock",
                    "ownership-lock-stale-broken ageSeconds=" +
                    Math.Max(0, (int)(DateTime.UtcNow - acquiredUtc).TotalSeconds));
                try { await current.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
            }
            catch
            {
                // The current holder may have released the file between lookup and rename.
            }
        }

        private static string CreateLeaseJson(string token, string owner, DateTime acquiredUtc)
        {
            var json = new JsonObject();
            json.SetNamedValue("version", JsonValue.CreateNumberValue(1));
            json.SetNamedValue(
                "token",
                JsonValue.CreateStringValue(token ?? string.Empty));
            json.SetNamedValue(
                "owner",
                JsonValue.CreateStringValue(owner ?? string.Empty));
            json.SetNamedValue(
                "acquiredUtc",
                JsonValue.CreateStringValue(
                    acquiredUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
            return json.Stringify();
        }

        private static bool TryReadLeaseUtc(string json, out DateTime acquiredUtc)
        {
            acquiredUtc = DateTime.MinValue;
            try
            {
                JsonObject parsed = JsonObject.Parse(json);
                string value = parsed.GetNamedString("acquiredUtc", string.Empty);
                return DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out acquiredUtc);
            }
            catch
            {
                return false;
            }
        }

        private static string ShortToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;
            return token.Length <= 8 ? token : token.Substring(0, 8);
        }
    }

    internal sealed class BrokerInterprocessLease
    {
        private StorageFile _lockFile;
        private readonly string _token;
        private readonly string _owner;
        private int _released;

        internal BrokerInterprocessLease(StorageFile lockFile, string token, string owner)
        {
            _lockFile = lockFile;
            _token = token;
            _owner = owner;
        }

        public async Task ReleaseAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            StorageFile file = _lockFile;
            _lockFile = null;
            try
            {
                if (file != null)
                {
                    string content = await FileIO.ReadTextAsync(file);
                    if (content != null &&
                        content.IndexOf(_token, StringComparison.Ordinal) >= 0)
                    {
                        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                }
                await BrokerLog.AppendAsync(
                    "lock",
                    "ownership-lock-released owner=" + (_owner ?? string.Empty) +
                    " token=" + (_token == null
                        ? string.Empty
                        : _token.Substring(0, Math.Min(8, _token.Length))));
            }
            catch (Exception ex)
            {
                await BrokerLog.AppendAsync(
                    "lock",
                    "ownership-lock-release-failed owner=" + (_owner ?? string.Empty) +
                    " error=" + ex.GetType().Name +
                    " hresult=0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            }
        }
    }
}
