// =============================================================================
// PushNameLookup
//
// The first usable push name stored under an address, its alias, or the other
// form of the same user. Every stored label goes through the sanitizer, so a
// masked number or a self marker never counts as a name.
// =============================================================================
using System;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    public sealed class PushNameLookup
    {
        private readonly DisplayNameTable _pushNames;
        private readonly JidAliasTable _aliases;
        private readonly ContactLabelSanitizer _labels;
        private readonly Action<ContactLabelResult, string> _onDecision;

        /// <param name="onDecision">Told about every stored label that was judged, with the address it was stored under. Optional.</param>
        public PushNameLookup(
            DisplayNameTable pushNames,
            JidAliasTable aliases,
            ContactLabelSanitizer labels,
            Action<ContactLabelResult, string> onDecision = null)
        {
            _pushNames = pushNames ?? throw new ArgumentNullException(nameof(pushNames));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _labels = labels ?? throw new ArgumentNullException(nameof(labels));
            _onDecision = onDecision;
        }

        public string FindBest(params string[] jids)
        {
            foreach (string candidate in NameLookupCandidates.Expand(jids, JidHelper.Normalize, AliasOf))
            {
                string stored;
                if (!_pushNames.TryGetValue(candidate, out stored))
                {
                    continue;
                }

                ContactLabelResult result = _labels.Sanitize(stored, candidate);
                _onDecision?.Invoke(result, candidate);
                if (result.IsUsable && !string.IsNullOrWhiteSpace(result.Label))
                {
                    return result.Label;
                }
            }

            return null;
        }

        public bool HasName(string jid, string canonicalJid) => FindBest(jid, canonicalJid) != null;

        private string AliasOf(string normalized)
        {
            string alias;
            return _aliases.TryGetValue(normalized, out alias) && !string.IsNullOrWhiteSpace(alias)
                ? alias
                : null;
        }
    }
}
