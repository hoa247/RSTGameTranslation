using System;
using System.Collections.Generic;
using System.Linq;

namespace RSTGameTranslation
{
    /// <summary>
    /// One captured LLM call: what was sent, what came back, token usage and estimated cost.
    /// </summary>
    public class RequestLogEntry
    {
        public DateTime Time { get; set; }
        public string Service { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string RequestText { get; set; } = string.Empty;   // params/prompt actually sent
        public string ResponseText { get; set; } = string.Empty;  // raw response body
        public int PromptTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }
        public double EstimatedCostUsd { get; set; }
        public bool Success { get; set; }
        public bool CacheHit { get; set; }                        // served from local cache (no LLM call, $0)
        public string Status { get; set; } = string.Empty;        // "OK", "CACHE HIT", or an error string

        public string TimeText => Time.ToString("HH:mm:ss");
        public string CostText => EstimatedCostUsd <= 0 ? "~$0" : $"${EstimatedCostUsd:F6}";
        public string TokensText => $"{PromptTokens} in / {OutputTokens} out";
    }

    /// <summary>
    /// In-memory log of LLM requests/responses for the Debug window. Also tracks running totals so
    /// the user can see whether translation is costing money. Thread-safe; capped to avoid unbounded growth.
    /// </summary>
    public static class RequestLogManager
    {
        private const int MAX_ENTRIES = 500;
        private static readonly object _lock = new object();
        private static readonly List<RequestLogEntry> _entries = new List<RequestLogEntry>();

        public static int TotalRequests { get; private set; }
        public static long TotalPromptTokens { get; private set; }
        public static long TotalOutputTokens { get; private set; }
        public static double TotalCostUsd { get; private set; }

        public static void Add(RequestLogEntry entry)
        {
            lock (_lock)
            {
                _entries.Add(entry);
                TotalRequests++;
                TotalPromptTokens += entry.PromptTokens;
                TotalOutputTokens += entry.OutputTokens;
                TotalCostUsd += entry.EstimatedCostUsd;

                while (_entries.Count > MAX_ENTRIES)
                    _entries.RemoveAt(0);
            }

            // Persist for cross-session statistics (best-effort, never blocks).
            TranslationDatabase.InsertRequestLog(entry, TranslationCache.CurrentProfile());
        }

        /// <summary>Newest-first snapshot for display.</summary>
        public static List<RequestLogEntry> GetEntries()
        {
            lock (_lock)
            {
                return _entries.AsEnumerable().Reverse().ToList();
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                TotalRequests = 0;
                TotalPromptTokens = 0;
                TotalOutputTokens = 0;
                TotalCostUsd = 0;
            }
        }

        /// <summary>
        /// Rough USD cost from token counts. Rates are per 1M tokens and are best-effort defaults for
        /// Gemini Flash-Lite class models; adjust if Google changes pricing. Free-tier usage costs $0
        /// but this shows what the same volume would cost on the paid tier.
        /// </summary>
        public static double EstimateCostUsd(string model, int promptTokens, int outputTokens)
        {
            double inputPerM = 0.10;
            double outputPerM = 0.40;

            string m = (model ?? string.Empty).ToLowerInvariant();
            if (m.Contains("flash-lite"))
            {
                inputPerM = 0.10; outputPerM = 0.40;
            }
            else if (m.Contains("flash"))
            {
                inputPerM = 0.30; outputPerM = 2.50;
            }
            else if (m.Contains("pro"))
            {
                inputPerM = 1.25; outputPerM = 10.0;
            }

            return (promptTokens / 1_000_000.0 * inputPerM) + (outputTokens / 1_000_000.0 * outputPerM);
        }
    }
}
