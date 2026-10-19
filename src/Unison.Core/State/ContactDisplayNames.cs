// =============================================================================
// ContactDisplayNames
//
// The name shown for an address: the lookups that feed DisplayNameResolution,
// and the separate rule for the account's own address.
//
// Extracted from WhatsAppService, where it read the same shared tables this
// class takes (push names, the address-book overlay, the alias map) plus the
// person cache. The client and the contact façade both answer from here, so a
// name rule changes in one place.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    /// <summary>What the logged-in account knows about its own name, read on each self lookup.</summary>
    public struct SelfNameInputs
    {
        public string Id;
        public string Lid;

        /// <summary>The name stored with the session credentials.</summary>
        public string CredentialsName;

        /// <summary>The name the profile façade shows for the account. Wins over push / agenda on self.</summary>
        public string CurrentUserName;

        /// <summary>Digits shown when no name is usable.</summary>
        public string Phone;
    }

    public sealed class ContactDisplayNames
    {
        private readonly JidAliasTable _aliases;
        private readonly DisplayNameTable _addressBookNames;
        private readonly IPersonStore _people;
        private readonly ISelfMarkerNaming _selfMarkers;
        private readonly ContactLabelSanitizer _labels;
        private readonly PushNameLookup _pushNames;

        private Func<SelfNameInputs> _self = () => default(SelfNameInputs);
        private Action<ContactLabelResult, string> _onLabelDecision;
        private string _lastSelfNameForLog;

        public ContactDisplayNames(
            JidAliasTable aliases,
            DisplayNameTable pushNames,
            DisplayNameTable addressBookNames,
            IPersonStore people,
            ISelfMarkerNaming selfMarkers)
        {
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            if (pushNames == null) throw new ArgumentNullException(nameof(pushNames));
            _addressBookNames = addressBookNames ?? throw new ArgumentNullException(nameof(addressBookNames));
            _people = people ?? throw new ArgumentNullException(nameof(people));
            _selfMarkers = selfMarkers ?? throw new ArgumentNullException(nameof(selfMarkers));
            _labels = new ContactLabelSanitizer(selfMarkers);
            _pushNames = new PushNameLookup(pushNames, aliases, _labels, (result, jid) => _onLabelDecision?.Invoke(result, jid));
        }

        /// <summary>
        /// Points the self rule at the logged-in account. A function rather than a value for the
        /// same reason as <see cref="JidAliasTable.BindSelf"/>: the account's name and LID arrive
        /// after construction and change in place.
        /// </summary>
        /// <param name="onLabelDecision">Told about every label the sanitizer judged, with its address. Optional.</param>
        public void Bind(Func<SelfNameInputs> self, Action<ContactLabelResult, string> onLabelDecision)
        {
            _self = self ?? throw new ArgumentNullException(nameof(self));
            _onLabelDecision = onLabelDecision;
        }

        public string Resolve(string jid, string context)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return string.Empty;
            }

            string normalized = JidHelper.Normalize(jid);
            string canonical = _aliases.GetCanonicalJid(normalized);
            bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

            if (_aliases.IsSelfLinked(canonical) || _aliases.IsSelfLinked(normalized))
            {
                return ResolveSelf(canonical, normalized);
            }

            PersonSource personSource;
            string personName = FindPersonName(canonical, out personSource);
            if (string.IsNullOrWhiteSpace(personName))
            {
                personName = FindPersonName(normalized, out personSource);
            }

            if (!string.IsNullOrWhiteSpace(personName) && personSource.ToNameSource() == NameSource.AddressBook)
            {
                return personName;
            }

            if (string.IsNullOrWhiteSpace(personName))
            {
                // Cold cache: warm from disk without blocking the UI name path.
                _ = WarmPersonAsync(canonical);
                if (!string.Equals(canonical, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _ = WarmPersonAsync(normalized);
                }
            }

            return DisplayNameResolution.Resolve(new DisplayNameSources
            {
                PersonName = personName,
                PersonSource = personSource,
                PhoneContactName = FindPhoneContactName(canonical, normalized),
                WhatsAppName = _pushNames.FindBest(canonical, normalized),
                CanonicalJid = canonical,
                IsGroup = isGroup,
                IsSenderContext = string.Equals(context, "sender", StringComparison.OrdinalIgnoreCase)
            });
        }

        /// <summary>The first usable push name under any of the addresses or their aliases.</summary>
        public string FindBestPushName(params string[] jids) => _pushNames.FindBest(jids);

        private string ResolveSelf(string canonical, string normalized)
        {
            SelfNameInputs self = _self();
            // Profile first: the personal chat must match Settings / the logged-in account.
            // A WhatsApp push nick or an agenda entry under our own number used to win here and
            // rename the notes-to-self row to something unrelated during name enrichment.
            var sources = new[]
            {
                Source("current-user-name", () => self.CurrentUserName),
                Source("auth-me-name", () => self.CredentialsName),
                Source("whatsapp-cache", () => _pushNames.FindBest(canonical, normalized, self.Id, self.Lid)),
                Source("phone-canonical", () => RawPhoneContactName(canonical)),
                Source("phone-normalized", () => RawPhoneContactName(normalized))
            };

            SelfDisplayNameResolution.Result result = SelfDisplayNameResolution.Pick(
                sources,
                candidate => SelfNameCandidate.Normalize(candidate, canonical, normalized, _selfMarkers),
                self.Phone);

            if (!string.Equals(_lastSelfNameForLog, result.Name, StringComparison.Ordinal))
            {
                _lastSelfNameForLog = result.Name;
                Debug.WriteLine($"[ContactDisplayNames] Self base name resolved for {canonical}: '{(result.Name.Length == 0 ? "(empty)" : result.Name)}' (source={result.Source ?? "fallback"})");
            }

            return result.Name;
        }

        private static KeyValuePair<string, Func<string>> Source(string source, Func<string> read) =>
            new KeyValuePair<string, Func<string>>(source, read);

        private string RawPhoneContactName(string jid) =>
            jid != null && _addressBookNames.TryGetValue(jid, out var name) ? name : null;

        /// <summary>
        /// The address book's name for either form of the address, once sanitized. Both forms
        /// are tried because a contact saved under a phone number has to be found from a LID.
        /// </summary>
        private string FindPhoneContactName(string canonical, string normalized)
        {
            string fromCanonical = SanitizedPhoneContactName(canonical);
            if (!string.IsNullOrWhiteSpace(fromCanonical))
            {
                return fromCanonical;
            }

            return string.Equals(canonical, normalized, StringComparison.OrdinalIgnoreCase)
                ? null
                : SanitizedPhoneContactName(normalized);
        }

        private string SanitizedPhoneContactName(string jid)
        {
            string raw = string.IsNullOrWhiteSpace(jid) ? null : RawPhoneContactName(jid);
            return string.IsNullOrWhiteSpace(raw) ? null : Sanitize(raw, jid);
        }

        private string FindPersonName(string jid, out PersonSource source)
        {
            source = PersonSource.Unknown;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            try
            {
                Person person = _people.TryGetCached(jid);
                if (person == null || string.IsNullOrWhiteSpace(person.Name))
                {
                    return null;
                }

                source = person.Source;
                return Sanitize(person.Name, jid);
            }
            catch
            {
                return null;
            }
        }

        private async Task WarmPersonAsync(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            try
            {
                await _people.GetAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactDisplayNames] Person warm failed for " + jid + ": " + ex.Message);
            }
        }

        private string Sanitize(string label, string contextJid)
        {
            ContactLabelResult result = _labels.Sanitize(label, contextJid);
            if (!string.IsNullOrEmpty(contextJid))
            {
                _onLabelDecision?.Invoke(result, contextJid);
            }

            return result.IsUsable ? result.Label : null;
        }
    }
}
