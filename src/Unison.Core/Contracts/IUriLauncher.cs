using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public interface IUriLauncher
    {
        Task<bool> LaunchAsync(string url);

        /// <summary>Opens a local/cached file (<c>ms-appdata</c> or path) with the default app.</summary>
        Task<bool> LaunchLocalFileAsync(string uriOrPath);
    }
}
