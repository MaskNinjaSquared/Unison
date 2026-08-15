namespace Unison.Core.Models
{
    /// <summary>
    /// Display chip for a grouped emoji reaction (count when several people used the same emoji).
    /// </summary>
    public sealed class ReactionChip
    {
        public string Emoji { get; set; }
        public int Count { get; set; }

        public string Label
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Emoji)) return string.Empty;
                return Count > 1 ? Emoji + " " + Count : Emoji;
            }
        }
    }
}
