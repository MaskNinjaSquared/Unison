namespace Unison.Core.Contracts
{
    /// <summary>
    /// Process memory level for startup / sync diagnostics. Hosts map WinRT
    /// <c>MemoryManager</c> (or equivalent) behind this so client helpers stay portable.
    /// </summary>
    public interface IAppMemoryInfo
    {
        /// <summary>Host-defined level name (e.g. Low / Medium / High / OverLimit).</summary>
        string UsageLevel { get; }

        /// <summary>Current process working-set estimate in bytes.</summary>
        ulong UsageBytes { get; }

        /// <summary>Host-reported memory budget for the process in bytes.</summary>
        ulong LimitBytes { get; }

        /// <summary>True when the host reports the lowest usable memory band.</summary>
        bool IsLow { get; }
    }
}
