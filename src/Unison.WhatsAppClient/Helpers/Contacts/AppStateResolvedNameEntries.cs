// =============================================================================
// AppStateResolvedNameEntries
//
// Builds the ResolvedNameEntry list for app-state subject / contact mutations
// before the host applies them on the UI thread.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AppStateResolvedNameEntries
    {
        public static List<ResolvedNameEntry> ForContact(
            string jid,
            string name,
            Func<string, string> normalizeJid,
            Func<string, string> getCanonicalJid,
            Func<string, string, string> sanitizeContactLabel)
        {
            var entries = new List<ResolvedNameEntry>(1);
            ResolvedNameBatchPrep.Add(
                entries,
                jid,
                name,
                isSubject: false,
                normalizeJid,
                getCanonicalJid,
                sanitizeContactLabel);
            return entries;
        }

        public static List<ResolvedNameEntry> ForGroupSubjects(
            IEnumerable<KeyValuePair<string, string>> subjectsByJid,
            Func<string, string> normalizeJid,
            Func<string, string> getCanonicalJid,
            Func<string, string, string> sanitizeContactLabel)
        {
            var entries = new List<ResolvedNameEntry>();
            if (subjectsByJid == null)
            {
                return entries;
            }

            foreach (KeyValuePair<string, string> pair in subjectsByJid)
            {
                ResolvedNameBatchPrep.Add(
                    entries,
                    pair.Key,
                    pair.Value,
                    isSubject: true,
                    normalizeJid,
                    getCanonicalJid,
                    sanitizeContactLabel);
            }

            return entries;
        }
    }
}
