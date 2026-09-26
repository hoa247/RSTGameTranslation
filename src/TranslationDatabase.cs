using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RSTGameTranslation
{
    /// <summary>
    /// Local SQLite store for the translation cache and per-request usage log.
    /// - `translations`: cached raw LLM responses keyed by (profile, source, langs) so revisiting
    ///   a previously translated screen reuses the result instead of calling the LLM again.
    /// - `request_log`: every LLM call (and cache hit) with tokens/cost for statistics.
    /// All operations are best-effort: a DB failure never blocks translation.
    /// </summary>
    public static class TranslationDatabase
    {
        private static SqliteConnection? _conn;
        private static readonly object _lock = new object();
        private static bool _ready;

        public static void Init()
        {
            lock (_lock)
            {
                if (_ready) return;
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rst_data.db");
                    _conn = new SqliteConnection($"Data Source={path}");
                    _conn.Open();

                    Exec(@"CREATE TABLE IF NOT EXISTS translations(
                                profile   TEXT NOT NULL,
                                source    TEXT NOT NULL,
                                src_lang  TEXT NOT NULL,
                                tgt_lang  TEXT NOT NULL,
                                response  TEXT NOT NULL,
                                created_at TEXT NOT NULL,
                                PRIMARY KEY(profile, source, src_lang, tgt_lang));");

                    Exec(@"CREATE TABLE IF NOT EXISTS request_log(
                                time TEXT, day TEXT, profile TEXT, service TEXT, model TEXT,
                                prompt_tokens INTEGER, output_tokens INTEGER, total_tokens INTEGER,
                                cost_usd REAL, success INTEGER, status TEXT, cache_hit INTEGER);");

                    _ready = true;
                    Console.WriteLine($"[DB] ready: {path}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB] init failed: {ex.Message}");
                }
            }
        }

        private static void Exec(string sql)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public static Dictionary<string, string> LoadCache()
        {
            var map = new Dictionary<string, string>();
            lock (_lock)
            {
                if (!_ready) return map;
                try
                {
                    using var cmd = _conn!.CreateCommand();
                    cmd.CommandText = "SELECT profile, source, src_lang, tgt_lang, response FROM translations;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string key = TranslationCache.MakeKey(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
                        map[key] = r.GetString(4);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[DB] LoadCache failed: {ex.Message}"); }
            }
            return map;
        }

        public static void SaveTranslation(string profile, string source, string srcLang, string tgtLang, string response)
        {
            lock (_lock)
            {
                if (!_ready) return;
                try
                {
                    using var cmd = _conn!.CreateCommand();
                    cmd.CommandText = @"INSERT INTO translations(profile, source, src_lang, tgt_lang, response, created_at)
                                        VALUES($p,$s,$sl,$tl,$r,$c)
                                        ON CONFLICT(profile, source, src_lang, tgt_lang)
                                        DO UPDATE SET response=$r, created_at=$c;";
                    cmd.Parameters.AddWithValue("$p", profile);
                    cmd.Parameters.AddWithValue("$s", source);
                    cmd.Parameters.AddWithValue("$sl", srcLang);
                    cmd.Parameters.AddWithValue("$tl", tgtLang);
                    cmd.Parameters.AddWithValue("$r", response);
                    cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("o"));
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex) { Console.WriteLine($"[DB] SaveTranslation failed: {ex.Message}"); }
            }
        }

        public static void InsertRequestLog(RequestLogEntry e, string profile)
        {
            lock (_lock)
            {
                if (!_ready) return;
                try
                {
                    using var cmd = _conn!.CreateCommand();
                    cmd.CommandText = @"INSERT INTO request_log(time, day, profile, service, model,
                                        prompt_tokens, output_tokens, total_tokens, cost_usd, success, status, cache_hit)
                                        VALUES($t,$d,$p,$sv,$m,$pt,$ot,$tt,$c,$s,$st,$ch);";
                    cmd.Parameters.AddWithValue("$t", e.Time.ToString("o"));
                    cmd.Parameters.AddWithValue("$d", e.Time.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$p", profile);
                    cmd.Parameters.AddWithValue("$sv", e.Service);
                    cmd.Parameters.AddWithValue("$m", e.Model);
                    cmd.Parameters.AddWithValue("$pt", e.PromptTokens);
                    cmd.Parameters.AddWithValue("$ot", e.OutputTokens);
                    cmd.Parameters.AddWithValue("$tt", e.TotalTokens);
                    cmd.Parameters.AddWithValue("$c", e.EstimatedCostUsd);
                    cmd.Parameters.AddWithValue("$s", e.Success ? 1 : 0);
                    cmd.Parameters.AddWithValue("$st", e.Status);
                    cmd.Parameters.AddWithValue("$ch", e.CacheHit ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex) { Console.WriteLine($"[DB] InsertRequestLog failed: {ex.Message}"); }
            }
        }

        public class Stats
        {
            public int Requests;
            public int CacheHits;
            public long PromptTokens;
            public long OutputTokens;
            public double CostUsd;
        }

        /// <summary>Aggregate usage. dayFilter = "yyyy-MM-dd" for a single day, or null for all-time.</summary>
        public static Stats GetStats(string? dayFilter, string? profileFilter)
        {
            var s = new Stats();
            lock (_lock)
            {
                if (!_ready) return s;
                try
                {
                    using var cmd = _conn!.CreateCommand();
                    string where = "WHERE 1=1";
                    if (dayFilter != null) { where += " AND day=$d"; cmd.Parameters.AddWithValue("$d", dayFilter); }
                    if (profileFilter != null) { where += " AND profile=$p"; cmd.Parameters.AddWithValue("$p", profileFilter); }
                    cmd.CommandText = $@"SELECT
                            COUNT(*),
                            COALESCE(SUM(cache_hit),0),
                            COALESCE(SUM(prompt_tokens),0),
                            COALESCE(SUM(output_tokens),0),
                            COALESCE(SUM(cost_usd),0)
                        FROM request_log {where};";
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        s.Requests = r.GetInt32(0);
                        s.CacheHits = r.GetInt32(1);
                        s.PromptTokens = r.GetInt64(2);
                        s.OutputTokens = r.GetInt64(3);
                        s.CostUsd = r.GetDouble(4);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[DB] GetStats failed: {ex.Message}"); }
            }
            return s;
        }
    }
}
