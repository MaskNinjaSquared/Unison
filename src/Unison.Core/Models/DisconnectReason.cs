namespace Unison.Core.Models
{
    /// <summary>
    /// Baileys-aligned disconnect classification for <see cref="Contracts.WhatsApp.IConnectionService"/>.
    /// </summary>
    public enum DisconnectReason
    {
        Unknown = 0,
        Network = 1,
        RestartRequired = 2,
        LoggedOut = 3,
        ConnectionReplaced = 4,
        Forbidden = 5,
        BadSession = 6
    }
}
