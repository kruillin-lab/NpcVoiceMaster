// File: VoicePlayer.cs
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
        private bool _disposed;

        public Action<string>? Log { get; set; }

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

                    // Decode
                    WaveStream decoded;
                    if (LooksLikeWav(audioData))
                    {
                        decoded = new WaveFileReader(_audioStream);
                        Log?.Invoke($"[VoicePlayer] WAV decoded OK. Duration={decoded.TotalTime}");
                    }
                    else
                    {
                        decoded = new Mp3FileReader(_audioStream);
                        Log?.Invoke($"[VoicePlayer] MP3 decoded OK. Duration={decoded.TotalTime}");
                    }

                    // Force volume = 1.0 (100%) using a volume wrapper.
                    // This makes "I didn't hear anything" much less likely to be a plugin volume issue.
                    _audioReader = new WaveChannel32(decoded) { Volume = 1.0f };

                    // Output: WaveOutEvent is the most compatible path on Windows.
                    // (WasapiOut can get "clever" with routing/exclusive mode and make you think nothing happened.)
                    var waveOut = new WaveOutEvent
                    {
                        DesiredLatency = 100
                    };

                    _outputDevice = waveOut;
                    _outputDevice.PlaybackStopped += OnPlaybackStopped;
                    _outputDevice.Init(_audioReader);
                    _outputDevice.Play();

                    Log?.Invoke("[VoicePlayer] Playback started (WaveOutEvent, volume=1.0).");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[VoicePlayer] PlayAudio ERROR: {ex.GetType().Name}: {ex.Message}");
                    StopInternal();
                }
            }
        }

        private static bool LooksLikeWav(byte[] bytes)
        {
            if (bytes.Length < 12) return false;
            return bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E';
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

                StopInternal();
            }
        }

        private void LogDefaultDevice()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                Log?.Invoke($"[VoicePlayer] Default output device: {device.FriendlyName}");
                Log?.Invoke($"[VoicePlayer] Device state: {device.State}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[VoicePlayer] Default device query failed: {ex.GetType().Name}: {ex.Message}");
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
