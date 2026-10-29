using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>Roster changes not yet put on the row, handed out once by the index.</summary>
    public sealed class GroupRosterDelta
    {
        public GroupRosterDelta(
            string groupJid,
            IReadOnlyList<GroupRosterRecord> upserted,
            IReadOnlyList<GroupRosterRecord> removed,
            GroupRosterCompleteness completeness)
        {
            GroupJid = groupJid;
            Upserted = upserted ?? new List<GroupRosterRecord>();
            Removed = removed ?? new List<GroupRosterRecord>();
            Completeness = completeness;
        }

        public string GroupJid { get; }

        public IReadOnlyList<GroupRosterRecord> Upserted { get; }

        public IReadOnlyList<GroupRosterRecord> Removed { get; }

        public GroupRosterCompleteness Completeness { get; }

        public bool IsEmpty => Upserted.Count == 0 && Removed.Count == 0;
    }

    /// <summary>
    /// Payload of <c>IChatStateStore.GroupRosterChanged</c>: which group, and which participants
    /// changed. <see cref="ChangedJids"/> is empty when the whole list was replaced.
    /// </summary>
    public sealed class GroupRosterChangedEventArgs : System.EventArgs
    {
        private static readonly IReadOnlyList<string> NoJids = new string[0];

        public GroupRosterChangedEventArgs(string groupJid, IReadOnlyList<string> changedJids = null)
        {
            GroupJid = groupJid;
            ChangedJids = changedJids ?? NoJids;
        }

        public string GroupJid { get; }

        public IReadOnlyList<string> ChangedJids { get; }

        public bool IsWholeRoster => ChangedJids.Count == 0;
    }
}
