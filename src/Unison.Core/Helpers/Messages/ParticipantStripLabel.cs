namespace Unison.Core.Helpers
{
    /// <summary>
    /// Last-resort author label for the group list strip, used when no name is known for the sender.
    /// </summary>
    /// <remarks>
    /// A phone number is a label a person recognises; a LID is not. The two are both long digit
    /// strings, so a fallback that just cut the JID's user part printed LID digits as if they were
    /// a number — which is how the list ended up showing an identity nobody could place. Callers
    /// that can map LID → PN pass the canonical JID and get the number; the ones that cannot get
    /// nothing, and the strip is composed without an author until a name (or the alias) arrives.
    /// </remarks>
    public static class ParticipantStripLabel
    {
        /// <summary>
        /// Phone digits for <paramref name="participantJid"/>, or empty when only a LID is known.
        /// </summary>
        /// <param name="canonicalParticipantJid">
        /// Same participant after LID → PN resolution, when the caller has a resolver.
        /// </param>
        public static string ForParticipant(string participantJid, string canonicalParticipantJid = null)
        {
            string phone = JidHelper.TryPhoneFromJid(participantJid)
                           ?? JidHelper.TryPhoneFromJid(canonicalParticipantJid);

            return string.IsNullOrWhiteSpace(phone) ? string.Empty : phone;
        }
    }
}
