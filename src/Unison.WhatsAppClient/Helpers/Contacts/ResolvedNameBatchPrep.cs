// =============================================================================
// ResolvedNameBatchPrep
//
// Turns a raw (jid, name, isSubject) into a ResolvedNameEntry without touching
// the UI thread or the name cache. Shared by app-state subjects/contact names
// and the usync batch path on the client.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>A name that has been resolved to an address, ready to be applied.</summary>
    public sealed class ResolvedNameEntry
    {
        public string Canonical;
        public string Normalized;
        public string Name;

        /// <summary>
        /// A group's subject, which is the group's name outright, as opposed to a contact's
        /// name, which is one input to a display name that is composed elsewhere.
        /// </summary>
        public bool IsSubject;
    }

    public static class ResolvedNameBatchPrep
    {
        /// <summary>
        /// Resolves an address and validates a name, without touching anything shared.
        /// </summary>
        public static void Add(
            List<ResolvedNameEntry> entries,
            string jid,
            string name,
            bool isSubject,
            Func<string, string> normalizeJid,
            Func<string, string> getCanonicalJid,
            Func<string, string, string> sanitizeContactLabel)
        {
            if (entries == null)
            {
                return;
            }

            if (normalizeJid == null)
            {
                throw new ArgumentNullException(nameof(normalizeJid));
            }

            if (getCanonicalJid == null)
            {
                throw new ArgumentNullException(nameof(getCanonicalJid));
            }

            if (sanitizeContactLabel == null)
            {
                throw new ArgumentNullException(nameof(sanitizeContactLabel));
            }

            string normalized = normalizeJid(jid);
            string canonical = getCanonicalJid(normalized);
            if (string.IsNullOrWhiteSpace(canonical) || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            // A subject is the group's own name; there is no number for it to be confused with.
            string resolved = isSubject ? name : sanitizeContactLabel(name, canonical);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                return;
            }

            if (isSubject && GroupIdPlaceholder.IsIdPlaceholder(resolved, canonical))
            {
                return;
            }

            entries.Add(new ResolvedNameEntry
            {
                Canonical = canonical,
                Normalized = normalized,
                Name = resolved,
                IsSubject = isSubject
            });
        }
    }
}
