using System.Threading.Tasks;

namespace Unison.Baileys.Client
{
    /// <summary>
    /// Abstracts auth-state persistence so protocol code does not depend on
    /// the UWP-specific AuthStore implementation.
    /// </summary>
    public interface IAuthPersistence
    {
        Task SaveAsync(AuthState auth);
    }
}
