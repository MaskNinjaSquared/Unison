using System;

namespace Unison.Core.Helpers
{
    /// <summary>Why an address cannot be used for a contact lookup.</summary>
    public enum ContactLookupSkip
    {
        None,
        Empty,

        /// <summary>Our own account. The server has nothing to tell us about ourselves.</summary>
        SelfAccount,

        /// <summary>A group, broadcast list or newsletter; usync answers about people.</summary>
        NotDirectChat,

        /// <summary>Nothing number-like survived. Usually a name typed into a phone field.</summary>
        NoDigits
    }

    /// <summary>
    /// The number a usync contact query is addressed to, derived from whatever the caller has:
    /// a JID, a canonical address, or digits a person typed.
    /// </summary>
    /// <remarks>
    /// Two callers built this by hand and neither stripped enough. The new-chat search box passes
    /// its text through untouched apart from '+', spaces and hyphens, so brackets and dots reached
    /// the wire — the server was asked about "+(11)999999999", found nobody, and the user was told
    /// the number is not on WhatsApp. `PhoneNumberHelper` next door already knew how to read a
    /// number properly; this puts the two together.
    /// </remarks>
    public sealed class ContactLookupNumber
    {
        private const string ServerPn = "@s.whatsapp.net";
        private const string ServerLid = "@lid";

        private static readonly string[] NonDirectServers = { "@g.us", "@broadcast", "@newsletter" };

        private ContactLookupNumber(string number, ContactLookupSkip skip)
        {
            Number = number;
            Skip = skip;
        }

        /// <summary>The query form, with a leading '+'. Null unless <see cref="IsUsable"/>.</summary>
        public string Number { get; private set; }

        public ContactLookupSkip Skip { get; private set; }

        public bool IsUsable
        {
            get { return Skip == ContactLookupSkip.None && !string.IsNullOrEmpty(Number); }
        }

        /// <param name="jidOrPhone">A JID, or digits typed by the user.</param>
        /// <param name="canonicalJid">
        /// The canonical address when known. A LID carries no phone number, so this is where the
        /// number lives for a contact reached that way.
        /// </param>
        /// <param name="selfJid">Our own address, so we do not ask the server about ourselves.</param>
        public static ContactLookupNumber For(string jidOrPhone, string canonicalJid, string selfJid)
        {
            if (string.IsNullOrWhiteSpace(jidOrPhone))
            {
                return new ContactLookupNumber(null, ContactLookupSkip.Empty);
            }

            string value = jidOrPhone.Trim();

            if (EndsWithAny(value, NonDirectServers))
            {
                return new ContactLookupNumber(null, ContactLookupSkip.NotDirectChat);
            }

            if (IsSameUser(value, selfJid))
            {
                return new ContactLookupNumber(null, ContactLookupSkip.SelfAccount);
            }

            string source = value;
            if (HasServer(value, ServerPn) || HasServer(value, ServerLid))
            {
                source = string.IsNullOrWhiteSpace(canonicalJid) ? value : canonicalJid.Trim();
            }

            // The device suffix goes before the digits are read. Reading digits first would fold
            // "5511988887777:12" into "551198888777712" - a well-formed query about nobody.
            string digits = PhoneNumberHelper.NormalizePhoneDigits(UserPart(source));
            if (string.IsNullOrEmpty(digits))
            {
                return new ContactLookupNumber(null, ContactLookupSkip.NoDigits);
            }

            return new ContactLookupNumber("+" + digits, ContactLookupSkip.None);
        }

        /// <summary>The part before the server and before any device suffix.</summary>
        private static string UserPart(string value)
        {
            int at = value.IndexOf('@');
            string user = at >= 0 ? value.Substring(0, at) : value;

            int device = user.IndexOf(':');
            return device >= 0 ? user.Substring(0, device) : user;
        }

        private static bool IsSameUser(string value, string selfJid)
        {
            if (string.IsNullOrWhiteSpace(selfJid))
            {
                return false;
            }

            string mine = PhoneNumberHelper.NormalizePhoneDigits(UserPart(selfJid.Trim()));
            if (string.IsNullOrEmpty(mine))
            {
                return false;
            }

            string theirs = PhoneNumberHelper.NormalizePhoneDigits(UserPart(value));
            return !string.IsNullOrEmpty(theirs) &&
                   string.Equals(mine, theirs, StringComparison.Ordinal);
        }

        private static bool HasServer(string value, string server)
        {
            return value.EndsWith(server, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EndsWithAny(string value, string[] servers)
        {
            for (int i = 0; i < servers.Length; i++)
            {
                if (HasServer(value, servers[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
