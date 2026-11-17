using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// What <see cref="Helpers.StartupMaintenanceHost"/> still needs from the connection owner:
    /// connection-state init, window visibility, quiet/memory waits and the phone INPC.
    /// </summary>
    public interface IStartupMaintenanceHostPort
    {
        Task InitializeConnectionStateAsync();

        bool IsAuthRegistered { get; }

        bool IsAppWindowVisible { get; }

        Task WaitForStartupQuietAsync(
            TimeSpan budget,
            string reason,
            CancellationToken token);

        Task<bool> WaitForMemoryHeadroomAsync(string reason, CancellationToken token);

        void NotifyCurrentUserPhoneChanged();
    }
}
