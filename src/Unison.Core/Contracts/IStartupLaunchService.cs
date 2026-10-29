using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>What Windows will do with the app at sign-in.</summary>
    public enum StartupLaunchState
    {
        /// <summary>Not a desktop, or the OS has no startup tasks for packaged apps.</summary>
        NotSupported,
        Disabled,
        Enabled,

        /// <summary>Turned off in Task Manager / Settings; the app cannot turn it back on.</summary>
        DisabledByUser,
        DisabledByPolicy,
        EnabledByPolicy
    }

    /// <summary>
    /// "Start with Windows" (package startup task). Desktop only; Mobile reports
    /// <see cref="StartupLaunchState.NotSupported"/>.
    /// </summary>
    public interface IStartupLaunchService
    {
        bool IsSupported { get; }

        Task<StartupLaunchState> GetStateAsync();

        /// <summary>Asks Windows to enable or disable launch at sign-in; returns the resulting state.</summary>
        Task<StartupLaunchState> SetEnabledAsync(bool enabled);
    }
}
