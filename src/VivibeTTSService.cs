using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NAudio.Wave;
using MessageBox = System.Windows.MessageBox;

namespace RSTGameTranslation
{
    /// <summary>
    /// Vivibe (LucyLab) TTS service. Ported from the vivibe-tts-desktop LucyLab
    /// JSON-RPC client and shaped like <see cref="ElevenLabsService"/>: an API
    /// call synthesizes speech, the resulting audio is downloaded to a temp file
    /// and played through NAudio. Read-aloud only — no MP3/WAV export.
    ///
    /// The LucyLab flow is two-step: POST a "tts" JSON-RPC request, receive an
    /// audio URL, then download the audio bytes from that URL.
    /// </summary>
    public class VivibeTTSService
    {
        private static VivibeTTSService? _instance;
        private readonly HttpClient _httpClient;

        // Ensure only one speech request is processed at a time.
        private static readonly SemaphoreSlim _speechSemaphore = new SemaphoreSlim(1, 1);

        private static bool _isPlayingAudio = false;
        private static IWavePlayer? _currentPlayer = null;

        public static VivibeTTSService Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new VivibeTTSService();
                }
                return _instance;
            }
        }

        private VivibeTTSService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<bool> SpeakText(string text)
        {
            // Skip if another request is already running (mirrors ElevenLabsService).
            if (!await _speechSemaphore.WaitAsync(0))
            {
                Console.WriteLine("Another Vivibe speech request is already in progress. Skipping this one.");
                return false;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine("Cannot speak empty text");
                    return false;
                }

                StopCurrentPlayback();

                string apiKey = ConfigManager.Instance.GetVivibeTtsApiKey();
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    MessageBox.Show("Vivibe TTS API key is not set. Please configure it in Settings.",
                        "API Key Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                string baseUrl = ConfigManager.Instance.GetVivibeTtsBaseUrl();
                string voiceId = ConfigManager.Instance.GetVivibeTtsVoice();
                double speed = ConfigManager.Instance.GetVivibeTtsSpeed();
                int blockVersion = ConfigManager.Instance.GetVivibeTtsBlockVersion();

                // Download the audio bytes (two-step JSON-RPC call with retry).
                byte[]? audioData = await GenerateAudioAsync(text, baseUrl, apiKey, voiceId, speed, blockVersion);
                if (audioData == null || audioData.Length == 0)
                {
                    Console.WriteLine("Vivibe TTS returned no audio");
                    return false;
                }

                // Save to a temp file for NAudio playback.
                string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
                Directory.CreateDirectory(tempDir);
                string audioFile = Path.Combine(tempDir, $"tts_vivibe_{DateTime.Now.Ticks}.wav");
                await File.WriteAllBytesAsync(audioFile, audioData);

                Console.WriteLine($"Vivibe audio saved to {audioFile}, playing...");
                return await PlayAudioFileAsync(audioFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during Vivibe TTS: {ex.Message}");
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Error with Vivibe Text-to-Speech: {ex.Message}",
                        "TTS Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
                return false;
            }
            finally
            {
                _speechSemaphore.Release();
            }
        }

        // Two-step LucyLab JSON-RPC call: request audio URL, then download it.
        // Retries on HTTP 429 and 5xx with exponential backoff (ported from the
        // vivibe-tts-desktop tts-client).
        private async Task<byte[]?> GenerateAudioAsync(
            string text, string baseUrl, string apiKey, string voiceId, double speed, int blockVersion)
        {
            int[] delays = { 1000, 2000, 4000 };

            var payload = new
            {
                method = "tts",
                input = new
                {
                    text = text,
                    userVoiceId = voiceId,
                    speed = speed,
                    blockVersion = blockVersion
                }
            };
            string jsonRequest = JsonSerializer.Serialize(payload);

            Exception? lastError = null;

            for (int attempt = 0; attempt <= delays.Length; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                    request.Headers.TryAddWithoutValidation("Origin", "https://vivibe.app");
                    request.Headers.TryAddWithoutValidation("Referer", "https://vivibe.app/");
                    request.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

                    HttpResponseMessage response = await _httpClient.SendAsync(request);

                    // Rate limited - wait and retry.
                    if ((int)response.StatusCode == 429)
                    {
                        int retryAfter = 5;
                        if (response.Headers.TryGetValues("Retry-After", out var values))
                        {
                            foreach (var v in values)
                            {
                                if (int.TryParse(v, out int parsed)) { retryAfter = parsed; break; }
                            }
                        }
                        Console.WriteLine($"Vivibe TTS rate limited, waiting {retryAfter}s (attempt {attempt + 1})");
                        await Task.Delay(retryAfter * 1000);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        string errorContent = await response.Content.ReadAsStringAsync();
                        throw new Exception($"Vivibe TTS API error {(int)response.StatusCode}: {errorContent}");
                    }

                    string body = await response.Content.ReadAsStringAsync();
                    string? audioUrl = ExtractAudioUrl(body);
                    if (string.IsNullOrEmpty(audioUrl))
                    {
                        throw new Exception("No audio URL in Vivibe TTS response");
                    }

                    // Step 2: download the audio bytes.
                    HttpResponseMessage audioResponse = await _httpClient.GetAsync(audioUrl);
                    if (!audioResponse.IsSuccessStatusCode)
                    {
                        throw new Exception($"Audio download failed: {(int)audioResponse.StatusCode}");
                    }

                    byte[] bytes = await audioResponse.Content.ReadAsByteArrayAsync();
                    if (bytes.Length == 0)
                    {
                        throw new Exception("Empty audio file");
                    }
                    return bytes;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Console.WriteLine($"Vivibe TTS attempt {attempt + 1} failed: {ex.Message}");
                    if (attempt < delays.Length)
                    {
                        await Task.Delay(delays[attempt]);
                    }
                }
            }

            if (lastError != null) throw lastError;
            return null;
        }

        // Parse { "result": { "url": "..." } } or surface { "error": { "message": "..." } }.
        private static string? ExtractAudioUrl(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                string message = error.ValueKind == JsonValueKind.Object &&
                                 error.TryGetProperty("message", out JsonElement msg)
                    ? msg.GetString() ?? error.ToString()
                    : error.ToString();
                throw new Exception($"Vivibe TTS API error: {message}");
            }

            if (root.TryGetProperty("result", out JsonElement result) &&
                result.TryGetProperty("url", out JsonElement url))
            {
                return url.GetString();
            }
            return null;
        }

        public static void StopAllTTS()
        {
            try
            {
                Console.WriteLine("Stopping all Vivibe TTS activities");
                _instance?.StopCurrentPlayback();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping Vivibe TTS: {ex.Message}");
            }
        }

        private void StopCurrentPlayback()
        {
            if (_isPlayingAudio && _currentPlayer != null)
            {
                try
                {
                    Console.WriteLine("Stopping current Vivibe audio playback");
                    _currentPlayer.Stop();
                    _currentPlayer.Dispose();
                    _currentPlayer = null;
                    _isPlayingAudio = false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping current playback: {ex.Message}");
                }
            }
        }

        private async Task<bool> PlayAudioFileAsync(string filePath)
        {
            var tcs = new TaskCompletionSource<bool>();

            try
            {
                _isPlayingAudio = true;
                _currentPlayer = new WaveOutEvent();

                _currentPlayer.PlaybackStopped += (sender, args) =>
                {
                    Console.WriteLine("Vivibe audio playback completed");
                    _isPlayingAudio = false;

                    if (_currentPlayer != null)
                    {
                        _currentPlayer.Dispose();
                        _currentPlayer = null;
                    }

                    try
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            Console.WriteLine($"Temp audio file deleted: {filePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to delete temp audio file: {ex.Message}");
                    }

                    tcs.TrySetResult(true);
                };

                var audioFile = new AudioFileReader(filePath);
                _currentPlayer.Init(audioFile);

                Console.WriteLine($"Starting Vivibe audio playback of file: {filePath}");
                _currentPlayer.Play();

                return await tcs.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error playing Vivibe audio file: {ex.Message}");
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Error playing audio: {ex.Message}",
                        "Audio Playback Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                });

                _isPlayingAudio = false;
                if (_currentPlayer != null)
                {
                    _currentPlayer.Dispose();
                    _currentPlayer = null;
                }

                try
                {
                    if (File.Exists(filePath)) File.Delete(filePath);
                }
                catch (Exception fileEx)
                {
                    Console.WriteLine($"Failed to delete temp audio file: {fileEx.Message}");
                }

                tcs.TrySetResult(false);
                return false;
            }
        }
    }
}
