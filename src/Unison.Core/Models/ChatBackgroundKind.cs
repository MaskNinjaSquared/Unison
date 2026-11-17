namespace Unison.Core.Models
{
    /// <summary>
    /// Per-chat wallpaper. Fill mode is <see cref="ChatBackgroundFillMode"/> via
    /// <c>ChatBackgroundCatalog.GetFillMode</c>: Default/Cubes/Owls/Damask tile; Custom stretches;
    /// <see cref="DefaultNoDoodles"/> is underfill only. Stored as int on <c>ChatRow.BackgroundKind</c>.
    /// </summary>
    public enum ChatBackgroundKind
    {
        Default = 0,
        Cubes = 1,
        Owls = 2,
        Damask = 3,
        /// <summary>Default family without doodle tiles — only the theme underfill.</summary>
        DefaultNoDoodles = 4,
        Custom = 100
    }
}
