namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Layout payload for <see cref="SettingBox"/>'s <c>ContentControl</c>.
    /// Must not be the <see cref="SettingBox"/> itself — that would nest the host
    /// inside its own content and hang the UI thread (black screen after splash).
    /// </summary>
    public sealed class SettingBoxFace
    {
        public bool HasDescription { get; set; }

        public string Glyph { get; set; }

        public string Header { get; set; }

        public string Text { get; set; }

        public object Control { get; set; }
    }
}
