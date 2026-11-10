using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public interface IUriLauncher
    {
        Task<bool> LaunchAsync(string url);

        /// <summary>
        /// Opens a URI that expects a result (e.g. People <c>ms-people:savecontacttask</c>).
        /// Prefer this over <see cref="LaunchAsync"/> when the protocol docs require
        /// <c>LaunchUriForResultsAsync</c>; otherwise only the host app main page opens.
        /// </summary>
        Task<bool> LaunchForResultsAsync(string url);

        /// <summary>Opens a local/cached file (<c>ms-appdata</c> or path) with the default app.</summary>
        Task<bool> LaunchLocalFileAsync(string uriOrPath);
    }
}
