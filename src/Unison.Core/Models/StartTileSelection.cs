namespace Unison.Core.Models
{
    /// <summary>Start-tile picker result: glyph plus whether the plate is transparent.</summary>
    public sealed class StartTileSelection
    {
        public StartTileMark Mark { get; set; }

        public bool Transparent { get; set; }
    }
}
