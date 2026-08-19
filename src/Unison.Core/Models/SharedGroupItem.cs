namespace Unison.Core.Models
{
    /// <summary>
    /// One shared group row for person / group-member info UI (display only).
    /// </summary>
    public sealed class SharedGroupItem
    {
        public string Jid { get; set; }
        public string Name { get; set; }
        public string AvatarUrl { get; set; }
    }
}
