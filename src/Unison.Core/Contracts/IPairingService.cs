using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Phone / QR companion pairing surface used by Login.
    /// </summary>
    public interface IPairingService
    {
        event EventHandler<string> OnPairingCode;
        event EventHandler<string> OnPairingFailed;

        Task<string> RequestPairingCodeAsync(string phoneNumber, string customCode = null);
    }
}
