using System.Threading.Tasks;
using Unison.Baileys.Client;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// What <see cref="Helpers.ConnectionSessionHost"/> still needs from the connection owner:
    /// health-monitor stop, status publishing, pairing trace, self-identity filing, post-replay
    /// maintenance and the on-disk auth store.
    /// </summary>
    public interface IConnectionSessionHostPort
    {
        void StopConnectionHealthMonitor(string reason);

        void PublishConnectionStatus(string status);

        void PairingTrace(string message);

        void FileSelfIdentityAliases(string source);

        void EnsureSelfPhonePersisted();

        void SchedulePostReplay(int offlineCount);

        Task SaveAuthAsync(AuthState auth);
    }
}
