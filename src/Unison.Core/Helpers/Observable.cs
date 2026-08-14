using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Unison.Core.Helpers
{
    public class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>Notifies several bindable names in one call (keeps ViewModel handlers readable).</summary>
        protected void RaiseProperties(params string[] propertyNames)
        {
            if (propertyNames == null || propertyNames.Length == 0)
            {
                return;
            }

            for (int i = 0; i < propertyNames.Length; i++)
            {
                OnPropertyChanged(propertyNames[i]);
            }
        }
    }
}
