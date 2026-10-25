// =============================================================================
// AppStateLocalHost
//
// Default IAppStateLocalHost — durable forget I/O only (name historical).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class AppStateLocalHost : IAppStateLocalHost
    {
        private readonly IForgetChatStorageHost _forget;

        public AppStateLocalHost(IForgetChatStorageHost forget)
        {
            _forget = forget ?? throw new ArgumentNullException(nameof(forget));
        }

        /// <inheritdoc />
        public Task ForgetChatStorageAsync(
            string canonicalJid,
            IReadOnlyList<string> storeKeys) =>
            ForgetChatStorageDispatch.RunAsync(_forget, canonicalJid, storeKeys);
    }
}
