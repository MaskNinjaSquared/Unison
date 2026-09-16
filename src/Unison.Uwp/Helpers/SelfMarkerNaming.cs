using Unison.Core.Contracts;

namespace Unison.Uwp.Helpers
{
    /// <summary>
    /// Supplies <see cref="SelfChatDisplayHelper"/>'s marker rules to Core, which cannot
    /// reach them directly because resolving the localized marker reads UWP resources.
    /// </summary>
    public sealed class SelfMarkerNaming : ISelfMarkerNaming
    {
        public bool IsMarkerLabel(string label) => SelfChatDisplayHelper.IsSelfMarkerLabel(label);

        public string StripMarker(string label) => SelfChatDisplayHelper.StripSelfMarker(label);
    }
}
