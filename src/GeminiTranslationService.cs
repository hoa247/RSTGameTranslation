using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RSTGameTranslation
{
    public class GeminiTranslationService : ITranslationService
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        // Token source for the in-flight request so the user can cancel a slow/hung call.
        private static CancellationTokenSource? _currentCts;

        /// <summary>Cancels the translation request currently in flight (if any).</summary>
        public static void CancelCurrent()
        {
            try { _currentCts?.Cancel(); } catch { }
        }

        // Extract the human-readable error.message from a Gemini error payload,
        // falling back to the raw body when it isn't JSON.
        private static string ExtractGeminiErrorMessage(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage)) return "Unknown error";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(errorMessage);
                if (doc.RootElement.TryGetProperty("error", out JsonElement err) &&
                    err.TryGetProperty("message", out JsonElement msg))
                    return msg.GetString() ?? errorMessage;
            }
            catch { }
            return errorMessage;
        }

        private static int _consecutiveFailures = 0;
        private static int _retryCount = 0;
        private static readonly object _keySwitchLock = new object();
        private const int MAX_RETRIES = 3;
        private int delayMS = 100;

        // Keys that hit a rate limit / quota wall are parked here (key -> UTC time it may
        // be used again). A parked key is skipped by key selection for 12h so a throttled
        // Google project gets a real rest instead of being hammered on every request.
        // Access only while holding _keySwitchLock.
        private static readonly Dictionary<string, DateTime> _keyCooldownUntil = new Dictionary<string, DateTime>();
        private static readonly TimeSpan KeyCooldownDuration = TimeSpan.FromHours(12);

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

        // True only for rate-limit / quota errors - the ones a 12h key rest actually
        // helps - not for a merely invalid or misconfigured key.
        private bool IsRateLimitOrQuota(HttpStatusCode statusCode, string errorMessage)
        {
            if ((int)statusCode == 429) return true;
            string m = (errorMessage ?? string.Empty).ToLower();
            return m.Contains("quota") || m.Contains("rate limit") || m.Contains("rate-limit");
        }

        // Cooldown helpers - call only while holding _keySwitchLock.
        private static bool IsKeyOnCooldownLocked(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            string k = key.Trim();
            if (_keyCooldownUntil.TryGetValue(k, out DateTime until))
            {
                if (DateTime.UtcNow < until) return true;
                _keyCooldownUntil.Remove(k); // cooldown elapsed - key is usable again
            }
            return false;
        }

        private static void PutKeyOnCooldownLocked(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _keyCooldownUntil[key.Trim()] = DateTime.UtcNow + KeyCooldownDuration;
        }

        // Shown when every key is still on its 12h cooldown, so we stop instead of
        // firing calls we already know will be throttled.
        private static void ShowAllKeysCooldownError(DateTime earliestFreeUtc)
        {
            string when = earliestFreeUtc == DateTime.MaxValue
                ? ""
                : $" Key sớm nhất dùng lại được lúc {earliestFreeUtc.ToLocalTime():HH:mm dd/MM}.";
            try
            {
                System.IO.File.WriteAllText("gemini_last_error.txt",
                    $"All Gemini keys are on 12h rate-limit cooldown.{when}");
            }
            catch { }
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ChatBoxWindow.Instance?.ShowError(
                        $"Tất cả API key đang tạm nghỉ 12 giờ do dính rate limit.{when}\n" +
                        $"➕ Thêm API key mới (từ Google project khác) trong Cài đặt để dịch tiếp.");
                });
            }
            catch { }
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
            // Hedged mode: fire N keys in parallel and take the fastest successful result.
            // Falls back to the normal single-key path when disabled or too few keys.
            int parallel = ConfigManager.Instance.GetGeminiParallelKeys();
            if (parallel > 1)
            {
                string? raced = await TranslateParallelAsync(jsonData, prompt, parallel);
                if (raced != null) return raced;
                // If the race produced nothing usable, fall through to the normal path
                // only when it wasn't a rate-limit wall (the parallel path already showed
                // the right message in that case). Simplest + safe: just return null here;
                // the parallel path handles its own error UI.
                return null;
            }
            return await TranslateAsyncCore(jsonData, prompt, new HashSet<string>());
        }

        // Builds the generateContent request JSON with model-aware thinking config.
        // Shared by the single-key path and each parallel attempt.
        private static string BuildRequestJson(string model, string prompt, string jsonData)
        {
            var generationConfig = new Dictionary<string, object>
            {
                ["response_mime_type"] = "text/plain",
            };
            // Gemini 3.x can't disable thinking and rejects thinkingBudget=0 (HTTP 400);
            // 3.x flash-lite accepts the faster "minimal", other 3.x use "low", 2.x use budget 0.
            string modelLower = model.ToLowerInvariant();
            bool isGen3Plus = modelLower.Contains("gemini-3") || modelLower.Contains("gemini-4");
            if (isGen3Plus && modelLower.Contains("flash-lite"))
                generationConfig["thinkingConfig"] = new { thinkingLevel = "minimal" };
            else if (isGen3Plus)
                generationConfig["thinkingConfig"] = new { thinkingLevel = "low" };
            else
                generationConfig["thinkingConfig"] = new { thinkingBudget = 0 };

            var requestContent = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = $"{prompt}\n{jsonData}" } } }
                },
                generationConfig
            };
            var jsonOptions = new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            return JsonSerializer.Serialize(requestContent, jsonOptions);
        }

        // Outcome of one parallel attempt against a specific key.
        private sealed class GeminiAttemptResult
        {
            public bool Success;
            public string? Response;
            public string ApiKey = "";
            public HttpStatusCode Status;
            public string Error = "";
            public bool Canceled;
            public long DurationMs;
        }

        // One Gemini call against an explicit key + cancellation token. Never throws (all failures
        // are captured in the result) so the race coordinator can await it safely. Logs completed
        // attempts to the request log; a canceled loser is not logged.
        private async Task<GeminiAttemptResult> AttemptOnceAsync(
            string jsonData, string prompt, string model, string apiKey, CancellationToken ct)
        {
            var result = new GeminiAttemptResult { ApiKey = apiKey };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int timeoutSec = ConfigManager.Instance.GetGeminiRequestTimeoutSec();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                string requestJson = BuildRequestJson(model, prompt, jsonData);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

                HttpResponseMessage response = await _httpClient.PostAsync(url, content, timeoutCts.Token);
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Status = response.StatusCode;
                Console.WriteLine($"[Gemini||] {MaskApiKey(apiKey)} {model} {result.DurationMs} ms (HTTP {(int)response.StatusCode})");

                string body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    result.Success = true;
                    result.Response = body;
                    LogAttempt(model, prompt, jsonData, body, true, "OK", result.DurationMs);
                }
                else
                {
                    result.Error = body;
                    LogAttempt(model, prompt, jsonData, body, false, $"HTTP {(int)response.StatusCode}", result.DurationMs);
                }
            }
            catch (OperationCanceledException)
            {
                // ct fired -> another key won or the user cancelled (ignore this loser).
                // Otherwise it's this attempt's own timeout -> a real, retryable failure.
                if (ct.IsCancellationRequested)
                {
                    result.Canceled = true;
                }
                else
                {
                    result.Status = HttpStatusCode.RequestTimeout; // 408
                    result.Error = $"Timeout {timeoutSec}s";
                    result.DurationMs = sw.ElapsedMilliseconds;
                    Console.WriteLine($"[Gemini||] {MaskApiKey(apiKey)} TIMEOUT {timeoutSec}s");
                    LogAttempt(model, prompt, jsonData, result.Error, false, $"TIMEOUT {timeoutSec}s", result.DurationMs);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        private static void LogAttempt(string model, string prompt, string jsonData, string body, bool success, string status, long durationMs)
        {
            try
            {
                int promptTokens = 0, outputTokens = 0, totalTokens = 0;
                if (success)
                {
                    using JsonDocument doc = JsonDocument.Parse(body);
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
                    ResponseText = body,
                    PromptTokens = promptTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens,
                    EstimatedCostUsd = RequestLogManager.EstimateCostUsd(model, promptTokens, outputTokens),
                    Success = success,
                    Status = status,
                    DurationMs = durationMs
                });
            }
            catch { }
        }

        // Race up to parallelCount healthy keys; return the first SUCCESSFUL response and cancel the
        // rest. Falls back to the single-key path when fewer than 2 healthy keys are available (that
        // path also shows the "all keys cooling down" message). Only the first error is surfaced.
        private async Task<string?> TranslateParallelAsync(string jsonData, string prompt, int parallelCount, int attempt = 0)
        {
            string currenServices = ConfigManager.Instance.GetCurrentTranslationService();

            List<string> keys;
            lock (_keySwitchLock)
            {
                keys = ConfigManager.Instance.GetApiKeysList(currenServices)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Select(k => k.Trim())
                    .Distinct()
                    .Where(k => !IsKeyOnCooldownLocked(k))
                    .Take(parallelCount)
                    .ToList();
            }

            // Not enough healthy keys to race -> let the normal path handle it (incl. cooldown UI).
            if (keys.Count < 2)
                return await TranslateAsyncCore(jsonData, prompt, new HashSet<string>());

            string model = ConfigManager.Instance.GetGeminiModel();
            _currentCts = new CancellationTokenSource();
            var raceCts = CancellationTokenSource.CreateLinkedTokenSource(_currentCts.Token);

            // Show how many keys are racing under the "Đang dịch..." spinner.
            try { ChatBoxWindow.Instance?.SetLoadingParallelCount(keys.Count); } catch { }

            var pending = keys.Select(k => AttemptOnceAsync(jsonData, prompt, model, k, raceCts.Token)).ToList();
            GeminiAttemptResult? firstError = null;
            bool anyRateLimited = false;
            bool anyTimeout = false;

            while (pending.Count > 0)
            {
                Task<GeminiAttemptResult> done = await Task.WhenAny(pending);
                pending.Remove(done);
                GeminiAttemptResult r = done.Result; // AttemptOnceAsync never throws

                if (r.Success && r.Response != null)
                {
                    Console.WriteLine($"[Gemini||] winner {MaskApiKey(r.ApiKey)} in {r.DurationMs} ms, canceling {pending.Count} loser(s)");
                    raceCts.Cancel();
                    // Dispose once the aborted losers have unwound (they don't throw).
                    _ = Task.WhenAll(pending).ContinueWith(_ => raceCts.Dispose(), TaskScheduler.Default);
                    _consecutiveFailures = 0;
                    _retryCount = 0;
                    return r.Response;
                }

                if (!r.Canceled)
                {
                    if (IsRateLimitOrQuota(r.Status, r.Error))
                    {
                        anyRateLimited = true;
                        lock (_keySwitchLock) { PutKeyOnCooldownLocked(r.ApiKey); }
                    }
                    if (r.Status == HttpStatusCode.RequestTimeout) anyTimeout = true;
                    firstError ??= r;
                }
            }

            raceCts.Dispose();

            // User pressed Hủy (Cancel) mid-race: every attempt was aborted, so return quietly
            // without an error popup - exactly like the single-key path's cancellation handling.
            if ((_currentCts != null && _currentCts.IsCancellationRequested) || firstError == null)
            {
                Console.WriteLine("[Gemini||] race canceled - no real failure to report");
                return null;
            }

            // All keys timed out (not rate-limited) -> retry the whole race once before giving up.
            if (anyTimeout && !anyRateLimited && attempt < 1)
            {
                Console.WriteLine("[Gemini||] all attempts timed out - retrying the race once");
                return await TranslateParallelAsync(jsonData, prompt, parallelCount, attempt + 1);
            }

            // Every racer failed -> surface a readable error.
            _consecutiveFailures++;
            string detail = firstError != null ? ExtractGeminiErrorMessage(firstError.Error) : "Không có key nào phản hồi.";
            try
            {
                System.IO.File.WriteAllText("gemini_last_error.txt",
                    $"Parallel translate: all {keys.Count} key(s) failed.\nRate-limited: {anyRateLimited}\nDetail: {detail}");
            }
            catch { }
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ChatBoxWindow.Instance?.ShowError(
                    anyRateLimited
                        ? $"Cả {keys.Count} key đua song song đều dính rate limit / lỗi (đã cho nghỉ 12h).\n➕ Thêm API key mới (Google project khác) để dịch tiếp.\n\nChi tiết: {detail}"
                        : $"Cả {keys.Count} key đua song song đều lỗi.\n\nChi tiết: {detail}");
            });
            return null;
        }

        // triedKeys: the API keys already attempted for THIS request. On a quota/
        // rate-limit error we rotate to a key not yet in this set; once every key
        // has been tried we stop (no waiting/looping) and ask the user to add keys.
        private async Task<string?> TranslateAsyncCore(string jsonData, string prompt, HashSet<string> triedKeys)
        {
            string apiKey = ConfigManager.Instance.GetGeminiApiKey();
            string currenServices = ConfigManager.Instance.GetCurrentTranslationService();

            // Don't spend this attempt on a key still resting on its 12h rate-limit
            // cooldown: switch to an available key up front, or stop early (with a clear
            // message) when every key is either already tried or still cooling down.
            bool allKeysCoolingDown = false;
            DateTime earliestFreeUtc = DateTime.MaxValue;
            lock (_keySwitchLock)
            {
                if (IsKeyOnCooldownLocked(apiKey))
                {
                    var keys = ConfigManager.Instance.GetApiKeysList(currenServices);
                    string? fresh = keys.FirstOrDefault(k =>
                        !string.IsNullOrWhiteSpace(k) &&
                        !triedKeys.Contains(k.Trim()) &&
                        !IsKeyOnCooldownLocked(k.Trim()));
                    if (!string.IsNullOrEmpty(fresh))
                    {
                        ConfigManager.Instance.SetGeminiApiKey(fresh);
                        apiKey = fresh;
                    }
                    else
                    {
                        allKeysCoolingDown = true;
                        foreach (var until in _keyCooldownUntil.Values)
                            if (until < earliestFreeUtc) earliestFreeUtc = until;
                    }
                }
            }
            if (allKeysCoolingDown)
            {
                ShowAllKeysCooldownError(earliestFreeUtc);
                return null;
            }

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

                // Get model from config
                string model = ConfigManager.Instance.GetGeminiModel();
                string requestJson = BuildRequestJson(model, prompt, jsonData);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                _currentCts = new CancellationTokenSource();

                // Abort the call once it exceeds the user's max timeout (linked so the user's
                // Cancel still works). A timeout is treated as a retryable error below.
                int timeoutSec = ConfigManager.Instance.GetGeminiRequestTimeoutSec();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_currentCts.Token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                // Measure wall-clock latency of the call so the flaky fast/slow behaviour
                // can be seen in the console and analysed from the request_log table.
                var _latencySw = System.Diagnostics.Stopwatch.StartNew();
                HttpResponseMessage response = await _httpClient.PostAsync(url, content, timeoutCts.Token);
                _latencySw.Stop();
                long durationMs = _latencySw.ElapsedMilliseconds;
                Console.WriteLine($"[Gemini] {model} latency: {durationMs} ms (HTTP {(int)response.StatusCode})");

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
                            Status = "OK",
                            DurationMs = durationMs
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
                            Status = $"HTTP {(int)response.StatusCode} {response.StatusCode}",
                            DurationMs = durationMs
                        });
                    }
                    catch { }

                    // Quota / rate-limit / invalid-key errors: rotate to a key we haven't
                    // tried yet for THIS request. Once every key has been tried and still
                    // fails, stop and tell the user to add new keys - no waiting/looping,
                    // which only deepens the rate limit.
                    if (ShouldSwitchApiKey(response.StatusCode, errorMessage))
                    {
                        triedKeys.Add(apiKey);

                        string? nextKey = null;
                        lock (_keySwitchLock)
                        {
                            // Park the failed key for 12h if it hit a rate limit / quota wall,
                            // so later requests skip this throttled project entirely.
                            if (IsRateLimitOrQuota(response.StatusCode, errorMessage))
                                PutKeyOnCooldownLocked(apiKey);

                            var keys = ConfigManager.Instance.GetApiKeysList(currenServices);
                            nextKey = keys.FirstOrDefault(k =>
                                !string.IsNullOrWhiteSpace(k) &&
                                !triedKeys.Contains(k.Trim()) &&
                                !IsKeyOnCooldownLocked(k.Trim()));
                            if (!string.IsNullOrEmpty(nextKey))
                            {
                                ConfigManager.Instance.SetGeminiApiKey(nextKey);
                                Console.WriteLine($"HTTP {(int)response.StatusCode}: switching to next key {MaskApiKey(nextKey)} ({triedKeys.Count} tried)");
                            }
                        }

                        // A fresh key is available -> retry with it.
                        if (!string.IsNullOrEmpty(nextKey))
                        {
                            return await TranslateAsyncCore(jsonData, prompt, triedKeys);
                        }

                        // Every key exhausted -> stop and ask the user to add more.
                        string quotaMsg = ExtractGeminiErrorMessage(errorMessage);
                        int keyCount = triedKeys.Count;
                        try
                        {
                            System.IO.File.WriteAllText("gemini_last_error.txt",
                                $"Gemini API error: {quotaMsg}\n\nResponse code: {response.StatusCode}\nAll {keyCount} key(s) exhausted.\nFull response: {errorMessage}");
                        }
                        catch { }
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ChatBoxWindow.Instance?.ShowError(
                                $"Tất cả {keyCount} API key đều hết quota / bị chặn (HTTP {(int)response.StatusCode}).\n" +
                                $"➕ Hãy thêm API key mới (mỗi key từ một Google project khác) trong Cài đặt để dịch tiếp.\n\n" +
                                $"Chi tiết: {quotaMsg}");
                        });
                        return null;
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
                            // Always record + surface the error so it can be read (quota/rate-limit
                            // errors need to be visible on the first failure, not after several).
                            System.IO.File.WriteAllText("gemini_last_error.txt", $"Gemini API error: {detailedError}\n\nResponse code: {response.StatusCode}\nFull response: {errorMessage}");
                            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                                ChatBoxWindow.Instance?.ShowError($"{detailedError}\n\n(HTTP {(int)response.StatusCode})");
                                if (_consecutiveFailures > 3)
                                    MainWindow.Instance?.ShowFastNotification(
                                        LocalizationManager.Instance.Strings["Title_GeminiError"],
                                        string.Format(LocalizationManager.Instance.Strings["Msg_GeminiApiError"], detailedError));
                            });
                            await Task.Delay(delayMS);
                            return null;
                        }
                    }
                    catch (JsonException)
                    {
                        // If we can't parse as JSON, just use the raw message
                    }
                    System.IO.File.WriteAllText("gemini_last_error.txt", $"Gemini API error: {response.StatusCode}\n\nFull response: {errorMessage}");
                    System.Windows.Application.Current.Dispatcher.Invoke(() => {
                        ChatBoxWindow.Instance?.ShowError($"HTTP {(int)response.StatusCode}\n\n{errorMessage}");
                        if (_consecutiveFailures > 3)
                            MainWindow.Instance?.ShowFastNotification(
                                LocalizationManager.Instance.Strings["Title_GeminiError"],
                                string.Format(LocalizationManager.Instance.Strings["Msg_GeminiApiErrorStatus"], response.StatusCode, errorMessage));
                    });
                    await Task.Delay(delayMS);
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                // Tell a user-cancel apart from a timeout: user cancel -> silent; timeout -> a
                // real, retryable error (rotate to another key and try again, bounded by triedKeys).
                if (_currentCts != null && _currentCts.IsCancellationRequested)
                {
                    Console.WriteLine("Translation request cancelled by user");
                    return null;
                }

                int tsec = ConfigManager.Instance.GetGeminiRequestTimeoutSec();
                Console.WriteLine($"Gemini request timed out after {tsec}s (key {MaskApiKey(apiKey)})");
                LogAttempt(ConfigManager.Instance.GetGeminiModel(), prompt, jsonData, $"timeout {tsec}s", false, $"TIMEOUT {tsec}s", (long)tsec * 1000);

                triedKeys.Add(apiKey);
                string? nextKey = null;
                lock (_keySwitchLock)
                {
                    var keys = ConfigManager.Instance.GetApiKeysList(currenServices);
                    nextKey = keys.FirstOrDefault(k =>
                        !string.IsNullOrWhiteSpace(k) &&
                        !triedKeys.Contains(k.Trim()) &&
                        !IsKeyOnCooldownLocked(k.Trim()));
                    if (!string.IsNullOrEmpty(nextKey))
                        ConfigManager.Instance.SetGeminiApiKey(nextKey);
                }
                if (!string.IsNullOrEmpty(nextKey))
                    return await TranslateAsyncCore(jsonData, prompt, triedKeys);

                // No other key to try -> surface the timeout so the user can raise it or add keys.
                try
                {
                    System.IO.File.WriteAllText("gemini_last_error.txt",
                        $"Gemini request timed out after {tsec}s. All {triedKeys.Count} key(s) tried.");
                }
                catch { }
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ChatBoxWindow.Instance?.ShowError(
                        $"Request Gemini quá thời gian chờ ({tsec}s) - đã thử hết key.\n" +
                        $"Tăng timeout hoặc thêm API key trong Cài đặt.");
                });
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Translation API error: {ex.Message}");

                // Write error to file
                System.IO.File.WriteAllText("gemini_last_error.txt", $"Gemini API error: {ex.Message}\n\nStack trace: {ex.StackTrace}");

                // Persistent, readable error banner plus the (fast) tray notification.
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    ChatBoxWindow.Instance?.ShowError(ex.Message);
                    MainWindow.Instance?.ShowFastNotification(
                        LocalizationManager.Instance.Strings["Title_GeminiError"],
                        string.Format(LocalizationManager.Instance.Strings["Msg_GeminiApiException"], ex.Message));
                });

                return null;
            }
        }
    }
}