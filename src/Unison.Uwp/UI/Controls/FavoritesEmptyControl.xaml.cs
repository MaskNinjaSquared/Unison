using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>Empty Favorites body shared by shell pages and the Legacy pivot.</summary>
    public sealed partial class FavoritesEmptyControl : UserControl
    {
        public static readonly DependencyProperty AddCommandProperty =
            DependencyProperty.Register(
                nameof(AddCommand),
                typeof(ICommand),
                typeof(FavoritesEmptyControl),
                new PropertyMetadata(null));

        public FavoritesEmptyControl()
        {
            InitializeComponent();
        }

        public ICommand AddCommand
        {
            get => (ICommand)GetValue(AddCommandProperty);
            set => SetValue(AddCommandProperty, value);
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddCommand != null && AddCommand.CanExecute(null))
            {
                AddCommand.Execute(null);
            }
        }
    }
}
