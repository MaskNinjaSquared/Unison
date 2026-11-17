// =============================================================================
// CallScreenViewModel
//
// The call screen: peer photo, status line, name, number and the audio dock,
// for whatever call ICallService is tracking. The view owns the second tick
// (RefreshClock) so Core stays free of timers.
// =============================================================================
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Calls;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public sealed class CallScreenViewModel : INotifyPropertyChanged
    {
        private const string DisplayNameContext = "call-screen";

        private readonly ICallService _calls;
        private readonly ChatStateStore _chatState;
        private readonly IDispatcher _ui;
        private readonly IStringResources _strings;
        private readonly IContactService _contacts;
        private readonly IJidResolver _jids;
        private readonly IVoicePlaybackRoutingService _routing;
        private readonly Func<DateTime> _utcNow;

        private ActiveCallSnapshot _call;
        private string _statusText = string.Empty;
        private string _displayName = string.Empty;
        private string _phoneNumber = string.Empty;
        private string _avatarUrl;
        private bool _ending;

        public CallScreenViewModel(
            ICallService calls,
            ChatStateStore chatState,
            IDispatcher ui,
            IStringResources strings = null,
            IContactService contacts = null,
            IVoicePlaybackRoutingService routing = null,
            Func<DateTime> utcNow = null,
            IJidResolver jids = null)
        {
            _calls = calls ?? throw new ArgumentNullException(nameof(calls));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _strings = strings;
            _contacts = contacts;
            _routing = routing;
            _jids = jids;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);

            AnswerCommand = new RelayCommand(() => _ = _calls.AnswerActiveCallAsync());
            // Ringing → reject; Connecting/Active → terminate (meowcaller hangup).
            DeclineCommand = new RelayCommand(() => _ = EndCallAsync());
            EndCallCommand = new RelayCommand(() => _ = EndCallAsync());
            MuteCommand = new RelayCommand(() => _calls.SetMuted(!IsMuted));
            SpeakerCommand = new RelayCommand(ToggleSpeaker);
            BluetoothCommand = new RelayCommand(() => _calls.SetBluetoothOn(!IsBluetoothOn));
            MessageCommand = new RelayCommand(RequestOpenChat);
            ReplyWithMessageCommand = new RelayCommand(() => _ = DeclineAndOpenChatAsync());

            _calls.ActiveCallChanged += OnActiveCallChanged;
            Apply(_calls.ActiveCall);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Payload is the peer JID to open in Chats.</summary>
        public event EventHandler<string> OpenChatRequested;

        public ICommand AnswerCommand { get; }

        /// <summary>Rejects a ringing offer (or hangs up). Same path as <see cref="EndCallCommand"/>.</summary>
        public ICommand DeclineCommand { get; }

        public ICommand EndCallCommand { get; }

        public ICommand MuteCommand { get; }

        public ICommand SpeakerCommand { get; }

        public ICommand BluetoothCommand { get; }

        public ICommand MessageCommand { get; }

        /// <summary>Rejects the ring, then asks the shell to open the peer chat.</summary>
        public ICommand ReplyWithMessageCommand { get; }

        public bool IsOpen => _call != null;

        public bool IsRinging =>
            _call != null && _call.Phase == ActiveCallPhase.Ringing;

        public bool IsConnected => _call != null && _call.Phase == ActiveCallPhase.Active;

        public bool CanToggleSpeaker =>
            _call != null &&
            (_call.Phase == ActiveCallPhase.Ringing ||
             _call.Phase == ActiveCallPhase.Connecting ||
             _call.Phase == ActiveCallPhase.Dialing ||
             _call.Phase == ActiveCallPhase.Active);

        public bool IsMuted => _call != null && _call.IsMuted;

        /// <summary>Two-way for the call-dock <c>ToggleButton</c>; also flipped by <see cref="SpeakerCommand"/>.</summary>
        public bool IsSpeakerOn
        {
            get => _call != null && _call.IsSpeakerOn;
            set => ApplySpeaker(value);
        }

        public bool IsBluetoothOn => _call != null && _call.IsBluetoothOn;

        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        public string DisplayName
        {
            get => _displayName;
            private set => Set(ref _displayName, value);
        }

        public string PhoneNumber
        {
            get => _phoneNumber;
            private set => Set(ref _phoneNumber, value);
        }

        public string AvatarUrl
        {
            get => _avatarUrl;
            private set
            {
                if (Set(ref _avatarUrl, value))
                {
                    OnPropertyChanged(nameof(HasAvatar));
                }
            }
        }

        public bool HasAvatar => !string.IsNullOrWhiteSpace(_avatarUrl);

        /// <summary>Recomputes the running call time; the view calls this once a second.</summary>
        public void RefreshClock()
        {
            ActiveCallSnapshot call = _call;
            if (call == null)
            {
                return;
            }

            StatusText = CallScreenStatusText.For(
                call.Phase,
                call.ConnectedAtUtc,
                _utcNow(),
                Label("CallScreen_Dialing", "DIALING..."),
                Label("CallScreen_Receiving", "RECEIVING CALL..."),
                Label("CallScreen_Ended", "CALL ENDED"),
                Label("CallScreen_Connecting", "CONNECTING..."));
        }

        private void ToggleSpeaker() => ApplySpeaker(!IsSpeakerOn);

        private void ApplySpeaker(bool on)
        {
            bool current = _call != null && _call.IsSpeakerOn;
            if (!CanToggleSpeaker || on == current)
            {
                // Snap ToggleButton IsChecked back when the change was rejected.
                OnPropertyChanged(nameof(IsSpeakerOn));
                return;
            }

            _calls.SetSpeakerOn(on);
            _routing?.SetPreferredRoute(
                on ? VoicePlaybackRoute.Speaker : VoicePlaybackRoute.Earpiece);
        }

        private void OnActiveCallChanged(object sender, EventArgs e)
        {
            ActiveCallSnapshot call = _calls.ActiveCall;
            _ = _ui.RunAsync(() => Apply(call));
        }

        private void Apply(ActiveCallSnapshot call)
        {
            bool peerChanged = call == null || _call == null ||
                               !string.Equals(call.PeerJid, _call.PeerJid, StringComparison.Ordinal);
            _call = call;
            if (peerChanged)
            {
                ResolvePeer(call?.PeerJid);
            }

            RefreshClock();
            OnPropertyChanged(nameof(IsOpen));
            OnPropertyChanged(nameof(IsRinging));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(CanToggleSpeaker));
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(IsSpeakerOn));
            OnPropertyChanged(nameof(IsBluetoothOn));
        }

        private void ResolvePeer(string peerJid)
        {
            if (string.IsNullOrWhiteSpace(peerJid))
            {
                DisplayName = string.Empty;
                PhoneNumber = string.Empty;
                AvatarUrl = null;
                return;
            }

            string digits = _contacts?.TryResolvePhone(peerJid);
            if (string.IsNullOrWhiteSpace(digits))
            {
                digits = JidHelper.TryPhoneFromJid(peerJid);
            }

            string phone = string.IsNullOrWhiteSpace(digits) ? string.Empty : "+" + digits;
            ChatItem chat = FindChatFlexible(peerJid);
            string name = chat?.Name;
            if (string.IsNullOrWhiteSpace(name) && _contacts != null)
            {
                name = _contacts.ResolveDisplayName(peerJid, DisplayNameContext);
            }

            DisplayName = !string.IsNullOrWhiteSpace(name) ? name : (phone.Length > 0 ? phone : peerJid);
            PhoneNumber = phone;
            // Prefer high for the full-bleed call photo; GetAvatarUrl falls back either way.
            AvatarUrl = chat != null ? chat.GetAvatarUrl(preferHigh: true) : null;
        }

        private ChatItem FindChatFlexible(string jid)
        {
            ChatItem chat = _chatState.FindChat(jid);
            if (chat != null)
            {
                return chat;
            }

            if (_jids == null)
            {
                return null;
            }

            string canonical = _jids.GetCanonicalJid(jid);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, jid, StringComparison.OrdinalIgnoreCase))
            {
                chat = _chatState.FindChat(canonical);
                if (chat != null)
                {
                    return chat;
                }
            }

            string alias;
            if (_jids.TryGetAlias(jid, out alias) && !string.IsNullOrWhiteSpace(alias))
            {
                return _chatState.FindChat(alias);
            }

            return null;
        }

        private void RequestOpenChat()
        {
            ActiveCallSnapshot call = _call;
            if (call != null && !string.IsNullOrWhiteSpace(call.PeerJid))
            {
                OpenChatRequested?.Invoke(this, call.PeerJid);
            }
        }

        private async Task EndCallAsync()
        {
            if (_ending)
            {
                return;
            }

            _ending = true;
            try
            {
                await _calls.EndActiveCallAsync().ConfigureAwait(true);
            }
            catch
            {
                // Signaling can fail (no session / reject stanza); façade already cleared ActiveCall.
            }
            finally
            {
                _ending = false;
            }
        }

        private async Task DeclineAndOpenChatAsync()
        {
            ActiveCallSnapshot call = _call;
            string peerJid = call?.PeerJid;
            await EndCallAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(peerJid))
            {
                OpenChatRequested?.Invoke(this, peerJid);
            }
        }

        private string Label(string key, string fallback) =>
            _strings?.Get(key, fallback) ?? fallback;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
