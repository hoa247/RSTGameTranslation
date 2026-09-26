using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.Wave;

namespace RSTGameTranslation
{
    /// <summary>
    /// A single Vivibe / LucyLab voice, slimmed down from the getAllVoices API
    /// response. Bound directly to the voice list in the TTS settings.
    /// </summary>
    public class VivibeVoice
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string TagsText { get; set; } = "";
        public string SampleUrl { get; set; } = "";

        // Lowercased haystack for the settings search box.
        public string SearchText { get; set; } = "";
    }

    /// <summary>
    /// Fetches the available voice list from the Vivibe / LucyLab TTS API.
    /// Ported from the vivibe-tts-desktop voices-cache "getAllVoices" call so the
    /// RST settings can browse and audition voices before selecting one.
    /// </summary>
    public static class VivibeVoicesService
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        /// <summary>
        /// Retrieve the full voice list. Throws with a user-facing message on
        /// failure so the caller can show it in the settings window.
        /// </summary>
        public static async Task<List<VivibeVoice>> GetVoicesAsync(string baseUrl, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new Exception("Vivibe TTS API key is not set.");
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new Exception("Vivibe TTS base URL is not set.");

            // getCommunityVoices is the endpoint the vivibe.app frontend uses; it
            // returns voices with a playable sampleAsset.cdnUrl for auditioning.
            var payload = new
            {
                method = "getCommunityVoices",
                input = new { sortBy = "score", page = 1, limit = 100, language = "vi" },
                mappings = new { items = new { sampleAsset = 1, avatar = 1 } }
            };
            string jsonRequest = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Origin", "https://vivibe.app");
            request.Headers.TryAddWithoutValidation("Referer", "https://vivibe.app/");
            request.Content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Failed to load voices ({(int)response.StatusCode}): {errorContent}");
            }

            string body = await response.Content.ReadAsStringAsync();
            return ParseVoices(body);
        }

        private static List<VivibeVoice> ParseVoices(string json)
        {
            var voices = new List<VivibeVoice>();
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out JsonElement error))
            {
                string message = error.ValueKind == JsonValueKind.Object &&
                                 error.TryGetProperty("message", out JsonElement msg)
                    ? msg.GetString() ?? error.ToString()
                    : error.ToString();
                throw new Exception($"Vivibe voices API error: {message}");
            }

            if (!root.TryGetProperty("result", out JsonElement result) ||
                !result.TryGetProperty("items", out JsonElement items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return voices;
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                string id = GetString(item, "id");
                if (string.IsNullOrEmpty(id)) continue;

                string name = GetString(item, "name");
                string description = GetString(item, "description");

                var tags = new List<string>();
                if (item.TryGetProperty("tags", out JsonElement tagsEl) &&
                    tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tag in tagsEl.EnumerateArray())
                    {
                        string? t = tag.GetString();
                        if (!string.IsNullOrWhiteSpace(t)) tags.Add(t);
                    }
                }

                string sampleUrl = "";
                if (item.TryGetProperty("sampleAsset", out JsonElement sample) &&
                    sample.ValueKind == JsonValueKind.Object)
                {
                    sampleUrl = GetString(sample, "cdnUrl");
                }

                string tagsText = string.Join(" · ", tags);
                voices.Add(new VivibeVoice
                {
                    Id = id,
                    Name = string.IsNullOrEmpty(name) ? id : name,
                    Description = description,
                    TagsText = tagsText,
                    SampleUrl = sampleUrl,
                    SearchText = $"{name} {description} {tagsText}".ToLowerInvariant()
                });
            }

            return voices;
        }

        private static string GetString(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }

        // ==================== Voice sample preview ("nghe thử") ====================
        // Played via NAudio (download to temp + WaveOut) rather than WPF MediaPlayer,
        // which depends on Media Foundation and can stay silent on some Windows
        // editions - the same reason the app avoids the UWP speech APIs.

        private static IWavePlayer? _previewPlayer;
        private static AudioFileReader? _previewReader;

        // Incremented on every play/stop request. A sample download that finishes
        // after a newer request started is discarded so only the latest preview
        // plays (fixes overlapping audio when clicking through voices quickly).
        private static int _previewGeneration = 0;

        /// <summary>True while a voice sample is currently playing.</summary>
        public static bool IsPlaying => _previewPlayer != null;

        /// <summary>
        /// Download and play a voice sample. Any currently playing preview is
        /// stopped first, and any in-flight preview download is discarded.
        /// <paramref name="onStopped"/> is invoked when this sample stops - naturally
        /// or because it was replaced/stopped - so the caller can reset its UI.
        /// Returns true if this call actually started playback (false if superseded).
        /// Throws with a user-facing message on failure.
        /// </summary>
        public static async Task<bool> PlaySampleAsync(string sampleUrl, Action? onStopped = null)
        {
            if (string.IsNullOrWhiteSpace(sampleUrl))
                throw new Exception("Giọng này không có mẫu nghe thử.");

            int myGeneration = ++_previewGeneration;
            StopInternal(); // stop the currently playing sample immediately

            byte[] data = await _httpClient.GetByteArrayAsync(sampleUrl);
            if (myGeneration != _previewGeneration) return false; // superseded while downloading
            if (data.Length == 0) throw new Exception("Mẫu nghe thử rỗng.");

            string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            Directory.CreateDirectory(tempDir);
            string ext = sampleUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".wav";
            string file = Path.Combine(tempDir, $"voice_preview_{DateTime.Now.Ticks}{ext}");
            await File.WriteAllBytesAsync(file, data);
            if (myGeneration != _previewGeneration)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                return false;
            }

            var reader = new AudioFileReader(file);
            var player = new WaveOutEvent();
            player.PlaybackStopped += (s, e) =>
            {
                try { reader.Dispose(); } catch { }
                try { player.Dispose(); } catch { }
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                if (ReferenceEquals(_previewPlayer, player)) _previewPlayer = null;
                if (ReferenceEquals(_previewReader, reader)) _previewReader = null;
                onStopped?.Invoke();
            };
            player.Init(reader);
            player.Play();

            _previewPlayer = player;
            _previewReader = reader;
            return true;
        }

        /// <summary>Stop any currently playing voice sample and cancel in-flight ones.</summary>
        public static void StopSample()
        {
            _previewGeneration++; // invalidate any in-flight download
            StopInternal();
        }

        private static void StopInternal()
        {
            try { _previewPlayer?.Stop(); } catch { }
            try { _previewPlayer?.Dispose(); } catch { }
            try { _previewReader?.Dispose(); } catch { }
            _previewPlayer = null;
            _previewReader = null;
        }
    }
}
