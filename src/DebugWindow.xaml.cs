using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RSTGameTranslation
{
    /// <summary>
    /// Debug window: image-save options, the raw LLM request/response + token/cost log,
    /// and the full translation history. Read-only views refreshed on demand.
    /// </summary>
    public partial class DebugWindow : Window
    {
        private bool _loading;

        public DebugWindow()
        {
            InitializeComponent();

            _loading = true;
            saveAllCapturesCheck.IsChecked = MainWindow.Instance.DebugSaveAllCaptures;
            _loading = false;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            logPathText.Text = "Log phiên hiện tại: " + (DebugFileLogger.CurrentLogPath ?? Path.Combine(baseDir, "logs"));

            RefreshLog();
            RefreshHistory();
            RefreshStats();
        }

        // ---------- Statistics ----------

        private void RefreshStats()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            string profile = TranslationCache.CurrentProfile();

            cacheCountText.Text = $"Cache: {TranslationCache.Count} câu đã lưu | Game: {profile}";
            statsTodayText.Text = FormatStats(TranslationDatabase.GetStats(today, null));
            statsProfileText.Text = FormatStats(TranslationDatabase.GetStats(today, profile));
            statsAllText.Text = FormatStats(TranslationDatabase.GetStats(null, null));
        }

        private static string FormatStats(TranslationDatabase.Stats s)
            => $"Requests: {s.Requests}  (cache hit: {s.CacheHits})\n" +
               $"Tokens: {s.PromptTokens} in + {s.OutputTokens} out\n" +
               $"Chi phí ước tính: ${s.CostUsd:F6}";

        private void RefreshStats_Click(object sender, RoutedEventArgs e) => RefreshStats();

        private void SaveAllCaptures_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool on = saveAllCapturesCheck.IsChecked == true;
            MainWindow.Instance.DebugSaveAllCaptures = on;
            ConfigManager.Instance.SetValue("debug_save_all_captures", on ? "true" : "false");
            ConfigManager.Instance.SaveConfig();
        }

        private void OpenCapturesFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webserver", "debug_captures");
            Directory.CreateDirectory(dir);
            OpenFolder(dir);
        }

        private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            OpenFolder(dir);
        }

        private void OpenFolder(string dir)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Không mở được thư mục:\n{ex.Message}", "Debug", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- Request/Response log ----------

        private void RefreshLog()
        {
            logList.ItemsSource = RequestLogManager.GetEntries();
            summaryText.Text =
                $"Tổng: {RequestLogManager.TotalRequests} request | " +
                $"{RequestLogManager.TotalPromptTokens} in + {RequestLogManager.TotalOutputTokens} out tokens | " +
                $"ước tính ${RequestLogManager.TotalCostUsd:F6}";
        }

        private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            RequestLogManager.Clear();
            requestBox.Clear();
            responseBox.Clear();
            RefreshLog();
        }

        private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (logList.SelectedItem is RequestLogEntry entry)
            {
                // In batch (non-manga) mode all sentences of one screen go in a single request,
                // joined by "##|||##". Split them into a readable numbered list so the sentences
                // that share one request are grouped clearly in one place.
                string req = entry.RequestText ?? string.Empty;
                if (req.Contains("##|||##"))
                {
                    var parts = req.Split(new[] { "##|||##" }, StringSplitOptions.None);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"── {parts.Length} câu trong 1 request ──");
                    for (int i = 0; i < parts.Length; i++)
                        sb.AppendLine($"{i + 1}. {parts[i].Trim()}");
                    sb.AppendLine();
                    sb.AppendLine("── Params gốc gửi đi ──");
                    sb.Append(req);
                    requestBox.Text = sb.ToString();
                }
                else
                {
                    requestBox.Text = req;
                }
                responseBox.Text = entry.ResponseText;
            }
        }

        // ---------- Translation history ----------

        private void RefreshHistory()
        {
            var history = MainWindow.Instance.GetFullTranslationHistory();
            historyList.ItemsSource = history;
            historyCountText.Text = $"Tổng {history.Count} bản dịch (mới nhất ở trên)";
        }

        private void RefreshHistory_Click(object sender, RoutedEventArgs e) => RefreshHistory();
    }
}
