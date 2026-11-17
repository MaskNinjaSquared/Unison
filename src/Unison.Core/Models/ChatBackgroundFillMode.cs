namespace Unison.Core.Models
{
    /// <summary>
    /// How a <see cref="ChatBackgroundKind"/> paints: stock presets tile; only Custom stretches.
    /// </summary>
    public enum ChatBackgroundFillMode
    {
        /// <summary>No image — theme underfill only (DefaultNoDoodles).</summary>
        None = 0,

        /// <summary>Repeat the asset at native aspect (Default doodles + Cubes/Owls/Damask).</summary>
        Tile = 1,

        /// <summary>One image UniformToFill (Custom wallpaper only).</summary>
        Stretch = 2
    }
}
