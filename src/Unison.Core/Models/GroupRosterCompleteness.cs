namespace Unison.Core.Models
{
    /// <summary>How much of a group's participant list the app holds.</summary>
    public enum GroupRosterCompleteness
    {
        /// <summary>Nothing known yet.</summary>
        None = 0,

        /// <summary>
        /// Some members are known (message authors, joins, a disk restore, or a metadata reply cut
        /// short). Enough to name and badge them; not enough to list "everyone".
        /// </summary>
        Partial = 1,

        /// <summary>A metadata reply listed every participant.</summary>
        Full = 2
    }
}
