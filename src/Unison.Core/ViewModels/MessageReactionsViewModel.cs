using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Backs the reactions dialog for one bubble: the tally chips and who reacted, with identity
    /// resolved from <see cref="IPersonStore"/> rather than from the reaction envelope alone.
    /// </summary>
    public sealed class MessageReactionsViewModel : Observable
    {
        private readonly IPersonStore _people;
        private readonly IHistoryMessageStore _historyMessages;
        private readonly IStringResources _strings;
        private readonly IWhatsAppService _whatsApp;

        private string _title;
        private bool _isLoading;

        public MessageReactionsViewModel(
            IPersonStore people,
            IHistoryMessageStore historyMessages = null,
            IStringResources strings = null,
            IWhatsAppService whatsApp = null)
        {
            _people = people ?? throw new ArgumentNullException(nameof(people));
            _historyMessages = historyMessages;
            _strings = strings;
            _whatsApp = whatsApp;
        }

        /// <summary>"1 reaction" / "{0} reactions".</summary>
        public string Title
        {
            get => _title;
            private set => Set(ref _title, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        /// <summary>One chip per distinct emoji, with its tally.</summary>
        public ObservableCollection<ReactionChip> Chips { get; } =
            new ObservableCollection<ReactionChip>();

        public ObservableCollection<ReactionAuthorItem> Authors { get; } =
            new ObservableCollection<ReactionAuthorItem>();

        /// <summary>
        /// Fills the dialog for <paramref name="bubble"/>. Rows land in reaction order (newest last)
        /// and each identity is read once, so a reactor who also reacted with another emoji does not
        /// re-query the store.
        /// </summary>
        public async Task LoadAsync(ChatMessageViewModel bubble)
        {
            Chips.Clear();
            Authors.Clear();

            ChatMessage model = bubble?.Model;
            if (model == null || !model.HasReactions)
            {
                Title = BuildTitle(0);
                return;
            }

            IsLoading = true;
            try
            {
                List<MessageReaction> reactions = await EnsureReactionDetailsAsync(model).ConfigureAwait(true);
                Title = BuildTitle(reactions == null ? 0 : reactions.Count);
                if (reactions == null || reactions.Count == 0)
                {
                    return;
                }

                foreach (ReactionChip chip in ReactionsBuilder.BuildChips(reactions))
                {
                    Chips.Add(chip);
                }

                var resolved = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
                foreach (MessageReaction reaction in reactions)
                {
                    Authors.Add(await BuildAuthorAsync(reaction, resolved).ConfigureAwait(true));
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task<List<MessageReaction>> EnsureReactionDetailsAsync(ChatMessage model)
        {
            if (model.AreReactionDetailsLoaded && model.Reactions.Count > 0)
            {
                return model.Reactions
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Emoji))
                    .OrderBy(r => r.Timestamp)
                    .ToList();
            }

            if (_historyMessages == null ||
                string.IsNullOrWhiteSpace(model.RemoteJid) ||
                string.IsNullOrWhiteSpace(model.Id))
            {
                return model.Reactions
                    ?.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Emoji))
                    .OrderBy(r => r.Timestamp)
                    .ToList()
                    ?? new List<MessageReaction>();
            }

            IReadOnlyList<HistoryMessageReaction> rows =
                await _historyMessages.GetReactionsForMessageAsync(model.RemoteJid, model.Id)
                    .ConfigureAwait(true);

            var list = new List<MessageReaction>(rows?.Count ?? 0);
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    HistoryMessageReaction row = rows[i];
                    if (row == null || string.IsNullOrWhiteSpace(row.Emoji))
                    {
                        continue;
                    }

                    list.Add(new MessageReaction
                    {
                        Emoji = row.Emoji,
                        ReactorJid = row.ReactorJid,
                        ReactorName = row.ReactorName,
                        Timestamp = row.TimestampUtc,
                        ReactionMessageId = row.ReactionMessageId,
                        FromMe = row.FromMe
                    });
                }
            }

            model.ApplyReactionDetails(list);
            return list
                .OrderBy(r => r.Timestamp)
                .ToList();
        }

        private string BuildTitle(int total)
        {
            if (total == 1)
            {
                return Get("Reactions_TitleOne", "1 reaction");
            }

            return string.Format(Get("Reactions_TitleMany", "{0} reactions"), total);
        }

        private async Task<ReactionAuthorItem> BuildAuthorAsync(
            MessageReaction reaction,
            IDictionary<string, Person> resolved)
        {
            string jid = reaction.ReactorJid;
            string canonical = ResolveCanonical(jid);

            Person person;
            if (!resolved.TryGetValue(canonical ?? string.Empty, out person))
            {
                person = await LoadPersonAsync(canonical, jid).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(canonical))
                {
                    resolved[canonical] = person;
                }
            }

            return new ReactionAuthorItem
            {
                Jid = canonical,
                DisplayName = ResolveName(person, reaction, canonical),
                Phone = ResolvePhone(person, canonical, jid),
                AvatarUrl = person?.AvatarUrl,
                Emoji = reaction.Emoji.Trim()
            };
        }

        private string ResolveCanonical(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string canonical = _whatsApp?.GetCanonicalJid(jid);
            return string.IsNullOrWhiteSpace(canonical) ? JidHelper.Normalize(jid) : canonical;
        }

        private async Task<Person> LoadPersonAsync(string canonical, string rawJid)
        {
            Person person = await GetPersonAsync(canonical).ConfigureAwait(true);
            if (person != null)
            {
                return person;
            }

            string normalized = JidHelper.Normalize(rawJid);
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.Equals(normalized, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return await GetPersonAsync(normalized).ConfigureAwait(true);
        }

        private async Task<Person> GetPersonAsync(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            try
            {
                return await _people.GetAsync(jid).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[MessageReactionsViewModel] Person lookup failed for " + jid + ": " + ex.Message);
                return null;
            }
        }

        private string ResolveName(Person person, MessageReaction reaction, string canonical)
        {
            if (person != null && !string.IsNullOrWhiteSpace(person.Name))
            {
                return person.Name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(reaction.ReactorName))
            {
                return reaction.ReactorName.Trim();
            }

            if (reaction.FromMe)
            {
                return Get("Chat_SelfFallbackName", "You");
            }

            string phone = JidHelper.TryPhoneFromJid(canonical);
            if (!string.IsNullOrWhiteSpace(phone))
            {
                return "+" + phone;
            }

            return BareUser(canonical);
        }

        private string ResolvePhone(Person person, string canonical, string rawJid)
        {
            string digits = person == null
                ? null
                : PhoneNumberHelper.NormalizePhoneDigits(person.Phone);

            if (string.IsNullOrWhiteSpace(digits))
            {
                digits = JidHelper.TryPhoneFromJid(canonical) ?? JidHelper.TryPhoneFromJid(rawJid);
            }

            if (string.IsNullOrWhiteSpace(digits))
            {
                return null;
            }

            bool named = person != null && !string.IsNullOrWhiteSpace(person.Name);
            return named ? "+" + digits : null;
        }

        private static string BareUser(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return string.Empty;
            }

            int at = jid.IndexOf('@');
            return at > 0 ? jid.Substring(0, at) : jid;
        }

        private string Get(string key, string fallback)
        {
            return _strings != null ? _strings.Get(key, fallback) : fallback;
        }
    }
}
