namespace Unison.Core.Models
{
    /// <summary>
    /// Toast audio for incoming messages. Persisted as int under
    /// <see cref="Constants.LocalSettingsConstants.MessageNotificationSound"/> and
    /// <see cref="Constants.LocalSettingsConstants.GroupNotificationSound"/>.
    /// Never renumber existing members — installed devices already store the ints.
    /// </summary>
    public enum NotificationSound
    {
        /// <summary>Windows default toast sound (<c>Notification.Default</c>).</summary>
        SystemDefault = 0
    }
}
