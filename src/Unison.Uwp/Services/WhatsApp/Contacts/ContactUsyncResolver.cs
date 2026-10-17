// =============================================================================
// ContactUsyncResolver
//
// Asks the server who a set of contacts are, and decides what each answer may
// change: the LID/phone pair, a one-time merge of the two chats, the push name,
// a missing avatar.
//
// The push name, the "listed without a picture" check and the picture fetch
// are done here. Filing the pair (socket registration, identity heal) goes
// through IUsyncAliasHost; merging the two chats goes through ChatListAliasMerge.
// What this class owns is everything that decides: which numbers to ask
// about, the gate that keeps usync from competing with itself, and the plan.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    internal sealed class ContactUsyncResolver
    {
        private static readonly TimeSpan SingleTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(15);

        private readonly IUsyncAliasHost _aliases;
        private readonly ContactDirectory _directory;
        private readonly IUsyncGate _gate;
        private readonly IJidResolver _jids;
        private readonly DisplayNameTable _pushNames;
        private readonly ContactLabelSanitizer _labels;
        private readonly IChatStateStore _chats;
        private readonly AvatarFetcher _avatars;
        private readonly IDispatcher _ui;
        private readonly ChatNameRefresh _nameRefresh;
        private readonly ChatListAliasMerge _aliasMerge;

        internal ContactUsyncResolver(
            IUsyncAliasHost aliases,
            ContactDirectory directory,
            IUsyncGate gate,
            IJidResolver jids,
            DisplayNameTable pushNames,
            ContactLabelSanitizer labels,
            IChatStateStore chats,
            AvatarFetcher avatars,
            IDispatcher ui,
            ChatNameRefresh nameRefresh,
            ChatListAliasMerge aliasMerge)
        {
            _avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _nameRefresh = nameRefresh ?? throw new ArgumentNullException(nameof(nameRefresh));
            _aliasMerge = aliasMerge ?? throw new ArgumentNullException(nameof(aliasMerge));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _pushNames = pushNames ?? throw new ArgumentNullException(nameof(pushNames));
            _labels = labels ?? throw new ArgumentNullException(nameof(labels));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
        }

        public async Task ResolveAsync(IEnumerable<string> jids)
        {
            List<string> numbers = PickLookupNumbers(jids);
            if (numbers.Count == 0)
            {
                return;
            }

            try
            {
                using (await _gate.AcquireAsync().ConfigureAwait(false))
                {
                    var session = _directory.GetReadySession();
                    if (session == null)
                    {
                        Debug.WriteLine("[ContactUsyncResolver] Skipped (session not connected)");
                        return;
                    }

                    var useCase = new ResolveContactNamesUseCase(session.Connection);
                    TimeSpan timeout = numbers.Count > 1 ? BatchTimeout : SingleTimeout;
                    var contacts = await useCase.ExecuteAsync(numbers, "interactive", timeout).ConfigureAwait(false);

                    bool cachesChanged = false;
                    foreach (var contact in contacts)
                    {
                        if (Apply(contact))
                        {
                            cachesChanged = true;
                        }
                    }

                    if (cachesChanged)
                    {
                        await _nameRefresh.ApplyAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ContactUsyncResolver] Resolve failed: " + ex);
                try
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "contacts",
                        "resolve-contacts-failed",
                        ex,
                        "count=" + numbers.Count);
                }
                catch
                {
                }
            }
        }

        /// <summary>This endpoint answers by phone number; a LID with no known pair has none to ask with.</summary>
        private List<string> PickLookupNumbers(IEnumerable<string> jids)
        {
            var numbers = new List<string>();
            if (jids == null)
            {
                return numbers;
            }

            string selfJid = _jids.Self?.Id;
            foreach (string jid in jids)
            {
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                ContactLookupNumber number = ContactLookupNumber.For(jid, _jids.GetCanonicalJid(jid), selfJid);
                if (!number.IsUsable)
                {
                    Debug.WriteLine("[ContactUsyncResolver] Skipping " + jid + " (" + number.Skip + ")");
                    continue;
                }

                numbers.Add(number.Number);
            }

            return numbers;
        }

        /// <summary>Returns true when the alias or name caches changed and the list needs redrawing.</summary>
        private bool Apply(ResolvedContact contact)
        {
            if (contact == null || string.IsNullOrEmpty(contact.Jid))
            {
                return false;
            }

            string user = JidHelper.Normalize(contact.Jid);
            string lid = string.IsNullOrEmpty(contact.Lid) ? null : JidHelper.Normalize(contact.Lid);

            bool lidAlreadyAliased = false;
            bool pairAccepted = !string.IsNullOrEmpty(lid) &&
                _aliases.TryFileUsyncAlias(user, lid, out lidAlreadyAliased);
            bool pushNameStored = StorePushName(user, contact.Name);
            bool listedWithoutAvatar = !string.IsNullOrEmpty(contact.PictureId) &&
                UsyncContactCaches.HasListedChatWithoutAvatar(_chats.Chats, user);

            ResolvedContactApplyPlan plan = ResolvedContactApplyPlan.Decide(
                hasLid: !string.IsNullOrEmpty(lid),
                lidAlreadyAliased: lidAlreadyAliased,
                pairAccepted: pairAccepted,
                hasPushName: pushNameStored,
                listedChatHasNoAvatar: listedWithoutAvatar);

            if (plan.MergeDuplicateChats)
            {
                _ = _aliasMerge.MergeAsync(lid, user);
            }

            // Not awaited: the picture fetch queues behind the same usync gate this pass is holding.
            if (plan.FetchAvatar)
            {
                _ = FetchAvatarAsync(contact.Jid);
            }

            return plan.CachesChanged;
        }

        /// <summary>The usync answer only says a picture exists; the URL is a separate round trip.</summary>
        private async Task FetchAvatarAsync(string jid)
        {
            string url = await _avatars.FetchAndCacheAsync(jid, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            await _ui.RunAsync(() => ChatAvatarOutcome.RecordCached(
                UsyncContactCaches.FindListedChatWithoutAvatar(_chats.Chats, jid),
                url,
                nowUtc));
        }

        private bool StorePushName(string user, string rawName)
        {
            bool stored = UsyncContactCaches.StorePushName(_pushNames, _labels, user, rawName, out ContactLabelResult label);
            if (stored)
            {
                Debug.WriteLine("[ContactUsyncResolver] usync name resolved: " + user + " -> " + label.Label);
            }
            else if (label.Rejection != ContactLabelRejection.Empty)
            {
                Debug.WriteLine($"[ContactUsyncResolver] Ignoring usync name for {user}: {label.Rejection} '{label.Original}'");
            }

            return stored;
        }
    }
}
