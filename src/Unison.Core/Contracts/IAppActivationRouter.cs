namespace Unison.Core.Contracts
{
    /// <summary>
    /// Routes inbound app activations (Start tile, toast, People) into shell navigation intents.
    /// Platform activation kinds stay in the UWP host; this contract speaks only in arguments / remote ids.
    /// </summary>
    public interface IAppActivationRouter
    {
        /// <summary>
        /// Secondary tile / toast / boot launch arguments (<c>chat=</c> or <c>profile=</c>).
        /// </summary>
        /// <param name="appWasAlreadyOpen">
        /// True when the process was Running or Suspended — skips the cold-start chat-list hold.
        /// </param>
        void RouteLaunchArguments(string arguments, bool appWasAlreadyOpen);

        /// <summary>People Message (<c>ms-ipmessaging</c>) — open the conversation.</summary>
        void RoutePeopleMessage(string remoteId, bool appWasAlreadyOpen);

        /// <summary>People Profile protocol (<c>ms-contact-profile</c>) — full-page Profile.</summary>
        void RoutePeopleProfile(string remoteId, bool appWasAlreadyOpen);

        /// <summary>People ContactPanel / ContactProfile host — full-page Profile.</summary>
        void RouteContactPanel(string remoteId, bool appWasAlreadyOpen);

        /// <summary>
        /// Reads the first <c>ContactRemoteIds</c> value from a People protocol URI query
        /// (e.g. <c>?ContactRemoteIds=w…</c>). Returns null when missing.
        /// </summary>
        string TryReadPeopleRemoteId(string uriQuery);

        /// <summary>
        /// Builds <c>chat=</c> / <c>profile=</c> launch arguments from a People remote id
        /// (empty when the id cannot be mapped to a JID).
        /// </summary>
        string BuildPeopleLaunchArguments(string remoteId, bool openProfile);
    }
}
