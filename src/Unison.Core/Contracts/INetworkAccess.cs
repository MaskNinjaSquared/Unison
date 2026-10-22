namespace Unison.Core.Contracts
{
    /// <summary>
    /// Whether the host currently has a usable internet path. WinRT
    /// <c>NetworkInformation</c> stays in a UWP adapter — façades only ask this port.
    /// </summary>
    public interface INetworkAccess
    {
        bool HasInternetAccess { get; }
    }
}
