// =============================================================================
// PersistAuthStateOwner
//
// Owns IPersistAuthStateHost. Auth state and its store stay on the host
// (connection lifecycle); this owner only routes the save.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class PersistAuthStateOwner : IPersistAuthStateHost
    {
        private readonly Func<object, object> _resolveAuthToSave;
        private readonly Func<object, Task> _assignAndSaveAuthAsync;
        private readonly Action<string> _log;

        public PersistAuthStateOwner(
            Func<object, object> resolveAuthToSave,
            Func<object, Task> assignAndSaveAuthAsync,
            Action<string> log)
        {
            _resolveAuthToSave = resolveAuthToSave
                ?? throw new ArgumentNullException(nameof(resolveAuthToSave));
            _assignAndSaveAuthAsync = assignAndSaveAuthAsync
                ?? throw new ArgumentNullException(nameof(assignAndSaveAuthAsync));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task RunAsync(object sourceSocket, string reason) =>
            PersistAuthStateDispatch.RunAsync(this, sourceSocket, reason);

        object IPersistAuthStateHost.ResolveAuthToSave(object sourceSocket) => _resolveAuthToSave(sourceSocket);

        void IPersistAuthStateHost.LogSkipAuthStateSave(string reason) =>
            _log("Skipping auth-state save for " + reason + ": no live auth state");

        Task IPersistAuthStateHost.AssignAndSaveAuthAsync(object auth) => _assignAndSaveAuthAsync(auth);
    }
}
