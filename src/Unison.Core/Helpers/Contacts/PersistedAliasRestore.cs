// =============================================================================
// PersistedAliasRestore
//
// One stored alias row at startup: normalize, skip blanks, drop identity poison.
// Filing into the table stays on the host (bulk import + self seed).
// =============================================================================
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    public enum PersistedAliasRestoreAction
    {
        /// <summary>Blank or whitespace-only after normalize — leave the table alone.</summary>
        Skip,

        /// <summary>Safe to write both directions under the open bulk-import scope.</summary>
        Keep,

        /// <summary>Would confuse our own identity — count as poisoned and drop.</summary>
        DropPoison
    }

    public static class PersistedAliasRestore
    {
        public static PersistedAliasRestoreAction Evaluate(
            string key,
            string value,
            JidAliasTable table,
            out string normalizedKey,
            out string normalizedValue)
        {
            normalizedKey = JidHelper.Normalize(key);
            normalizedValue = JidHelper.Normalize(value);

            if (string.IsNullOrWhiteSpace(normalizedKey) ||
                string.IsNullOrWhiteSpace(normalizedValue))
            {
                normalizedKey = null;
                normalizedValue = null;
                return PersistedAliasRestoreAction.Skip;
            }

            if (AliasPairPolicy.IsUnsafeToRestore(normalizedKey, normalizedValue, table))
            {
                return PersistedAliasRestoreAction.DropPoison;
            }

            return PersistedAliasRestoreAction.Keep;
        }
    }
}
