// =============================================================================
// UwpCallAudioSink
//
// 960-sample float frames → AudioGraph FrameInput → device (Communications).
// Empty queue plays silence so the graph never underruns into garbage.
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;
using Unison.Voip.Engine.Media;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace Unison.Uwp.Services.Calls
{
    internal sealed class UwpCallAudioSink : ICallAudioSink
    {
        private const int MaxQueuedFrames = 4;

        private readonly object _gate = new object();
        private readonly ConcurrentQueue<float[]> _inbox = new ConcurrentQueue<float[]>();
        private readonly float[] _play = new float[AudioFrameFormat.FrameSamples];
        private int _playOffset = AudioFrameFormat.FrameSamples;
        private AudioGraph _graph;
        private AudioDeviceOutputNode _output;
        private AudioFrameInputNode _frameIn;
        private bool _started;
        private bool _disposed;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(UwpCallAudioSink));
                }

                if (_started)
                {
                    return;
                }
            }

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
            CreateAudioDeviceOutputNodeResult outputResult = await graph
                .CreateDeviceOutputNodeAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (outputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                graph.Dispose();
                throw new InvalidOperationException("Speaker output node failed: " + outputResult.Status);
            }

            AudioFrameInputNode frameIn = graph.CreateFrameInputNode(settings.EncodingProperties);
            frameIn.AddOutgoingConnection(outputResult.DeviceOutputNode);
            frameIn.QuantumStarted += OnQuantumStarted;

            lock (_gate)
            {
                if (_disposed)
                {
                    frameIn.QuantumStarted -= OnQuantumStarted;
                    frameIn.Dispose();
                    outputResult.DeviceOutputNode.Dispose();
                    graph.Dispose();
                    throw new ObjectDisposedException(nameof(UwpCallAudioSink));
                }

                _graph = graph;
                _output = outputResult.DeviceOutputNode;
                _frameIn = frameIn;
                _started = true;
            }

            graph.Start();
        }

        public Task WriteAsync(float[] pcm, CancellationToken cancellationToken)
        {
            if (pcm == null)
            {
                throw new ArgumentNullException(nameof(pcm));
            }

            if (pcm.Length != AudioFrameFormat.FrameSamples)
            {
                throw new ArgumentException(
                    "Call audio sink expects " + AudioFrameFormat.FrameSamples + " samples.",
                    nameof(pcm));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            var copy = new float[pcm.Length];
            Array.Copy(pcm, copy, pcm.Length);
            _inbox.Enqueue(copy);
            while (_inbox.Count > MaxQueuedFrames)
            {
                float[] dropped;
                _inbox.TryDequeue(out dropped);
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            AudioGraph graph;
            AudioDeviceOutputNode output;
            AudioFrameInputNode frameIn;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                graph = _graph;
                output = _output;
                frameIn = _frameIn;
                _graph = null;
                _output = null;
                _frameIn = null;
            }

            if (frameIn != null)
            {
                try
                {
                    frameIn.QuantumStarted -= OnQuantumStarted;
                }
                catch
                {
                }
            }

            if (graph != null)
            {
                try
                {
                    graph.Stop();
                }
                catch
                {
                }
            }

            try
            {
                frameIn?.Dispose();
            }
            catch
            {
            }

            try
            {
                output?.Dispose();
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

            float[] discarded;
            while (_inbox.TryDequeue(out discarded))
            {
            }
        }

        private void OnQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            if (_disposed || args.RequiredSamples <= 0)
            {
                return;
            }

            uint samplesNeeded = (uint)args.RequiredSamples;
            var frame = new AudioFrame(samplesNeeded * sizeof(float));
            var scratch = new float[samplesNeeded];
            int filled = 0;
            while (filled < scratch.Length)
            {
                if (_playOffset >= _play.Length)
                {
                    float[] next;
                    if (!_inbox.TryDequeue(out next))
                    {
                        break;
                    }

                    Array.Copy(next, _play, _play.Length);
                    _playOffset = 0;
                }

                int take = Math.Min(_play.Length - _playOffset, scratch.Length - filled);
                Array.Copy(_play, _playOffset, scratch, filled, take);
                _playOffset += take;
                filled += take;
            }

            if (filled == 0)
            {
                AudioFrameBufferAccess.WriteSilence(frame);
            }
            else
            {
                for (int i = filled; i < scratch.Length; i++)
                {
                    scratch[i] = 0f;
                }

                AudioFrameBufferAccess.WriteFloats(frame, scratch, 0, scratch.Length);
            }

            sender.AddFrame(frame);
        }
    }
}
