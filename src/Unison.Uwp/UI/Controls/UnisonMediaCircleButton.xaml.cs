using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Unison-only round media action (download / play). Instantiated only under the Unison shell.
    /// </summary>
    public sealed partial class UnisonMediaCircleButton : UserControl
    {
        public const double CompactDiameter = 30;
        public const double OverlayDiameter = 56;

        public static readonly DependencyProperty IsFromMeProperty =
            DependencyProperty.Register(
                nameof(IsFromMe),
                typeof(bool),
                typeof(UnisonMediaCircleButton),
                new PropertyMetadata(false, OnChromeChanged));

        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(
                nameof(Glyph),
                typeof(string),
                typeof(UnisonMediaCircleButton),
                new PropertyMetadata("\uE896", OnChromeChanged));

        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(
                nameof(Command),
                typeof(ICommand),
                typeof(UnisonMediaCircleButton),
                new PropertyMetadata(null, OnChromeChanged));

        public static readonly DependencyProperty DiameterProperty =
            DependencyProperty.Register(
                nameof(Diameter),
                typeof(double),
                typeof(UnisonMediaCircleButton),
                new PropertyMetadata(CompactDiameter, OnChromeChanged));

        public UnisonMediaCircleButton()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyChrome();
        }

        public bool IsFromMe
        {
            get => (bool)GetValue(IsFromMeProperty);
            set => SetValue(IsFromMeProperty, value);
        }

        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public ICommand Command
        {
            get => (ICommand)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        /// <summary>30 for audio/doc row; 56 matches video play overlay.</summary>
        public double Diameter
        {
            get => (double)GetValue(DiameterProperty);
            set => SetValue(DiameterProperty, value);
        }

        private static void OnChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as UnisonMediaCircleButton;
            control?.ApplyChrome();
        }

        private void ApplyChrome()
        {
            double diameter = Diameter > 0 ? Diameter : CompactDiameter;
            Width = diameter;
            Height = diameter;

            if (ActionButton != null)
            {
                ActionButton.Width = diameter;
                ActionButton.Height = diameter;
                ActionButton.FontSize = diameter >= OverlayDiameter - 0.5 ? 28 : 14;
                ActionButton.Content = Glyph ?? "\uE896";
                ActionButton.Command = Command;
            }

            VisualStateManager.GoToState(this, IsFromMe ? "Sent" : "Received", false);
        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            // Command handles invoke when bound.
        }
    }
}
