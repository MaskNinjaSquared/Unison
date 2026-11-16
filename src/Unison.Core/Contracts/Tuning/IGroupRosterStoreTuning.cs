// =============================================================================
// IGroupRosterStoreTuning
//
// How many group rosters the host keeps in RAM between SQLite writes. Product
// layer picks the tier; the store only applies the cap.
// =============================================================================
namespace Unison.Core.Contracts.Tuning
{
    public interface IGroupRosterStoreTuning
    {
        /// <summary>Recent groups whose roster snapshot may stay in memory.</summary>
        int MemoryMaxGroups { get; }
    }
}