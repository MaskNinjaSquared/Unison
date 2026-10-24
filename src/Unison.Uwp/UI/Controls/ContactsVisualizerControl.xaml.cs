using System.Collections;
using System.Windows.Input;
using Unison.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Generic contact/chat picker overlay. Hosts pass a title, collection, and click command —
    /// the command closes over the ViewModel / façade, so favouriting (or any other action) runs
    /// without a NavigationService hop.
    /// </summary>
    public sealed partial class ContactsVisualizerControl : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(null));

        public static readonly DependencyProperty ItemClickCommandProperty =
            DependencyProperty.Register(
                nameof(ItemClickCommand),
                typeof(ICommand),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(null));

        public static readonly DependencyProperty BackCommandProperty =
            DependencyProperty.Register(
                nameof(BackCommand),
                typeof(ICommand),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register(
                nameof(IsOpen),
                typeof(bool),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(false, OnIsOpenChanged));

        public ContactsVisualizerControl()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public ICommand ItemClickCommand
        {
            get => (ICommand)GetValue(ItemClickCommandProperty);
            set => SetValue(ItemClickCommandProperty, value);
        }

        public ICommand BackCommand
        {
            get => (ICommand)GetValue(BackCommandProperty);
            set => SetValue(BackCommandProperty, value);
        }

        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ContactsVisualizerControl;
            if (control == null)
            {
                return;
            }

            control.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (BackCommand != null && BackCommand.CanExecute(null))
            {
                BackCommand.Execute(null);
                return;
            }

            IsOpen = false;
        }

        private void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatItem;
            if (chat == null || ItemClickCommand == null)
            {
                return;
            }

            if (ItemClickCommand.CanExecute(chat))
            {
                ItemClickCommand.Execute(chat);
            }
        }
    }
}
