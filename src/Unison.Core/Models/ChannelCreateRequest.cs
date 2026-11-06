namespace Unison.Core.Models
{
    /// <summary>What the New Conversation channel form collected.</summary>
    public sealed class ChannelCreateRequest
    {
        public string Name { get; set; }

        public string Description { get; set; }

        /// <summary>Picked image as-is; the façade encodes it to a square JPEG.</summary>
        public byte[] PhotoBytes { get; set; }
    }
}
