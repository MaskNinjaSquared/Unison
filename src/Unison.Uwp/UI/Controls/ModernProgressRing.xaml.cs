using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Thin wrapper around the native <see cref="ProgressRing"/> with an
    /// <see cref="IsActive"/> DP bound in XAML (<c>x:Bind</c>).
    /// </summary>
    public sealed partial class ModernProgressRing : UserControl
    {
        public ModernProgressRing()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(
                nameof(IsActive),
                typeof(bool),
                typeof(ModernProgressRing),
                new PropertyMetadata(false));

        public bool IsActive
        {
            get { return (bool)GetValue(IsActiveProperty); }
            set { SetValue(IsActiveProperty, value); }
        }
    }
}
