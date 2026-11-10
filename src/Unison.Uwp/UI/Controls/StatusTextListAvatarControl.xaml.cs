using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Status list face for text posts: solid background color + body scaled with a Viewbox.
    /// </summary>
    public sealed partial class StatusTextListAvatarControl : UserControl
    {
        private const uint DefaultBackgroundArgb = 0xFF128C7E;
        private const uint DefaultTextArgb = 0xFFFFFFFF;

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register(
                nameof(Size),
                typeof(double),
                typeof(StatusTextListAvatarControl),
                new PropertyMetadata(48d, OnVisualChanged));

        public static readonly DependencyProperty BackgroundArgbProperty =
            DependencyProperty.Register(
                nameof(BackgroundArgb),
                typeof(uint),
                typeof(StatusTextListAvatarControl),
                new PropertyMetadata(0u, OnVisualChanged));

        public static readonly DependencyProperty TextArgbProperty =
            DependencyProperty.Register(
                nameof(TextArgb),
                typeof(uint),
                typeof(StatusTextListAvatarControl),
                new PropertyMetadata(0u, OnVisualChanged));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(StatusTextListAvatarControl),
                new PropertyMetadata(null, OnVisualChanged));

        public StatusTextListAvatarControl()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyVisual();
        }

        public double Size
        {
            get { return (double)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        public uint BackgroundArgb
        {
            get { return (uint)GetValue(BackgroundArgbProperty); }
            set { SetValue(BackgroundArgbProperty, value); }
        }

        public uint TextArgb
        {
            get { return (uint)GetValue(TextArgbProperty); }
            set { SetValue(TextArgbProperty, value); }
        }

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as StatusTextListAvatarControl;
            if (control != null)
            {
                control.ApplyVisual();
            }
        }

        private void ApplyVisual()
        {
            if (RootGrid == null || BackgroundPlate == null || PreviewTextBlock == null)
            {
                return;
            }

            double size = Size > 0 ? Size : 48d;
            RootGrid.Width = size;
            RootGrid.Height = size;
            BackgroundPlate.Width = size;
            BackgroundPlate.Height = size;

            uint background = BackgroundArgb != 0 ? BackgroundArgb : DefaultBackgroundArgb;
            uint foreground = TextArgb != 0 ? TextArgb : DefaultTextArgb;
            BackgroundPlate.Fill = new SolidColorBrush(ToColor(background));
            PreviewTextBlock.Foreground = new SolidColorBrush(ToColor(foreground));
            PreviewTextBlock.Text = Text ?? string.Empty;
        }

        private static Color ToColor(uint argb)
        {
            return Color.FromArgb(
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb);
        }
    }
}
