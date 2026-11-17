// =============================================================================
// UwpCallAudioSource
//
// Mic → AudioGraph (Communications, 16 kHz mono) → 960-sample float frames.
// Latest-frame handoff: a slow send loop drops older frames (realtime).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;
using Unison.Voip.Engine.Media;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace Unison.Uwp.Services.Calls
{
    internal sealed class UwpCallAudioSource : ICallAudioSource
    {
        private readonly object _gate = new object();
        private readonly float[] _accum = new float[AudioFrameFormat.FrameSamples];
        private int _accumCount;
        private float[] _latest;
        private AudioGraph _graph;
        private AudioDeviceInputNode _input;
        private AudioFrameOutputNode _frameOut;
        private bool _started;
        private bool _disposed;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(UwpCallAudioSource));
                }

                if (_started)
                {
                    return;
                }
            }

            await EnsureMicrophoneAccessAsync(cancellationToken).ConfigureAwait(false);

            var settings = new AudioGraphSettings(AudioRenderCategory.Communications)
            {
                EncodingProperties = AudioEncodingProperties.CreatePcm(
                    (uint)AudioFrameFormat.SampleRate, 1, 32),
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
            };

            CreateAudioGraphResult graphResult = await AudioGraph.CreateAsync(settings)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                throw new InvalidOperationException("AudioGraph create failed: " + graphResult.Status);
            }

            AudioGraph graph = graphResult.Graph;
            CreateAudioDeviceInputNodeResult inputResult = await graph
                .CreateDeviceInputNodeAsync(MediaCategory.Communications)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                graph.Dispose();
                throw new InvalidOperationException("Mic input node failed: " + inputResult.Status);
            }

            AudioFrameOutputNode frameOut = graph.CreateFrameOutputNode(settings.EncodingProperties);
            inputResult.DeviceInputNode.AddOutgoingConnection(frameOut);
            graph.QuantumStarted += OnQuantumStarted;

            lock (_gate)
            {
                if (_disposed)
                {
                    graph.QuantumStarted -= OnQuantumStarted;
                    frameOut.Dispose();
                    inputResult.DeviceInputNode.Dispose();
                    graph.Dispose();
                    throw new ObjectDisposedException(nameof(UwpCallAudioSource));
                }

                _graph = graph;
                _input = inputResult.DeviceInputNode;
                _frameOut = frameOut;
                _started = true;
            }

            graph.Start();
        }

        /// <summary>
        /// Same prompt path as voice notes: DeviceAccessInformation, then a throwaway
        /// MediaCapture.InitializeAsync when status is still unknown (16299 has no
        /// MediaCapture.RequestAccessAsync).
        /// </summary>
        private static async Task EnsureMicrophoneAccessAsync(CancellationToken cancellationToken)
        {
            DeviceAccessInformation access =
                DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture);
            DeviceAccessStatus status = access.CurrentStatus;
            if (status == DeviceAccessStatus.DeniedByUser ||
                status == DeviceAccessStatus.DeniedBySystem)
            {
                throw new InvalidOperationException("Microphone access was denied (" + status + ").");
            }

            if (status == DeviceAccessStatus.Allowed)
            {
                return;
            }

            var capture = new MediaCapture();
            try
            {
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio,
                    MediaCategory = MediaCategory.Communications
                }).AsTask(cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException("Microphone access was denied.", ex);
            }
            finally
            {
                try
                {
                    capture.Dispose();
                }
                catch
                {
                }
            }
        }

        public float[] TryReadFrame()
        {
            return Interlocked.Exchange(ref _latest, null);
        }

        public async Task<float[]> ReadAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                float[] frame = TryReadFrame();
                if (frame != null)
                {
                    return frame;
                }

                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        public void Dispose()
        {
            AudioGraph graph;
            AudioDeviceInputNode input;
            AudioFrameOutputNode frameOut;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                graph = _graph;
                input = _input;
                frameOut = _frameOut;
                _graph = null;
                _input = null;
                _frameOut = null;
            }

            if (graph != null)
            {
                try
                {
                    graph.QuantumStarted -= OnQuantumStarted;
                    graph.Stop();
                }
                catch
                {
                }
            }

            try
            {
                frameOut?.Dispose();
            }
            catch
            {
            }

            try
            {
                input?.Dispose();
            }
            catch
            {
            }

            try
            {
                graph?.Dispose();
            }
            catch
            {
            }

        }

        private void OnQuantumStarted(AudioGraph sender, object args)
        {
            AudioFrameOutputNode frameOut;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                frameOut = _frameOut;
            }

            if (frameOut == null)
            {
                return;
            }

            using (AudioFrame frame = frameOut.GetFrame())
            {
                if (frame == null)
                {
                    return;
                }

                // Quantum sample count varies; accumulate into 60 ms MLow frames.
                float[] scratch = AudioFrameBufferAccess.ReadAllFloats(frame);
                if (scratch == null || scratch.Length == 0)
                {
                    return;
                }

                int offset = 0;
                while (offset < scratch.Length)
                {
                    int space = _accum.Length - _accumCount;
                    int take = Math.Min(space, scratch.Length - offset);
                    Array.Copy(scratch, offset, _accum, _accumCount, take);
                    _accumCount += take;
                    offset += take;
                    if (_accumCount == _accum.Length)
                    {
                        var complete = new float[_accum.Length];
                        Array.Copy(_accum, complete, _accum.Length);
                        _accumCount = 0;
                        Interlocked.Exchange(ref _latest, complete);
                    }
                }
            }
        }
    }
}
