# RST — Notes phiên UI polish + Thanh nút tùy biến (2026-09-26, chiều)

Nhánh: `fix/region-crop-wgc-scaling` · nối tiếp phiên trước (`4f8a27c`).
Tất cả **compile 0 lỗi** (`dotnet build RST.csproj`). Build/chạy bằng `build-and-run.bat`.

---

## Commit phiên này (mới → cũ)
```
c50f8db feat: customizable ChatBox toolbar (show/hide + drag-drop reorder, all actions configurable)
5710df8 feat: add 'show selected area' option to on-Start auto-enable group
854b063 feat: add 'Chọn vùng' (Select Area / Alt+Q) button to chatbox header
f5e6ed1 style: unify toggle colors (green on / gray off), calmer experimental box, wider Settings + ChatBox
c7eff49 feat: on-Start auto-enable options (checkboxes) + remove chatbox one-off area button
a856527 feat: UI polish — labeled chatbox buttons, single-row settings tabs, en→vi default + reset, move STT to experimental box, drop purple
```

---

## 1. Đổi màu / UI rõ ràng
- **Toggle thống nhất: BẬT = xanh `#14b414 (20,180,20)`, TẮT = xám `#6b7280 (107,114,128)`.** Áp cho 🎤 Dịch âm thanh (STT) + 🔊 Đọc bản dịch (TTS), cả XAML default lẫn code-behind (`UpdateTtsButtonUI`, `UpdateAudioServiceButtonUI` trong `MainWindow.xaml.cs`).
- **Bỏ màu tím khó nhìn:** Lớp phủ (monitor) tím `#8b5cf6`→xanh `#3b82f6`; Nhật ký (log) tím/hồng → cyan `#0891b2`/`#0e7490` (cả code-behind `ToggleConsoleWindow`, `updateLogButtonState`).
- **Ô "🧪 Thử nghiệm"** (chứa STT): bỏ viền/nhãn vàng WarningBrush → về style thường (bớt chói).
- STT đã tách khỏi nhóm Điều khiển sang ô "🧪 Thử nghiệm" riêng (từ commit a856527).

## 2. Ngôn ngữ mặc định + reset
- `ConfigManager`: default nguồn `ja→en`, đích `en→vi`.
- Nút **↺ Mặc định (English → Tiếng Việt)** trong Settings→tab Ngôn ngữ (`ResetLanguageDefaultsButton_Click` + helper `SelectLanguageByCode`).

## 3. Settings + ChatBox rộng ra
- **Settings**: `TabControl.Template` mới → tab headers 1 hàng, cuộn ngang khi chật (không wrap nhiều hàng). Window `600→1180px` (cao `550→600`).
- **ChatBox**: `Width 400→980`, thêm `MinWidth=940` (kẹp cả cửa sổ cũ đã lưu hẹp). Config `CHATBOX_POSITION_WIDTH` default `400→980`.

## 4. "Khi nhấn ▶ Bắt đầu" — auto-enable (Settings → tab Hồ sơ Game, nhóm trên cùng)
- 5 checkbox, tick cái nào thì mỗi lần bấm Bắt đầu tự bật: 🎤 STT · 🔊 TTS · 💬 ChatBox · 📺 Overlay · ▦ Hiện vùng đang chọn.
- Config keys: `start_auto_audio/tts/chatbox/overlay/showarea` (getter/setter trong `ConfigManager`).
- Logic: `MainWindow.ApplyStartAutoEnablesAsync()` gọi cuối nhánh Start (isReady). **Idempotent** — chỉ bật khi đang tắt. Overlay tôn trọng guard Win10. Show-area set thẳng `MonitorWindow.BorderThickness=1`.
- Handler settings: `StartAutoOption_Changed` (SettingsWindow.xaml.cs).

## 5. ChatBox — nút chọn vùng
- **Bỏ** nút one-off "＋ Vùng lạ" (`OneOffAreaButton_Click` xóa).
- **Thêm** "⬚ Chọn vùng" = Alt+Q → `KeyboardShortcuts.InvokeFunctionFromClick("Select Area")` (`SelectAreaButton_Click`).
- Các nút icon-only đã thêm chữ từ trước: 🔊 Đọc, ↻ Dịch lại, ✕ Hủy, ▦ Vùng, A+/A-.

