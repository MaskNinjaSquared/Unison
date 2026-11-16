namespace Unison.Core.Models
{
    /// <summary>
    /// A history chunk was applied. Listeners only need the sync type; a null notice means
    /// "something changed, reload".
    /// </summary>
    public sealed class HistorySyncNotice
    {
        public HistorySyncNotice(string syncType)
        {
            SyncType = syncType ?? string.Empty;
        }

        /// <summary>The proto <c>HistorySyncType</c> name (<c>InitialBootstrap</c>, <c>OnDemand</c>, …).</summary>
        public string SyncType { get; }
    }
}
