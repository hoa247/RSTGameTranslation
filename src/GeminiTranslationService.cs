using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RSTGameTranslation
{
    public class GeminiTranslationService : ITranslationService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static int _consecutiveFailures = 0;
        private static int _retryCount = 0;
        private static readonly object _keySwitchLock = new object();
        private const int MAX_RETRIES = 3;
        private int delayMS = 100;

        /// <summary>
        /// Check if error requires API key switch
        /// </summary>
        private bool ShouldSwitchApiKey(HttpStatusCode statusCode, string errorMessage)
        {
            // Switch key for quota/rate limit/invalid key errors
            if (statusCode == HttpStatusCode.Unauthorized ||  // 401 - Invalid API key
                (int)statusCode == 429 ||  // Too Many Requests - Rate limit
                statusCode == HttpStatusCode.Forbidden)  // 403 - Quota exceeded
            {
                return true;
            }

            // Check error message for quota/rate limit keywords
            string lowerMessage = errorMessage.ToLower();
            if (lowerMessage.Contains("quota") ||
                lowerMessage.Contains("rate limit") ||
                lowerMessage.Contains("rate-limit") ||
                lowerMessage.Contains("invalid api key") ||
                lowerMessage.Contains("api key not found") ||
                lowerMessage.Contains("api key invalid"))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get masked API key for logging (show only first 4 and last 4 characters)
        /// </summary>
        private string MaskApiKey(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey) || apiKey.Length < 8)
                return "***";
            return $"{apiKey.Substring(0, 4)}...{apiKey.Substring(apiKey.Length - 4)}";
        }

        /// <summary>
        /// Lightweight liveness check for a Gemini API key: sends a tiny request and reports
        /// whether the key works. Returns (alive, status) — alive on HTTP 200, dead on 401/403/429.
        /// Unlike TranslateAsync it never retries, switches keys, or shows dialogs.
        /// </summary>
        public static async Task<(bool alive, string status)> ValidateKeyAsync(string apiKey, string model)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return (false, "Empty API key");
            if (string.IsNullOrWhiteSpace(model))
                model = "gemini-3.5-flash-lite";

            try
            {
                var requestContent = new
                {
                    contents = new[] { new { parts = new[] { new { text = "ping" } } } }
                };
                string requestJson = JsonSerializer.Serialize(requestContent);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

                HttpResponseMessage response = await _httpClient.PostAsync(url, content);
                if (response.IsSuccessStatusCode)
                    return (true, "OK");

                string body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Gemini key validation failed: {(int)response.StatusCode} {response.StatusCode} - {body}");
                return (false, $"HTTP {(int)response.StatusCode} {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gemini key validation error: {ex.Message}");
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Translate text using the Gemini API
        /// </summary>
        /// <param name="jsonData">The JSON data to translate</param>
        /// <param name="prompt">The prompt to guide the translation</param>
        /// <returns>The translation result as a JSON string or null if translation failed</returns>
        public async Task<string?> TranslateAsync(string jsonData, string prompt)
        {
            string apiKey = ConfigManager.Instance.GetGeminiApiKey();
            string currenServices = ConfigManager.Instance.GetCurrentTranslationService();

            // Check retry limit
            if (_retryCount >= MAX_RETRIES)
            {
                Console.WriteLine($"Gemini API: Max retries ({MAX_RETRIES}) reached. Giving up.");
                _retryCount = 0;
                return null;
            }

            try
            {
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("Gemini API key not configured");
                    return null;
                }

                var requestContent = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new
                                {
                                    text = $"{prompt}\n{jsonData}"
                                }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        response_mime_type = "text/plain",
                    }
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string requestJson = JsonSerializer.Serialize(requestContent, jsonOptions);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                // Get model from config
                string model = ConfigManager.Instance.GetGeminiModel();
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                HttpResponseMessage response = await _httpClient.PostAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    // Reset consecutive failures counter on success
                    _consecutiveFailures = 0;
                    _retryCount = 0;

                    // Log the raw Gemini response before returning it
                    LogManager.Instance.LogLlmReply(jsonResponse);

                    // Capture request/response + token usage for the Debug window's cost view.
                    try
                    {
                        int promptTokens = 0, outputTokens = 0, totalTokens = 0;
                        using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
                        {
                            if (doc.RootElement.TryGetProperty("usageMetadata", out var usage))
                            {
                                if (usage.TryGetProperty("promptTokenCount", out var p)) promptTokens = p.GetInt32();
                                if (usage.TryGetProperty("candidatesTokenCount", out var c)) outputTokens = c.GetInt32();
                                if (usage.TryGetProperty("totalTokenCount", out var t)) totalTokens = t.GetInt32();
                            }
                        }
                        RequestLogManager.Add(new RequestLogEntry
                        {
                            Time = DateTime.Now,
                            Service = "Gemini",
                            Model = model,
                            RequestText = $"{prompt}\n{jsonData}",
                            ResponseText = jsonResponse,
                            PromptTokens = promptTokens,
                            OutputTokens = outputTokens,
                            TotalTokens = totalTokens,
                            EstimatedCostUsd = RequestLogManager.EstimateCostUsd(model, promptTokens, outputTokens),
                            Success = true,
                            Status = "OK"
                        });
                    }
                    catch { }

                    return jsonResponse;
                }
                else
                {
                    string errorMessage = await response.Content.ReadAsStringAsync();
                    _consecutiveFailures++;
                    Console.WriteLine($"Gemini API error: {response.StatusCode}, {errorMessage}, error count: {_consecutiveFailures}");

                    // Record the failed call so it shows up in the Debug request/response log.
                    try
                    {
                        RequestLogManager.Add(new RequestLogEntry
                        {
                            Time = DateTime.Now,
                            Service = "Gemini",
                            Model = model,
                            RequestText = $"{prompt}\n{jsonData}",
                            ResponseText = errorMessage,
                            Success = false,
                            Status = $"HTTP {(int)response.StatusCode} {response.StatusCode}"
                        });
                    }
                    catch { }

                    // Check if we should switch API key
                    if (ShouldSwitchApiKey(response.StatusCode, errorMessage))
                    {
                        string newApikey = null;
                        lock (_keySwitchLock)
                        {
                            newApikey = ConfigManager.Instance.GetNextApiKey(currenServices, apiKey);
                            if (!string.IsNullOrEmpty(newApikey) && newApikey != apiKey)
                            {
                                ConfigManager.Instance.SetGeminiApiKey(newApikey);
                                Console.WriteLine($"Switched API key from {MaskApiKey(apiKey)} to {MaskApiKey(newApikey)}");
                                _retryCount++;
                            }
                            else
                            {
                                Console.WriteLine("No more API keys available to switch to");
                            }
                        }

                        // Retry with new key (outside lock)
                        if (!string.IsNullOrEmpty(newApikey) && newApikey != apiKey)
                        {
                            return await TranslateAsync(jsonData, prompt);
                        }
                    }

                    // Parse error message
                    try
                    {
                        using JsonDocument errorDoc = JsonDocument.Parse(errorMessage);
                        if (errorDoc.RootElement.TryGetProperty("error", out JsonElement errorElement))
                        {
                            string detailedError = "";

                            // Extract error message
                            if (errorElement.TryGetProperty("message", out JsonElement messageElement))
                            {
                                detailedError = messageElement.GetString() ?? "";
                            }
                            // Show error if too many consecutive failures
                            if (_consecutiveFailures > 3)
                            {
                                // Write error to file
                                System.IO.File.WriteAllText("gemini_last_error.txt", $"Gemini API error: {detailedError}\n\nResponse code: {response.StatusCode}\nFull response: {errorMessage}");

                                // Non-blocking notification (a modal dialog here interrupts gameplay).
                                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                                    MainWindow.Instance?.ShowFastNotification(
                                        LocalizationManager.Instance.Strings["Title_GeminiError"],
                                        string.Format(LocalizationManager.Instance.Strings["Msg_GeminiApiError"], detailedError));
                                });
                            }
                            await Task.Delay(delayMS);
                            return null;
                        }
                    }
                    catch (JsonException)
                    {
                        // If we can't parse as JSON, just use the raw message
                    }
                    if (_consecutiveFailures > 3)
                    {
                        // Write error to file
                        System.IO.File.WriteAllText("gemini_last_error.txt", $"Gemini API error: {response.StatusCode}\n\nFull response: {errorMessage}");

                        // Non-blocking notification instead of a modal dialog.
                        System.Windows.Application.Current.Dispatcher.Invoke(() => {
                            MainWindow.Instance?.ShowFastNotification(
                                LocalizationManager.Instance.Strings["Title_GeminiError"],
                                string.Format(LocalizationManager.Instance.Strings["Msg_GeminiApiErrorStatus"], response.StatusCode, errorMessage));
                        });
                    }
                    await Task.Delay(delayMS);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Translation API error: {ex.Message}");

                // Write error to file
                System.IO.File.WriteAllText("gemini_last_error.txt", $"Gemini API error: {ex.Message}\n\nStack trace: {ex.StackTrace}");

                // Non-blocking notification instead of a modal dialog (network errors are common
                // mid-game and a blocking popup freezes the whole app until dismissed).
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    MainWindow.Instance?.ShowFastNotification(
                        LocalizationManager.Instance.Strings["Title_GeminiError"],
                        string.Format(LocalizationManager.Instance.Strings["Msg_GeminiApiException"], ex.Message));
                });

                return null;
            }
        }
    }
}