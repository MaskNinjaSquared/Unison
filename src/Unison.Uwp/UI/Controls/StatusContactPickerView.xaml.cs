using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>Status privacy contact multi-select (Unison / WhatsApp chrome).</summary>
    public sealed partial class StatusContactPickerView : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register(
                nameof(ViewModel),
                typeof(StatusComposerViewModel),
                typeof(StatusContactPickerView),
                new PropertyMetadata(null, OnViewModelChanged));

        private readonly StatusContactPickerHost _host;

        public StatusContactPickerView()
        {
            InitializeComponent();
            _host = new StatusContactPickerHost(PickerZoom, PickerList, PickerJumpList);
            Loaded += (s, e) => _host.Attach(ViewModel);
            Unloaded += (s, e) => _host.Detach();
        }

        public StatusComposerViewModel ViewModel
        {
            get => (StatusComposerViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        /// <summary>Consumes back while the A–Z jump grid is showing.</summary>
        public bool TryHandleZoomBack() => _host.TryHandleZoomBack();

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (StatusContactPickerView)d;
            view.LayoutRoot.DataContext = e.NewValue;
            view._host.Attach(e.NewValue as StatusComposerViewModel);
        }
    }
}
