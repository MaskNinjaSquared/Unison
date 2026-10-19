using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// The @ menu above the composer. The view model owns the rows.
    /// </summary>
    public sealed partial class MentionSuggestList : UserControl
    {
        public MentionSuggestList()
        {
            InitializeComponent();
            Loaded += delegate { ApplySelection(); };
            Rows.Loaded += delegate { ApplySelection(); };
        }

        public event ItemClickEventHandler ItemClick;

        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(
                nameof(SelectedIndex),
                typeof(int),
                typeof(MentionSuggestList),
                new PropertyMetadata(-1, OnSelectedIndexChanged));

        public int SelectedIndex
        {
            get { return (int)GetValue(SelectedIndexProperty); }
            set { SetValue(SelectedIndexProperty, value); }
        }

        private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var list = d as MentionSuggestList;
            if (list == null || list.Rows == null)
            {
                return;
            }

            list.ApplySelection();
        }

        private void ApplySelection()
        {
            if (Rows == null || Rows.Items == null)
            {
                return;
            }

            int index = SelectedIndex;
            if (index < 0 || index >= Rows.Items.Count)
            {
                return;
            }

            Rows.SelectedIndex = index;
            Rows.ScrollIntoView(Rows.Items[index]);
        }

        private void Rows_ItemClick(object sender, ItemClickEventArgs e)
        {
            ItemClick?.Invoke(this, e);
        }
    }
}
