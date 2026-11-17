using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Resolves <see cref="ChatBackgroundKind"/> to display URIs. Custom paths stay relative
    /// under LocalFolder; the host turns them into ms-appdata or file paths.
    /// </summary>
    public static class ChatBackgroundCatalog
    {
        /// <summary>
        /// Classic Windows Phone 8 accent palette (+ a few chat-friendly neutrals) for the
        /// Default-family solid underfill picker.
        /// </summary>
        public static IReadOnlyList<(string Hex, string Title)> Wp8SolidColors { get; } =
            new List<(string, string)>
            {
                ("#FFA4C400", "Lime"),
                ("#FF60A917", "Green"),
                ("#FF008A00", "Emerald"),
                ("#FF00ABA9", "Teal"),
                ("#FF1BA1E2", "Cyan"),
                ("#FF0050EF", "Cobalt"),
                ("#FF6A00FF", "Indigo"),
                ("#FFAA00FF", "Violet"),
                ("#FFF472D0", "Pink"),
                ("#FFD80073", "Magenta"),
                ("#FFA20025", "Crimson"),
                ("#FFE51400", "Red"),
                ("#FFFA6800", "Orange"),
                ("#FFF0A30A", "Amber"),
                ("#FFD8C100", "Yellow"),
                ("#FF825A2C", "Brown"),
                ("#FF6D8764", "Olive"),
                ("#FF647687", "Steel"),
                ("#FF76608A", "Mauve"),
                ("#FF7A3B2E", "Sienna"),
                ("#FFDDE0DB", "Sand"),
                ("#FFEFEAE2", "Cream"),
                ("#FF424D4D", "Slate"),
                ("#FF2C2C2C", "Charcoal"),
            };

        public const string DefaultAssetUri =
            "ms-appx:///Assets/Backgrounds/WhatsAppBackground_Colored.png";

        public const string CubesAssetUri =
            "ms-appx:///Assets/Backgrounds/ChatBg_Cubes.jpg";

        public const string OwlsAssetUri =
            "ms-appx:///Assets/Backgrounds/ChatBg_Owls.png";

        public const string DamaskAssetUri =
            "ms-appx:///Assets/Backgrounds/ChatBg_Damask.jpg";

        /// <summary>Appx URI for a preset, or null for Default family / Custom.</summary>
        public static string TryGetPresetUri(ChatBackgroundKind kind)
        {
            switch (kind)
            {
                case ChatBackgroundKind.Cubes:
                    return CubesAssetUri;
                case ChatBackgroundKind.Owls:
                    return OwlsAssetUri;
                case ChatBackgroundKind.Damask:
                    return DamaskAssetUri;
                default:
                    return null;
            }
        }

        /// <summary>
        /// URI the UI should paint. Default → stock doodle asset (theme underfill is separate);
        /// <see cref="ChatBackgroundKind.DefaultNoDoodles"/> has no image (underfill only);
        /// Custom → <paramref name="customDisplayUri"/> from the host; else preset.
        /// </summary>
        public static string ResolveImageUri(ChatBackgroundKind kind, string customDisplayUri)
        {
            if (kind == ChatBackgroundKind.Custom)
            {
                return string.IsNullOrWhiteSpace(customDisplayUri) ? DefaultAssetUri : customDisplayUri;
            }

            if (kind == ChatBackgroundKind.Default)
            {
                return DefaultAssetUri;
            }

            if (kind == ChatBackgroundKind.DefaultNoDoodles)
            {
                return null;
            }

            return TryGetPresetUri(kind) ?? DefaultAssetUri;
        }

        public static bool IsPreset(ChatBackgroundKind kind)
        {
            return kind == ChatBackgroundKind.Cubes ||
                   kind == ChatBackgroundKind.Owls ||
                   kind == ChatBackgroundKind.Damask;
        }

        /// <summary>Default or DefaultNoDoodles — doodles toggle applies only here.</summary>
        public static bool IsDefaultFamily(ChatBackgroundKind kind)
        {
            return kind == ChatBackgroundKind.Default ||
                   kind == ChatBackgroundKind.DefaultNoDoodles;
        }

        /// <summary>True when the WhatsApp doodle tiles should paint.</summary>
        public static bool UsesWhatsAppDoodles(ChatBackgroundKind kind)
        {
            return kind == ChatBackgroundKind.Default;
        }

        /// <summary>
        /// Paint mode for <paramref name="kind"/>. The four stock wallpapers (Default doodles,
        /// Cubes, Owls, Damask) always <see cref="ChatBackgroundFillMode.Tile"/>; only Custom
        /// stretches to fill.
        /// </summary>
        public static ChatBackgroundFillMode GetFillMode(ChatBackgroundKind kind)
        {
            switch (kind)
            {
                case ChatBackgroundKind.DefaultNoDoodles:
                    return ChatBackgroundFillMode.None;
                case ChatBackgroundKind.Custom:
                    return ChatBackgroundFillMode.Stretch;
                case ChatBackgroundKind.Default:
                case ChatBackgroundKind.Cubes:
                case ChatBackgroundKind.Owls:
                case ChatBackgroundKind.Damask:
                    return ChatBackgroundFillMode.Tile;
                default:
                    return ChatBackgroundFillMode.Tile;
            }
        }
    }
}
