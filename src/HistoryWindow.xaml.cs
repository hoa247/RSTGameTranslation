using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace RSTGameTranslation
{
    /// <summary>
    /// Clean reading view of every translation this session — original (bold) above its
    /// translation (muted) — for reviewing earlier context and studying the language.
    /// Auto-refreshes while playing and supports quick text filtering.
    /// </summary>
    public partial class HistoryWindow : Window
    {
        private readonly DispatcherTimer _autoRefreshTimer;
        private int _lastCount = -1;

        public HistoryWindow()
        {
            InitializeComponent();

            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _autoRefreshTimer.Tick += (s, e) =>
            {
                if (autoRefreshCheck.IsChecked == true && string.IsNullOrEmpty(searchBox.Text))
                    Refresh();
            };
            _autoRefreshTimer.Start();

            Closed += (s, e) => _autoRefreshTimer.Stop();

            Refresh();
        }

        private void Refresh()
        {
            var all = MainWindow.Instance.GetFullTranslationHistory(); // newest-first
            string q = searchBox.Text?.Trim() ?? string.Empty;

            List<TranslationEntry> items = string.IsNullOrEmpty(q)
                ? all
                : all.Where(x =>
                        (x.OriginalText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (x.TranslatedText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                     .ToList();

            // Avoid resetting the list (and scroll position) when nothing changed during auto-refresh.
            if (string.IsNullOrEmpty(q) && items.Count == _lastCount) return;
            _lastCount = string.IsNullOrEmpty(q) ? items.Count : -1;

            historyList.ItemsSource = items;
            countText.Text = string.IsNullOrEmpty(q)
                ? $"Tổng {all.Count} bản dịch (mới nhất ở trên)"
                : $"{items.Count}/{all.Count} khớp \"{q}\"";
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _lastCount = -1; // force re-filter
            Refresh();
        }
    }
}
