using System.Windows.Input;
using Unison.Core.Helpers;

namespace Unison.Core.ViewModels
{
    /// <summary>Welcome / Get started — only when logged out, before Login/QR.</summary>
    public class StartViewModel : Observable
    {
        private readonly ShellViewModel _shell;

        public StartViewModel(ShellViewModel shell)
        {
            _shell = shell;
            GetStartedCommand = new RelayCommand(GetStarted);
        }

        public ICommand GetStartedCommand { get; }

        public string AppVersion { get; set; }

        private void GetStarted()
        {
            _shell.EnterLoginSurface(startPairing: true);
        }
    }
}
