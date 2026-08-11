using System.Threading.Tasks;
using Unison.Baileys.Protocol;

namespace Unison.Baileys.Client
{
    /// <summary>
    /// Abstracts the socket transport layer for protocol handlers
    /// without depending on the UWP-specific SocketClient implementation.
    /// </summary>
    public interface ISocketHandle
    {
        AuthState Auth { get; }
        IKeyStore KeyStore { get; }
        string GenerateMessageTag();
        Task<BinaryNode> QueryAsync(BinaryNode node, int timeoutMs = 60000);
        Task SendNodeAsync(BinaryNode node);
    }
}
