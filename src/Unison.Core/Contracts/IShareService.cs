using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Platform share sheet without WinRT types in Core.
    /// </summary>
    public interface IShareService
    {
        /// <summary>
        /// Opens the system share UI for a local file (<c>ms-appdata</c> / path).
        /// </summary>
        Task ShareLocalFileAsync(string title, string localUriOrPath);
    }
}
