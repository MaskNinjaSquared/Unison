using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public interface IUriLauncher
    {
        Task<bool> LaunchAsync(string url);
    }
}
