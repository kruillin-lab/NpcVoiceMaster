using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace NPCVoiceMaster
{
    public sealed class VoicePlayer : IDisposable
    {
        private readonly SemaphoreSlim _playLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        /// <summary>
        /// Fire-and-forget convenience (not recommended for sequencing).
        /// </summary>
        public void PlayAudio(byte[] wavBytes)
        {
            _ = PlayAudioAsync(wavBytes, CancellationToken.None);
        }

        /// <summary>
        /// Plays a WAV (byte[]) and completes when playback actually finishes.
        /// This avoids guessing duration and prevents random 5–30s dead gaps.
        /// </summary>
        public async Task PlayAudioAsync(byte[] wavBytes, CancellationToken ct)
        {
            if (_disposed) return;
            if (wavBytes == null || wavBytes.Length == 0) return;

            await _playLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed) return;

                using var ms = new MemoryStream(wavBytes, writable: false);
                using var reader = new WaveFileReader(ms);
                using var output = new WaveOutEvent();

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnStopped(object? s, StoppedEventArgs e)
                {
                    output.PlaybackStopped -= OnStopped;

                    // If NAudio reports an error, surface it (but don't crash plugin)
                    if (e.Exception != null)
                        tcs.TrySetException(e.Exception);
                    else
                        tcs.TrySetResult(true);
                }

                output.PlaybackStopped += OnStopped;
                output.Init(reader);

                using var reg = ct.Register(() =>
                {
                    try { output.Stop(); } catch { /* ignore */ }
                });

                output.Play();

                try
                {
                    await tcs.Task.ConfigureAwait(false);
                }
                catch
                {
                    // Swallow exceptions so one bad WAV doesn't kill the whole queue
                    // (the plugin will just skip/continue)
                }
            }
            finally
            {
                _playLock.Release();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _playLock.Dispose();
        }
    }
}
