// =============================================================================
// AuthStateWireOwner
//
// Owns IAuthStateWireHost. Alias file / auth persist stay as ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class AuthStateWireOwner : IAuthStateWireHost
    {
        private readonly Action _logUpdated;
        private readonly Action<string> _fileSelfAliases;
        private readonly Func<Task> _persistAuthAsync;

        public AuthStateWireOwner(
            Action logUpdated,
            Action<string> fileSelfAliases,
            Func<Task> persistAuthAsync)
        {
            _logUpdated = logUpdated ?? throw new ArgumentNullException(nameof(logUpdated));
            _fileSelfAliases = fileSelfAliases
                ?? throw new ArgumentNullException(nameof(fileSelfAliases));
            _persistAuthAsync = persistAuthAsync
                ?? throw new ArgumentNullException(nameof(persistAuthAsync));
        }

        public Task RunAsync() => AuthStateWireDispatch.RunAsync(this);

        void IAuthStateWireHost.LogAuthStateUpdated() => _logUpdated();

        void IAuthStateWireHost.FileSelfIdentityAliases(string source) =>
            _fileSelfAliases(source);

        Task IAuthStateWireHost.PersistAuthStateFromWireAsync() => _persistAuthAsync();
    }
}