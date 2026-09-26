using System;
using System.Collections.Generic;

namespace RSTGameTranslation
{
    /// <summary>
    /// In-memory translation cache (backed by <see cref="TranslationDatabase"/>) keyed by
    /// game profile + source text + language pair. Lets a previously translated screen be reused
    /// without another LLM call. The cached value is the raw LLM response so the existing
    /// response-parsing path works unchanged on a cache hit.
    /// </summary>
    public static class TranslationCache
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _map = new Dictionary<string, string>();
        private static bool _loaded;

        // Unit separator keeps the key parts unambiguous even if text contains '|'.
        internal static string MakeKey(string profile, string source, string srcLang, string tgtLang)
            => $"{profile}␟{srcLang}␟{tgtLang}␟{source}";

        /// <summary>The cache partition for the current game (its game_info description, or "default").</summary>
        public static string CurrentProfile()
        {
            try
            {
                string g = ConfigManager.Instance.GetGameInfo()?.Trim() ?? "";
                return string.IsNullOrEmpty(g) ? "default" : g;
            }
            catch { return "default"; }
        }

        public static void Load()
        {
            lock (_lock)
            {
                if (_loaded) return;
                try { _map = TranslationDatabase.LoadCache(); }
                catch { _map = new Dictionary<string, string>(); }
                _loaded = true;
                Console.WriteLine($"[Cache] loaded {_map.Count} cached translations");
            }
        }

        /// <summary>Returns the cached raw response for this source text, or null on a miss.</summary>
        public static string? Lookup(string source, string srcLang, string tgtLang)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            string key = MakeKey(CurrentProfile(), source, srcLang, tgtLang);
            lock (_lock)
            {
                return _map.TryGetValue(key, out var resp) ? resp : null;
            }
        }

        /// <summary>Stores a new translation in memory and persists it (write-through).</summary>
        public static void Save(string source, string srcLang, string tgtLang, string response)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrEmpty(response)) return;
            string profile = CurrentProfile();
            string key = MakeKey(profile, source, srcLang, tgtLang);
            lock (_lock)
            {
                _map[key] = response;
            }
            TranslationDatabase.SaveTranslation(profile, source, srcLang, tgtLang, response);
        }

        public static int Count
        {
            get { lock (_lock) { return _map.Count; } }
        }
    }
}
