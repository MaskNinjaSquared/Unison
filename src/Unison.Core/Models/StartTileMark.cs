namespace Unison.Core.Models
{
    /// <summary>
    /// Glyph pinned to Start as the app tile. Stored as an int in local settings.
    /// Unknown values fall back to <see cref="Unison"/>.
    /// </summary>
    public enum StartTileMark
    {
        Unison = 0,
        WhatsAppWeb = 1,
        WhatsAppLegacy = 2
    }
}
