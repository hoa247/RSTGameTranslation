# RST — Notes phiên UI (phần 2, 2026-09-26 chiều)

Nhánh: `fix/region-crop-wgc-scaling` · nối tiếp notes `ui-customization-session-260926-1503-...` (commit `a9890db`).
Tất cả **compile 0 lỗi** (`dotnet build RST.csproj`). Build/chạy: `build-and-run.bat`.

---

## Commit phần 2 (mới → cũ)
```
9f9bae3 fix: combine all blocks of one translation pass into a single chatbox entry (show-only-latest shows full latest translation)
51f43e8 fix: auto-clear timer now respects the 'auto-clear history' checkbox (off = never clears)
52e60c5 style: lower chatbox min size so it can be shrunk freely (header wraps)
2315061 feat: chatbox text options — alignment, padding, entry spacing, show-only-latest (auto-clear previous)
38a7374 chore: rename build-and-run.bat to zbuild-and-run.bat
705de74 style: taller settings window + larger hotkey list to reduce scrolling
53b7192 feat: per-function enable/disable in settings (hotkey unregistered + toolbar button greyed when off)
b9fe562 feat: loading overlay (spinner) covers old text while translating; reveals result on arrival
c297f0b feat: bottom-right translating/stopped status indicator (dot + colored text) in chatbox
b11d659 feat: color chatbox Start/Stop button green (translating) / red (stopped)
784b516 feat: drop-target highlight (accent line + faint fill) in toolbar customize dialog
8d23bf4 feat: hotkey key picker supports arrows, numpad, F1-F12 and navigation keys
6615abe fix: restored session shows area border immediately; focus captured window on Start
d5f4b24 feat: validate capture source before Start; ensure Esc cancels area selection (focus grab)
87731db feat: add one-off translate action (pick a region, translate once) to chatbox toolbar
639b5a1 fix: cache all toolbar buttons so re-enabling a hidden button shows it after save
32d0fde feat: add move-to-top / move-to-bottom buttons in toolbar customize dialog
742bd8e feat: add all hotkey actions to chatbox toolbar, wrap to multiple rows, move customize to top of options
```

---

## 1. Thanh nút ChatBox tùy biến — mở rộng
- **Thêm toàn bộ hành động phím tắt** vào catalog (giờ ~25 mục). 12 cái mới dùng chung handler `HotkeyProxyButton_Click` → map `Tag`→function trong `KeyboardShortcuts.InvokeFunctionFromClick`:
  toggleChatBox(ChatBox), settings(Setting), log(Log), swapLang(Swap Languages), clearAreas(Clear Areas), clearSelectedArea(Clear Selected Area), excludeRegion(Select Exclude Region), area1..area5(Area 1..5). Mặc định ẩn.
- **oneOff (🎯 Dịch 1 lần)**: chọn 1 vùng bất kỳ → dịch DUY NHẤT 1 lần, không đổi vùng mặc định. Gọi `MainWindow.StartOneOffAreaSelection()`. Đã đưa vào DEFAULT order (cạnh selectArea).
- **Wrap nhiều hàng**: header đổi StackPanel → **WrapPanel**, `MinHeight=28` cao tự động; `HeaderBar_SizeChanged` cập nhật lề `chatScrollViewer` + `cbLoadingOverlay` + nút toggle-borders theo chiều cao header (nút nhiều/thu hẹp → tự xuống hàng, nội dung không bị đè).
- **Hộp thoại 🧰 Tùy chỉnh** (`ChatBoxToolbarSettingsWindow`):
  - Nút **⤒ lên đầu / ⤓ xuống cuối** mỗi dòng (ngoài kéo–thả).
  - **Highlight dòng đích khi kéo** (viền accent trên + nền xanh nhạt) — `DragEnter/DragOver/DragLeave` + `SetDropHighlight`.
  - Đưa mục "🧰 Tùy chỉnh thanh nút" **lên trên cùng** cửa sổ Tùy chọn ChatBox.
- **BUG FIX quan trọng** (`639b5a1`): `ApplyToolbarLayout` lần đầu xóa nút ẩn khỏi cây → mất tham chiếu, bật lại không hiện. Sửa: **cache `_toolbarButtons` (tất cả nút theo Tag)** ngay lần đầu, dùng lại về sau.

