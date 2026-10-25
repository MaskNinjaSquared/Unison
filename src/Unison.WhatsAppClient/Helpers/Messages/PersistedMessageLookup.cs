// =============================================================================
// PersistedMessageLookup
//
// Owns IFindPersistedChatMessageHost + IResolveSentMessageForRetryHost.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class PersistedMessageLookup :
        IFindPersistedChatMessageHost,
        IResolveSentMessageForRetryHost
    {
        private readonly IHistoryMessageStore _history;
        private readonly IJidResolver _jids;

        public PersistedMessageLookup(IHistoryMessageStore history, IJidResolver jids)
        {
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
        }

        public Task<ChatMessage> FindAsync(string chatJid, string messageId) =>
            FindPersistedChatMessageDispatch.RunAsync(this, chatJid, messageId);

        public Task<global::Proto.Message> ResolveSentForRetryAsync(
            string remoteJid,
            string messageId) =>
            ResolveSentMessageForRetryDispatch.RunAsync(this, remoteJid, messageId);

        bool IFindPersistedChatMessageHost.HasHistoryMessageStore => true;

        IJidResolver IFindPersistedChatMessageHost.JidResolver => _jids;

        async Task<object> IFindPersistedChatMessageHost.GetHistoryMessageRowAsync(
            string storeKey,
            string messageId) =>
            await _history.GetAsync(storeKey, messageId).ConfigureAwait(false);

        ChatMessage IFindPersistedChatMessageHost.MapHistoryRowToChatMessage(object row) =>
            HistoryMessageMapper.ToChatMessage(row as HistoryMessage);

        bool IResolveSentMessageForRetryHost.HasMessageStore => true;

        string IResolveSentMessageForRetryHost.NormalizeJid(string remoteJid) =>
            JidHelper.Normalize(remoteJid);

        Task<ChatMessage> IResolveSentMessageForRetryHost.FindPersistedChatMessageAsync(
            string chatJid,
            string messageId) =>
            FindAsync(chatJid, messageId);

        void IResolveSentMessageForRetryHost.LogRetryLookupDeclined(
            string messageId,
            ChatMessageKind kind) =>
            Debug.WriteLine(
                "[PersistedMessageLookup] Retry lookup declined " + messageId + ": " + kind +
                " cannot be rebuilt");

        void IResolveSentMessageForRetryHost.LogRetryLookupFailed(
            string messageId,
            string message) =>
            Debug.WriteLine(
                "[PersistedMessageLookup] Retry lookup failed for " + messageId + ": " + message);
    }
}
