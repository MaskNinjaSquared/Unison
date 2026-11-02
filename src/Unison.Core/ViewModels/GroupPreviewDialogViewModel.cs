// =============================================================================
// GroupPreviewDialogViewModel
//
// Generic preview before joining a group (community can-join and future invite).
// =============================================================================
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Helpers;

namespace Unison.Core.ViewModels
{
    public sealed class GroupPreviewDialogArgs
    {
        public string Jid { get; set; }

        public string Title { get; set; }

        public string AvatarUrl { get; set; }

        public string CreatorLabel { get; set; }

        public long CreationUnixSeconds { get; set; }

        public int? MemberCount { get; set; }

        public string SubtitleTemplate { get; set; }

        public string MemberCountTemplate { get; set; }

        public string PrimaryButtonText { get; set; }

        public string CancelButtonText { get; set; }

        /// <summary>Returns true when join succeeded (dialog closes).</summary>
        public Func<Task<bool>> PrimaryAction { get; set; }
    }

    public sealed class GroupPreviewDialogViewModel : Observable
    {
        private readonly GroupPreviewDialogArgs _args;
        private bool _isBusy;
        private bool _joined;

        public GroupPreviewDialogViewModel(GroupPreviewDialogArgs args)
        {
            _args = args ?? throw new ArgumentNullException(nameof(args));
            PrimaryCommand = new RelayCommand(() => _ = ExecutePrimaryAsync(), () => !IsBusy);
            CancelCommand = new RelayCommand(() => { });
        }

        public string Jid => _args.Jid;

        public string Title =>
            string.IsNullOrWhiteSpace(_args.Title) ? (_args.Jid ?? string.Empty) : _args.Title;

        public string AvatarUrl => _args.AvatarUrl;

        public string Subtitle
        {
            get
            {
                string template = string.IsNullOrWhiteSpace(_args.SubtitleTemplate)
                    ? "Group created by {0} on {1}"
                    : _args.SubtitleTemplate;
                string creator = string.IsNullOrWhiteSpace(_args.CreatorLabel)
                    ? "—"
                    : _args.CreatorLabel.Trim();
                string date = GroupCreatedBySelf.FormatShortLocalDate(_args.CreationUnixSeconds)
                    ?? "—";
                try
                {
                    return string.Format(template, creator, date);
                }
                catch (FormatException)
                {
                    return creator + " · " + date;
                }
            }
        }

        public string MemberCountText
        {
            get
            {
                if (_args.MemberCount == null || _args.MemberCount.Value <= 0)
                {
                    return string.Empty;
                }

                string template = string.IsNullOrWhiteSpace(_args.MemberCountTemplate)
                    ? "{0} members"
                    : _args.MemberCountTemplate;
                try
                {
                    return string.Format(template, _args.MemberCount.Value);
                }
                catch (FormatException)
                {
                    return _args.MemberCount.Value.ToString();
                }
            }
        }

        public bool HasMemberCount => !string.IsNullOrWhiteSpace(MemberCountText);

        public string PrimaryButtonText =>
            string.IsNullOrWhiteSpace(_args.PrimaryButtonText) ? "Join" : _args.PrimaryButtonText;

        public string CancelButtonText =>
            string.IsNullOrWhiteSpace(_args.CancelButtonText) ? "Cancel" : _args.CancelButtonText;

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    (PrimaryCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>True after a successful primary action (ContentDialog can close as Primary).</summary>
        public bool Joined => _joined;

        public ICommand PrimaryCommand { get; }

        public ICommand CancelCommand { get; }

        /// <summary>Runs the join action; used by the ContentDialog primary click deferral.</summary>
        public async Task<bool> ExecutePrimaryAsync()
        {
            if (_args.PrimaryAction == null || IsBusy)
            {
                return _joined;
            }

            IsBusy = true;
            try
            {
                _joined = await _args.PrimaryAction().ConfigureAwait(false);
            }
            catch
            {
                _joined = false;
            }
            finally
            {
                IsBusy = false;
            }

            return _joined;
        }
    }
}
