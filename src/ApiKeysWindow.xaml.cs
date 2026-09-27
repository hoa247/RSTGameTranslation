using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace RSTGameTranslation
{
    public partial class ApiKeysWindow : Window
    {
        // Masked keys shown in the ListView.
        private readonly ObservableCollection<string> _displayKeys = new ObservableCollection<string>();
        // The real (unmasked) keys, kept in sync with what's saved in config.
        private List<string> _actualKeys = new List<string>();
        private readonly string _serviceType;

        public ApiKeysWindow(string serviceType, List<string> apiKeys)
        {
            InitializeComponent();

            _serviceType = serviceType;
            Title = $"{_serviceType} API Keys";

            _actualKeys = apiKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();
            apiKeysListView.ItemsSource = _displayKeys;
            RefreshDisplay();
        }

        // Rebuild the masked display list + count from the actual keys.
        private void RefreshDisplay()
        {
            _displayKeys.Clear();
            foreach (var key in _actualKeys)
                _displayKeys.Add(MaskApiKey(key));
            if (keyCountText != null)
                keyCountText.Text = $"{_actualKeys.Count} key";
        }

        // Add many keys at once. Accepts one key per line or comma/space separated.
        private void AddKeysButton_Click(object sender, RoutedEventArgs e)
        {
            string raw = bulkKeysTextBox.Text ?? "";
            if (string.IsNullOrWhiteSpace(raw))
                return;

            var candidates = raw.Split(new[] { '\r', '\n', ',', ' ', '\t', ';' },
                                       StringSplitOptions.RemoveEmptyEntries)
                                .Select(k => k.Trim())
                                .Where(k => k.Length > 0);

            int added = 0, skipped = 0;
            foreach (var key in candidates)
            {
                if (_actualKeys.Contains(key))
                {
                    skipped++;
                    continue;
                }
                _actualKeys.Add(key);
                added++;
            }

            if (added > 0)
            {
                ConfigManager.Instance.SaveApiKeysList(_serviceType, _actualKeys);
                bulkKeysTextBox.Text = "";
                RefreshDisplay();
            }

            System.Windows.MessageBox.Show(
                $"Đã thêm {added} key mới" + (skipped > 0 ? $", bỏ qua {skipped} key trùng." : ".") +
                $"\nTổng cộng: {_actualKeys.Count} key. App sẽ tự xoay vòng khi gặp rate limit.",
                "Thêm API key",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // Mask API key for display (show only first 4 and last 4 characters)
        private string MaskApiKey(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey) || apiKey.Length <= 8)
                return apiKey;
            return apiKey.Substring(0, 4) + "..." + apiKey.Substring(apiKey.Length - 4);
        }

        // Remove the selected API key
        private void ClearSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            int selectedIndex = apiKeysListView.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= _actualKeys.Count)
                return;

            MessageBoxResult result = System.Windows.MessageBox.Show(
                LocalizationManager.Instance.Strings["Msg_ConfirmRemoveKey"],
                LocalizationManager.Instance.Strings["Title_ConfirmRemoval"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _actualKeys.RemoveAt(selectedIndex);
                ConfigManager.Instance.SaveApiKeysList(_serviceType, _actualKeys);
                RefreshDisplay();
            }
        }

        // Remove all API keys
        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_actualKeys.Count == 0)
                return;

            MessageBoxResult result = System.Windows.MessageBox.Show(
                string.Format(LocalizationManager.Instance.Strings["Msg_ConfirmRemoveAllKeys"], _serviceType),
                LocalizationManager.Instance.Strings["Title_ConfirmRemoval"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _actualKeys.Clear();
                ConfigManager.Instance.SaveApiKeysList(_serviceType, new List<string>());
                RefreshDisplay();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