## 2. Phím tắt — chọn được nhiều phím
- `combineKey2` (SettingsWindow.xaml) thêm: **mũi tên** (LEFT/RIGHT/UP/DOWN), **numpad NUM0–9**, **F1–F12**, SPACE/TAB/ENTER/INSERT/DELETE/HOME/END/PAGEUP/PAGEDOWN, và chữ **S** (trước bị sót).
- `KeyboardShortcuts._keyCodeMap`: thêm numpad `NUM0..9` → VK `0x60..0x69` (arrows + F-keys đã có sẵn). Không thêm numpad +/−/*/÷ vì ký tự "+" trùng dấu phân tách config "ALT+...".

## 3. Start / vùng chọn
- **Validate khi Start** (`OnStartButtonToggleClicked`, sau `TryRestoreSession`): nếu `!isCapturingWindow && !hasSelectedTranslationArea` → `ShowFastNotification` "Chưa thể bắt đầu..." và **return** (không chụp full màn).
- **ESC hủy chọn vùng**: `TranslationAreaSelectorWindow.OnKeyDown` vốn có; thêm `Loaded → Activate()/Focus()/Keyboard.Focus` để chắc chắn nhận phím.
- **Restore session hiện viền ngay** (`6615abe`): `RestoreSessionAreas` giờ set `selectedTranslationArea`, gọi `UpdateCustomCaptureRect()` + `MonitorWindow.RefreshOverlays()` + nút Chọn vùng xanh (trước phải chọn lại mới hiện viền đỏ).
- **Focus cửa sổ game khi Start**: thêm P/Invoke `SetForegroundWindow` + `SW_RESTORE`; `FocusCapturedWindow()` gọi cuối nhánh Start.

## 4. Trạng thái dịch (ChatBox) — trực quan
- Nút **Start/Dừng** đổi màu: 🟢 xanh = đang dịch, 🔴 đỏ = đã dừng (trong `UpdateStartStopButton`, timer 500ms).
- **Badge góc phải dưới**: chấm + chữ 🟢"Đang dịch" / 🔴"Đã dừng" (`cbStatusIndicator`/`cbStatusDot`/`cbStatusLabel`), nền bo tròn tối cho dễ đọc.
- **Màn loading khi đang dịch** (`b9fe562`): `cbLoadingOverlay` phủ vùng chat che text cũ + **spinner xoay** (`cbSpinnerRotate` + `_loadingSpinnerTimer` 28ms) + "Đang dịch..." + tên service. Hook: `ShowTranslationStatus(false)`→`ShowLoading`; `HideTranslationStatus()`→`HideLoading` (đã được `OnTranslationWasAdded` gọi, bao cả lỗi/hủy). → clear-then-show, dễ nhận biết dịch xong.

## 5. Bật / tắt hẳn từng chức năng (`53b7192`)
- **Settings → tab Phím tắt → nhóm "Bật / tắt chức năng"**: 18 checkbox (Start/Stop, Overlay, ChatBox, Setting, Log, Select/Show/Clear Area(s), Clear Selected Area, Select Exclude Region, Audio Service, Swap Languages, Retry Translation, Area 1–5). Bỏ tick = **tắt hẳn**.
- Khi tắt: (a) **hotkey KHÔNG đăng ký** → phím lọt xuống game (vd Alt+V thôi đổi ngôn ngữ); (b) **nút tương ứng trên thanh ChatBox mờ + `IsEnabled=false`** (opacity 0.4); (c) chặn mọi ngả qua `DispatchFunction`.
- Config: `disabled_functions` (CSV) + `ConfigManager.IsFunctionEnabled/SetFunctionEnabled`.
- `KeyboardShortcuts`: gate ở `RegisterFunctionHotkey` (skip đăng ký) + `DispatchFunction` (chặn invoke).
- Handler `FunctionEnabledCheckBox_Changed` (SettingsWindow.xaml.cs, `functionToggleWrap`): lưu config → `RefreshHotkeys()` + `SetMainWindowHandle` + `ChatBoxWindow.ApplyToolbarLayout()` (cập nhật mờ nút ngay).
- DRY: gộp `_tagToFunction` (ChatBoxWindow) dùng cho cả dispatch proxy button lẫn greying (thay cho `_proxyTagToFunction` cũ).
- **Lưu ý:** greying chỉ áp cho thanh nút ChatBox; nút ở màn hình chính (Overlay/Cài đặt...) chưa greyed (là click chủ động, ít bấm nhầm).

## 6. Tùy chọn văn bản ChatBox + fix hiển thị (`2315061`, `51f43e8`, `9f9bae3`, `52e60c5`, `705de74`)
- **Cài đặt văn bản mới** (ChatBox → Tùy chọn → nhóm "Cài đặt văn bản"), áp dụng **live**:
  - Căn lề chữ (Trái/Giữa/Phải) → `para.TextAlignment`.
  - Lề trong (padding) slider → `chatHistoryText.Padding`.
  - Khoảng cách câu slider → margin dưới mỗi đoạn.
  - **Chỉ hiện câu mới nhất** (checkbox) → chỉ render entry mới nhất.
  - Config: `chatbox_text_alignment/padding/entry_spacing/show_only_latest`. Guard `_isLoadingOptions` khi nạp.
- **Fix auto-clear** (`51f43e8`): `AutoClearTimer_Tick` trước đây **KHÔNG** kiểm tra checkbox → tự xóa dù đã bỏ tick. Nay tôn trọng `IsAutoClearChatboxHistoryEnabled()` (tắt = không bao giờ xóa).
- **Fix "gộp 1 lần dịch = 1 entry"** (`9f9bae3`): `AddTranslatedTextObjectsToChatBox` trước bắn **mỗi block 1 event → nhiều entry** (phụ đề 3 dòng = 3 entry) → "chỉ hiện câu mới nhất" ra mảnh cuối. Nay **gộp tất cả block của 1 lần dịch (trên→dưới) thành 1 entry** (nối bằng space). Overlay game vẫn vẽ từng block riêng; chỉ ChatBox/history/TTS gộp.
- **ChatBox min size** (`52e60c5`): `MinWidth 940→200`, thêm `MinHeight 120` → kéo nhỏ thoải mái (header đã wrap).
- **Settings cao hơn** (`705de74`): `Height 600→880`, `MinHeight 600/MinWidth 900`, bảng phím tắt `MaxHeight 300→460`.

---

## File đã đụng (phần 2)
- `ChatBoxWindow.xaml` / `.xaml.cs` — WrapPanel header, catalog mở rộng, proxy/oneOff handlers, ApplyToolbarLayout cache, start/stop màu, badge góc, loading overlay + spinner.
- `ChatBoxToolbarSettingsWindow.xaml` / `.xaml.cs` — nút ⤒/⤓, drop-highlight.
- `ChatBoxOptionsWindow.xaml` — mục 🧰 lên trên cùng.
- `ConfigManager.cs` — DEFAULT order thêm oneOff.
- `KeyboardShortcuts.cs` — numpad vào `_keyCodeMap`.
- `SettingsWindow.xaml` — combineKey2 thêm phím.
- `MainWindow.xaml.cs` — validate Start, FocusCapturedWindow + SetForegroundWindow, RestoreSessionAreas refresh overlay.
- `TranslationAreaSelectorWindow.xaml.cs` — focus grab cho ESC.
- `ConfigManager.cs` — `disabled_functions` + IsFunctionEnabled/SetFunctionEnabled.
- `KeyboardShortcuts.cs` — gate register + dispatch theo function on/off.
- `SettingsWindow.xaml`/`.cs` — nhóm checkbox bật/tắt chức năng + handler; cửa sổ cao hơn.
- `ChatBoxOptionsWindow.xaml`/`.cs` — nhóm tùy chọn văn bản (căn lề/padding/spacing/chỉ-mới-nhất).
- `ChatBoxWindow.xaml`/`.cs` — apply tùy chọn văn bản trong UpdateChatHistory, min size, fix auto-clear gate.
- `Logic.cs` — `AddTranslatedTextObjectsToChatBox` gộp block thành 1 entry.
- `ConfigManager.cs` — key text options.

## Cần TEST thực tế
- [ ] 🧰: ẩn/hiện nút, kéo–thả (có highlight), ⤒/⤓, Lưu → thanh cập nhật; mở lại app còn giữ.
- [ ] Nút nhiều → thu hẹp ChatBox thấy wrap xuống hàng.
- [ ] Phím tắt: đặt ALT+NUM1 / ALT+UP / ALT+F5 → bấm trong game có ăn.
- [ ] Start khi chưa có app+vùng → hiện notice, không start. ESC hủy chọn vùng.
- [ ] Mở app → Start ngay: viền vùng cũ hiện luôn; cửa sổ game nhảy lên trước.
- [ ] Start/Dừng: nút + badge góc + màn loading spinner hoạt động; dịch xong hiện bản mới.
- [ ] Bật/tắt chức năng: bỏ tick "Đổi ngôn ngữ" → Alt+V hết tác dụng + nút 🔁 trên ChatBox mờ; tick lại → chạy lại.

## Câu hỏi mở
- Nhãn/tooltip nút + text loading hardcode tiếng Việt (chưa qua LocalizationManager) → đổi ngôn ngữ giao diện sẽ không dịch.
- Loading overlay hiện áp cho ChatBox; overlay game (MonitorWindow) chưa có kiểu clear-then-show tương tự (nếu cần thì làm thêm).
