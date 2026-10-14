using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Pins one Start tile that stands in for the app tile and receives the same
    /// badge and live-tile updates as the primary tile.
    /// </summary>
    public interface IStartTilePinService
    {
        Task<bool> PinAsync(StartTileMark mark, bool transparent = false);
    }
}