## 6. ⭐ Thanh nút ChatBox TÙY BIẾN (commit c50f8db) — tính năng chính
**Mở:** ChatBox → Tùy chọn → **🧰 Tùy chỉnh thanh nút**.
- Hộp thoại `ChatBoxToolbarSettingsWindow` (.xaml/.cs mới): **tick hiện/ẩn** + **kéo–thả đổi thứ tự** (ListBox drag-drop). Nút 💾 Lưu (áp ngay) · ↺ Mặc định · Đóng.
- **14 chức năng** trong catalog: selectArea, showArea, cancel, retry, tts, startStop, history, options, mode, clear, fontDecrease, fontIncrease, **overlay (📺)**, **audio/STT (🎤)** — 2 cái cuối mặc định ẩn.

**Cơ chế (quan trọng cho maintain):**
- Header ChatBox đổi từ DockPanel-các-nút → `StackPanel x:Name="toolbarPanel"` chứa label + tất cả nút. Nút X (đóng) vẫn `DockPanel.Dock=Right` cố định.
- Mỗi nút có `Tag="<id>"` ổn định. **Giữ nguyên instance nút gốc** (x:Name + handler) — không tạo lại → code cập nhật trạng thái (Start/Dừng timer, TTS, mode) vẫn chạy.
- `ChatBoxWindow.ApplyToolbarLayout()`: đọc config `chatbox_toolbar_order` (CSV id có thứ tự) → collapse hết + detach khỏi panel (giữ label) → re-add đúng thứ tự các id visible. Gọi ở đầu `ChatBoxWindow_Loaded` + sau khi Lưu.
- `ChatBoxWindow.ToolbarCatalog` (static `List<KeyValuePair<string,string>>` id→tên) — nguồn cho hộp thoại.
- Config: `CHATBOX_TOOLBAR_ORDER` + `DEFAULT_CHATBOX_TOOLBAR_ORDER` + `Get/SetChatBoxToolbarOrder` (ConfigManager).
- 2 handler mới ChatBox: `OverlayButton_Click`→"Overlay", `AudioButton_Click`→"Audio Service".

---

## File đã đụng
- `MainWindow.xaml` / `.xaml.cs` — màu toggle, ô Thử nghiệm, auto-enable-on-Start, ApplyStartAutoEnablesAsync.
- `ChatBoxWindow.xaml` / `.xaml.cs` — header data-driven, ApplyToolbarLayout, catalog, nút Chọn vùng/Overlay/Audio, width.
- `SettingsWindow.xaml` / `.xaml.cs` — tab 1 hàng, reset ngôn ngữ, 5 checkbox on-Start, width.
- `ConfigManager.cs` — default ngôn ngữ, start_auto_*, chatbox_toolbar_order, chatbox width default.
- `ChatBoxOptionsWindow.xaml` / `.xaml.cs` — nút 🧰 mở customize.
- `ChatBoxToolbarSettingsWindow.xaml` / `.xaml.cs` — **MỚI**, hộp thoại tùy biến (drag-drop + checkbox).

## Lưu ý kỹ thuật
- Project import cả WinForms + WPF → `Button`, `Point`, `MouseEventArgs`, `DragEventArgs`, `DragDropEffects`, `DragDrop` **bị ambiguous** → phải qualify đầy đủ hoặc alias `System.Windows.*` (đã làm trong ChatBoxToolbarSettingsWindow.xaml.cs bằng using-alias).
- Compile-check: build ra thư mục temp (tránh khóa exe đang chạy):
  `"/c/Program Files/dotnet/dotnet.exe" build RST.csproj -c Debug -o "<temp>"`.
- Runtime files KHÔNG commit: `app/*_config.txt`, `app/webserver/image_to_process.png`, `gemini_last_error.txt`.

## Cần TEST thực tế (compile OK, chưa chạy thử)
- [ ] Toggle STT/TTS: tắt xám hẳn, bật xanh.
- [ ] Settings mở 1180px, tab 1 hàng không phải scroll.
- [ ] ChatBox mở đủ rộng hiện hết nút.
- [ ] On-Start: tick vài mục → bấm Bắt đầu tự bật đúng (kể cả Hiện vùng).
- [ ] Nút "⬚ Chọn vùng" ChatBox = Alt+Q.
- [ ] 🧰 Tùy chỉnh thanh nút: tick hiện/ẩn + kéo–thả đổi thứ tự + Lưu → thanh cập nhật đúng; mở lại app vẫn giữ.

## Câu hỏi mở
- Nhãn/tooltip nút hardcode tiếng Việt (chưa qua LocalizationManager) — đổi ngôn ngữ giao diện sẽ không dịch.
- "Dịch 1 lần rồi tự về vùng cũ" (one-off) đã bỏ; nếu cần lại thì thêm nút riêng tách với "Chọn vùng" (cố định).
