using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Uwp.Client;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Unitary UWP mic capture via <see cref="MediaCapture"/> → temporary PCM, encoded to
    /// Ogg/Opus on stop so the result is a voice note WhatsApp clients can actually play.
    /// Returns <see cref="IAudioRecordingSession"/> handles for runtime elapsed.
    /// </summary>
    public sealed class AudioRecordingService : IAudioRecordingService
    {
        private const ulong MaxRecordingBytes = 20UL * 1024UL * 1024UL;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private MediaCapture _capture;
        private StorageFile _file;
        private DateTime _startedAtUtc;
        private AudioRecordingSession _current;

        public bool IsRecording => _current != null && _current.IsActive;

        public IAudioRecordingSession Current =>
            _current != null && _current.IsActive ? _current : null;

        public async Task<IAudioRecordingSession> StartAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                await DiscardUnlockedAsync().ConfigureAwait(true);

                _capture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio,
                    MediaCategory = MediaCategory.Speech
                };
                await _capture.InitializeAsync(settings);

                var folder = ApplicationData.Current.TemporaryFolder;
                _file = await folder.CreateFileAsync(
                    "unison_voice_" + DateTime.UtcNow.Ticks + ".wav",
                    CreationCollisionOption.ReplaceExisting);
                await _capture.StartRecordToStorageFileAsync(CreateVoiceProfile(), _file);

                _startedAtUtc = DateTime.UtcNow;
                _current = new AudioRecordingSession(this, _startedAtUtc);
                return _current;
            }
            catch
            {
                await DiscardUnlockedAsync().ConfigureAwait(true);
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        internal bool IsSessionCurrent(AudioRecordingSession session)
        {
            return session != null && ReferenceEquals(_current, session) && !session.IsCompleted;
        }

        internal Task<AudioRecordingResult> StopSessionAsync(AudioRecordingSession session)
        {
            return CompleteSessionAsync(session, cancel: false);
        }

        internal Task CancelSessionAsync(AudioRecordingSession session)
        {
            return CompleteSessionAsync(session, cancel: true);
        }

        private async Task<AudioRecordingResult> CompleteSessionAsync(AudioRecordingSession session, bool cancel)
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (!IsSessionCurrent(session))
                {
                    if (cancel)
                    {
                        return null;
                    }

                    throw new InvalidOperationException("Nenhuma gravação ativa.");
                }

                if (cancel)
                {
                    await DiscardUnlockedAsync().ConfigureAwait(true);
                    return null;
                }

                var capture = _capture;
                var file = _file;
                DateTime startedAtUtc = _startedAtUtc;

                session.MarkCompleted();
                _current = null;
                _capture = null;
                _file = null;
                _startedAtUtc = DateTime.MinValue;

                await capture.StopRecordAsync();
                DateTime endedAtUtc = DateTime.UtcNow;
                uint durationSeconds = (uint)Math.Max(1, Math.Round((endedAtUtc - startedAtUtc).TotalSeconds));

                try { capture.Dispose(); } catch { }

                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size == 0)
                {
                    await TryDeleteAsync(file).ConfigureAwait(true);
                    throw new InvalidOperationException("A gravação ficou vazia.");
                }

                if (properties.Size > MaxRecordingBytes)
                {
                    await TryDeleteAsync(file).ConfigureAwait(true);
                    throw new InvalidOperationException("A gravação ultrapassou 20 MB.");
                }

                byte[] bytes = await ReadBytesAsync(file).ConfigureAwait(true);
                await TryDeleteAsync(file).ConfigureAwait(true);

                byte[] opus = await Task.Run(() => OggOpusHandlerService.EncodeWavToOggOpus(bytes)).ConfigureAwait(true);
                bool encoded = opus != null && opus.Length > 0;
                if (!encoded)
                {
                    Debug.WriteLine("[AudioRecordingService] Opus encode failed; sending raw PCM.");
                }

                return new AudioRecordingResult
                {
                    Bytes = encoded ? opus : bytes,
                    MimeType = encoded ? OggOpusHandlerService.OpusMimeType : "audio/wav",
                    DurationSeconds = durationSeconds,
                    StartedAtUtc = startedAtUtc,
                    EndedAtUtc = endedAtUtc,
                    IsVoiceNote = true
                };
            }
            catch
            {
                await DiscardUnlockedAsync().ConfigureAwait(true);
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Captures raw PCM at the rate voice notes are encoded in, so the Opus pass that follows
        /// neither has to decode a compressed format - this platform has no Opus encoder and no
        /// AAC decoder we could chain - nor resample.
        /// </summary>
        private static MediaEncodingProfile CreateVoiceProfile()
        {
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto);
            profile.Audio = AudioEncodingProperties.CreatePcm(
                OggOpusHandlerService.VoiceSampleRate,
                OggOpusHandlerService.VoiceChannels,
                16);
            return profile;
        }

        private async Task DiscardUnlockedAsync()
        {
            var capture = _capture;
            var file = _file;
            var session = _current;
            bool wasRecording = session != null && !session.IsCompleted;

            if (session != null)
            {
                session.MarkCompleted();
            }

            _current = null;
            _capture = null;
            _file = null;
            _startedAtUtc = DateTime.MinValue;

            if (capture != null)
            {
                try
                {
                    if (wasRecording)
                    {
                        await capture.StopRecordAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[AudioRecordingService] Stop on discard: " + ex.Message);
                }

                try { capture.Dispose(); } catch { }
            }

            if (file != null)
            {
                await TryDeleteAsync(file).ConfigureAwait(true);
            }
        }

        private static async Task<byte[]> ReadBytesAsync(StorageFile file)
        {
            var buffer = await FileIO.ReadBufferAsync(file);
            return buffer.ToArray();
        }

        private static async Task TryDeleteAsync(StorageFile file)
        {
            try
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AudioRecordingService] Delete temp: " + ex.Message);
            }
        }

        /// <summary>Session handle returned by <see cref="StartAsync"/>.</summary>
        internal sealed class AudioRecordingSession : IAudioRecordingSession
        {
            private readonly AudioRecordingService _owner;
            private int _completed;

            public AudioRecordingSession(AudioRecordingService owner, DateTime startedAtUtc)
            {
                _owner = owner;
                StartedAtUtc = startedAtUtc;
            }

            public bool IsCompleted => Volatile.Read(ref _completed) != 0;

            public bool IsActive => !IsCompleted && _owner.IsSessionCurrent(this);

            public DateTime StartedAtUtc { get; }

            public TimeSpan Elapsed
            {
                get
                {
                    if (!IsActive)
                    {
                        return TimeSpan.Zero;
                    }

                    TimeSpan value = DateTime.UtcNow - StartedAtUtc;
                    return value < TimeSpan.Zero ? TimeSpan.Zero : value;
                }
            }

            public void MarkCompleted()
            {
                Interlocked.Exchange(ref _completed, 1);
            }

            public Task<AudioRecordingResult> StopAsync()
            {
                return _owner.StopSessionAsync(this);
            }

            public Task CancelAsync()
            {
                return _owner.CancelSessionAsync(this);
            }
        }
    }
}
