using System.Windows.Input;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// One option in the chat attachment bar: a fixed 150x150 accent tile with a glyph over a
    /// label, at the size Whatsapp used on Windows Phone 8.
    /// Optional <see cref="LocalizationUid"/> loads <c>{uid}.Text</c> into <see cref="Label"/>.
    /// </summary>
    public sealed partial class AttachTile : UserControl
    {
        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(
                nameof(Glyph),
                typeof(ImageSource),
                typeof(AttachTile),
                new PropertyMetadata(null));

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(
                nameof(Label),
                typeof(string),
                typeof(AttachTile),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty LocalizationUidProperty =
            DependencyProperty.Register(
                nameof(LocalizationUid),
                typeof(string),
                typeof(AttachTile),
                new PropertyMetadata(null));

        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(
                nameof(Command),
                typeof(ICommand),
                typeof(AttachTile),
                new PropertyMetadata(null));

        public AttachTile()
        {
            this.InitializeComponent();
            this.Loaded += AttachTile_Loaded;
        }

        /// <summary>White-on-transparent artwork, drawn at 54px.</summary>
        public ImageSource Glyph
        {
            get => (ImageSource)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        /// <summary>Caption fallback text when localization is missing.</summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        /// <summary>Resource key prefix; loads <c>{LocalizationUid}.Text</c>.</summary>
        public string LocalizationUid
        {
            get => (string)GetValue(LocalizationUidProperty);
            set => SetValue(LocalizationUidProperty, value);
        }

        /// <summary>
        /// What the tile does. Also what decides whether it can be pressed at all: an option with
        /// no route behind it supplies a command whose CanExecute is false, and greys out.
        /// </summary>
        public ICommand Command
        {
            get => (ICommand)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        /// <summary>
        /// The tile was pressed. Raised alongside the command, for the host that needs to know a
        /// choice was made without caring which - putting the bar away, in practice.
        /// </summary>
        public event RoutedEventHandler Invoked;

        private void AttachTile_Loaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(LocalizationUid))
            {
                return;
            }

            try
            {
                string text = LocalizedStrings.Get(LocalizationUid + ".Text", null);
                if (!string.IsNullOrEmpty(text))
                {
                    Label = text;
                }
            }
            catch
            {
            }
        }

        private void Tile_Click(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, e);

        /// <summary>
        /// Keeps the tile square off whatever width its column hands it. Here rather than in the
        /// host because there is no way to say "as tall as I am wide" in markup, and the host
        /// should not have to know that.
        /// </summary>
        private void AttachTile_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0)
            {
                Height = e.NewSize.Width;
            }
        }
    }
}
