namespace Unison.Core.Models
{
    /// <summary>
    /// Clock shown on message stamps after converting GMT 0 to the device time zone.
    /// Persisted as <see cref="Constants.LocalSettingsConstants.TimeFormat"/>.
    /// </summary>
    public enum TimeFormat
    {
        Hours24 = 0,
        Hours12 = 1
    }
}
