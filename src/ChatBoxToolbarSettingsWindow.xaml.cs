using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DragDrop = System.Windows.DragDrop;

namespace RSTGameTranslation
{
    // Lets the user pick which ChatBox toolbar buttons are visible and in what order.
    // Visibility via checkbox; order via drag-and-drop. Persisted as a comma-separated id list.
    public partial class ChatBoxToolbarSettingsWindow : Window
    {
        public class ToolbarItemVm
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public bool IsVisible { get; set; }
        }

        private readonly ObservableCollection<ToolbarItemVm> _items = new();
        private ToolbarItemVm? _dragged;
        private Point _startPoint;

        public ChatBoxToolbarSettingsWindow()
        {
            InitializeComponent();
            LoadItems();
            itemsList.ItemsSource = _items;
        }

        // Build the list: visible items first (in saved order), then the remaining actions (unchecked).
        private void LoadItems()
        {
            _items.Clear();

            var order = ConfigManager.Instance.GetChatBoxToolbarOrder()
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            var names = ChatBoxWindow.ToolbarCatalog.ToDictionary(k => k.Key, v => v.Value);

            foreach (var id in order)
            {
                if (names.TryGetValue(id, out var nm))
                    _items.Add(new ToolbarItemVm { Id = id, Name = nm, IsVisible = true });
            }

            foreach (var kv in ChatBoxWindow.ToolbarCatalog)
            {
                if (!order.Contains(kv.Key))
                    _items.Add(new ToolbarItemVm { Id = kv.Key, Name = kv.Value, IsVisible = false });
            }
        }

        private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(null);
            if (sender is ListBoxItem lbi && lbi.DataContext is ToolbarItemVm vm)
                _dragged = vm;
        }

        private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragged == null) return;

            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _startPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _startPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            DragDrop.DoDragDrop(itemsList, _dragged, DragDropEffects.Move);
        }

        // Drop onto another row -> move dragged item to that row's position.
        private void Item_Drop(object sender, DragEventArgs e)
        {
            if (_dragged != null &&
                sender is ListBoxItem lbi &&
                lbi.DataContext is ToolbarItemVm target &&
                !ReferenceEquals(target, _dragged))
            {
                int oldIndex = _items.IndexOf(_dragged);
                int newIndex = _items.IndexOf(target);
                if (oldIndex >= 0 && newIndex >= 0)
                    _items.Move(oldIndex, newIndex);
            }
            _dragged = null;
            e.Handled = true;
        }

        // Drop on empty space -> move to the end.
        private void ItemsList_Drop(object sender, DragEventArgs e)
        {
            if (_dragged != null)
            {
                int oldIndex = _items.IndexOf(_dragged);
                if (oldIndex >= 0)
                    _items.Move(oldIndex, _items.Count - 1);
            }
            _dragged = null;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            ConfigManager.Instance.SetChatBoxToolbarOrder(ConfigManager.DEFAULT_CHATBOX_TOOLBAR_ORDER);
            LoadItems();
            ChatBoxWindow.Instance?.ApplyToolbarLayout();
            statusText.Text = "Đã đặt lại mặc định ✓";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var visibleIds = _items.Where(i => i.IsVisible).Select(i => i.Id);
            ConfigManager.Instance.SetChatBoxToolbarOrder(string.Join(",", visibleIds));
            ChatBoxWindow.Instance?.ApplyToolbarLayout();
            statusText.Text = "Đã lưu ✓ — thanh nút đã cập nhật";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
