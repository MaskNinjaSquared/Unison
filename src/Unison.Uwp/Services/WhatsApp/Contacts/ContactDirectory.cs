// =============================================================================
// ContactDirectory
//
// The only part of contacts that talks to the server.
//
// Two questions live here because they share one thing: the session. It owns the
// gate that hands out the live one and, the first time it sees each session,
// points the LID mapping store's fallback at it - so a number nobody has mapped
// yet can resolve itself later without anyone arranging it.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Socket.Session;
using Unison.Socket.Signal;
using Unison.Socket.UseCases.Contacts;
using Unison.Socket.UseCases.Groups;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    internal enum ContactLookupOutcome
    {
        /// <summary>The number has an account, and <see cref="ContactLookupResult.Jid"/> names it.</summary>
        Found,

        /// <summary>The server said there is no account. A real answer, not a failure.</summary>
        NoAccount,

        /// <summary>Nothing could be asked, or the answer never came. The caller may try another way.</summary>
        Unanswered
    }

    internal sealed class ContactLookupResult
    {
        private ContactLookupResult(ContactLookupOutcome outcome, string jid)
        {
            Outcome = outcome;
            Jid = jid;
        }

        public ContactLookupOutcome Outcome { get; }

        public string Jid { get; }

        public static ContactLookupResult Found(string jid)
        {
            return new ContactLookupResult(ContactLookupOutcome.Found, jid);
        }

        public static ContactLookupResult NoAccount()
        {
            return new ContactLookupResult(ContactLookupOutcome.NoAccount, null);
        }

        public static ContactLookupResult Unanswered()
        {
            return new ContactLookupResult(ContactLookupOutcome.Unanswered, null);
        }
    }

    internal sealed class ContactDirectory
    {
        private readonly IWhatsAppSessionProvider _sessions;
        private readonly LidMappingStore _lidMappings;

        private readonly object _resolverGate = new object();
        private WhatsAppSession _resolverSession;

        internal ContactDirectory(IWhatsAppSessionProvider sessions, LidMappingStore lidMappings)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _lidMappings = lidMappings ?? throw new ArgumentNullException(nameof(lidMappings));
        }

        /// <summary>
        /// Asks the server whether a number has an account, rather than inferring it from whether
        /// a display name came back - a contact with no name is not a number with no WhatsApp.
        /// </summary>
        public async Task<ContactLookupResult> LookUpAsync(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return ContactLookupResult.Unanswered();
            }

            var session = GetReadySession();
            if (session == null)
            {
                return ContactLookupResult.Unanswered();
            }

            try
            {
                var useCase = new OnWhatsAppUseCase(session.Connection);
                var results = await useCase.ExecuteAsync(new[] { phoneNumber }).ConfigureAwait(false);

                if (results.Count == 0)
                {
                    return ContactLookupResult.Unanswered();
                }

                var match = results[0];
                if (!match.Exists)
                {
                    Debug.WriteLine("[ContactDirectory] No WhatsApp account for " + phoneNumber);
                    return ContactLookupResult.NoAccount();
                }

                await RememberLidForAsync(session, match.Jid).ConfigureAwait(false);
                return ContactLookupResult.Found(match.Jid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactDirectory] Lookup failed: " + ex.Message);
                return ContactLookupResult.Unanswered();
            }
        }

        /// <summary>
        /// Harvests the LID pairs the group list discloses. A single participating query names
        /// every member of every group in both address spaces, which is by far the cheapest way
        /// to fill the mapping store.
        /// </summary>
        public async Task HarvestGroupMappingsAsync()
        {
            var session = GetReadySession();
            if (session == null)
            {
                return;
            }

            try
            {
                var useCase = new FetchParticipatingGroupsUseCase(session.Connection);
                var result = await useCase.ExecuteAsync().ConfigureAwait(false);
                if (result.Mappings.Count == 0)
                {
                    return;
                }

                await _lidMappings.StoreMappingsAsync(result.Mappings).ConfigureAwait(false);
                Debug.WriteLine("[ContactDirectory] Harvested " + result.Mappings.Count +
                                " LID mapping(s) from " + result.Groups.Count + " group(s)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactDirectory] Group mapping harvest failed: " + ex.Message);
            }
        }

        private async Task RememberLidForAsync(WhatsAppSession session, string jid)
        {
            try
            {
                var useCase = new FetchLidMappingsUseCase(session.Connection);
                var mappings = await useCase.ExecuteAsync(new[] { jid }).ConfigureAwait(false);
                if (mappings.Count > 0)
                {
                    await _lidMappings.StoreMappingsAsync(mappings).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // The lookup already succeeded; a missing mapping only costs a later query.
                Debug.WriteLine("[ContactDirectory] LID lookup failed for " + jid + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The live session, or null. Re-pointing the mapping store happens here rather than at
        /// construction because the socket is replaced on every reconnect, and a resolver bound
        /// to a closed session fails silently.
        /// </summary>
        private WhatsAppSession GetReadySession()
        {
            var session = _sessions.Current;
            if (session == null || !_sessions.IsReady)
            {
                return null;
            }

            lock (_resolverGate)
            {
                if (!ReferenceEquals(_resolverSession, session))
                {
                    _resolverSession = session;
                    var useCase = new FetchLidMappingsUseCase(session.Connection);
                    _lidMappings.PnToLidResolver = async jids =>
                        await useCase.ExecuteAsync(jids).ConfigureAwait(false);
                }
            }

            return session;
        }
    }
}
