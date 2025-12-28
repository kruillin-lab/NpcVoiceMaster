using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.IO;

namespace NpcVoiceMaster
{
    public class VoicePlayer : IDisposable
    {
        private readonly object _lock = new();

        private IWavePlayer? _outputDevice;
        private WaveStream? _audioReader;
        private MemoryStream? _audioStream;

        public Action<string>? Log { get; set; }

        private bool _disposed;

        public void PlayAudio(byte[] audioData)
        {
            if (audioData == null || audioData.Length == 0)
            {
                Log?.Invoke("[VoicePlayer] PlayAudio: empty bytes.");
                return;
            }

            lock (_lock)
            {
                if (_disposed) return;

                try
                {
                    StopInternal();
                    LogDefaultDevice();

                    _audioStream = new MemoryStream(audioData, writable: false);

                    // If this throws, your bytes aren't valid MP3
                    _audioReader = new Mp3FileReader(_audioStream);
                    Log?.Invoke($"[VoicePlayer] MP3 decoded OK. Duration={_audioReader.TotalTime}");

                    _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    _outputDevice.Init(_audioReader);
                    _outputDevice.Play();

                    Log?.Invoke("[VoicePlayer] Playback started (WASAPI Shared).");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[VoicePlayer] PlayAudio ERROR: {ex.GetType().Name}: {ex.Message}");
                    StopInternal();
                }
            }
        }

        public void PlayBeep(int frequencyHz = 440, int ms = 400)
        {
            lock (_lock)
            {
                if (_disposed) return;

                try
                {
                    StopInternal();
                    LogDefaultDevice();

                    var sampleRate = 48000;
                    var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

                    var totalSamples = (int)((ms / 1000.0) * sampleRate);
                    var provider = new BufferedWaveProvider(waveFormat)
                    {
                        DiscardOnBufferOverflow = true
                    };

                    var buffer = new byte[totalSamples * sizeof(float)];
                    double phase = 0;
                    double phaseInc = 2 * Math.PI * frequencyHz / sampleRate;

                    for (int i = 0; i < totalSamples; i++)
                    {
                        float sample = (float)(Math.Sin(phase) * 0.2f);
                        phase += phaseInc;

                        var bytes = BitConverter.GetBytes(sample);
                        Buffer.BlockCopy(bytes, 0, buffer, i * sizeof(float), sizeof(float));
                    }

                    provider.AddSamples(buffer, 0, buffer.Length);

                    _outputDevice = new WasapiOut(AudioClientShareMode.Shared, 100);
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    _outputDevice.Init(provider);
                    _outputDevice.Play();

                    Log?.Invoke($"[VoicePlayer] Beep started: {frequencyHz}Hz {ms}ms");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[VoicePlayer] PlayBeep ERROR: {ex.GetType().Name}: {ex.Message}");
                    StopInternal();
                }
            }
        }

        private void LogDefaultDevice()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                Log?.Invoke($"[VoicePlayer] Default output device: {device.FriendlyName}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[VoicePlayer] Default device query failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_disposed) return;
                StopInternal();
            }
        }

        private void StopInternal()
        {
            try { _outputDevice?.Stop(); } catch { }

            try { _audioReader?.Dispose(); } catch { }
            _audioReader = null;

            try { _audioStream?.Dispose(); } catch { }
            _audioStream = null;

            if (_outputDevice != null)
            {
                try { _outputDevice.PlaybackStopped -= OnPlaybackStopped; } catch { }
                try { _outputDevice.Dispose(); } catch { }
                _outputDevice = null;
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            lock (_lock)
            {
                if (_disposed) return;

                if (e.Exception != null)
                    Log?.Invoke($"[VoicePlayer] PlaybackStopped exception: {e.Exception.GetType().Name}: {e.Exception.Message}");

                try { _audioReader?.Dispose(); } catch { }
                _audioReader = null;

                try { _audioStream?.Dispose(); } catch { }
                _audioStream = null;

                if (_outputDevice != null)
                {
                    try { _outputDevice.PlaybackStopped -= OnPlaybackStopped; } catch { }
                    try { _outputDevice.Dispose(); } catch { }
                    _outputDevice = null;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                StopInternal();
            }
        }
    }
}
