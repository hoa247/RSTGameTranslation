# RST — Notes phiên cải tiến (2026-09-26)

Nhánh: `fix/region-crop-wgc-scaling` · Base: `7c42741`

Tổng hợp toàn bộ bug đã fix + tính năng đã thêm. Tất cả **compile 0 lỗi** (verify bằng `dotnet build`), build/chạy bằng `build-and-run.bat`.

---

## 1. Bug đã fix

### 1.1 Crop vùng sai (bug gốc "lấy text vùng khác")
- **Nguyên nhân:** WGC bitmap ≠ kích thước cửa sổ (DWM rect). Log thực tế: `window=2560x1400` nhưng `bmp=2562x1416` → scale 1.011, lệch ~16px. Code cũ crop bằng toạ độ thô → đọc nhầm pixel.
- **Fix:** map vùng nguồn theo tỉ lệ `bitmap/window`, giữ bitmap ra ở không gian screen-px. `MainWindow.CaptureWindow`.

### 1.2 OneOCR không nạp được DLL → không dịch
- **Nguyên nhân:** `oneocr.dll` + `oneocr.onemodel` nằm ở `app/OneOcr/` nhưng loader tìm ở `app/` → `0x8007007E`. OCR không ra chữ → không có gì để dịch.
- **Fix:** `NativeMethods` static ctor đăng ký `DllImportResolver` tìm dll ở cả root lẫn `OneOcr/`; model path fallback tương tự. `OneOCRManager.cs`.

### 1.3 App chụp toàn màn game khi không nên
- **Nguyên nhân:** `clearSelectedArea` (Alt+H) xoá vùng cuối không reset state; tick chỉ gate theo `hasSelectedTranslationArea`.
- **Fix 2 lớp:** (a) gate tick chặt `if (!isStarted || !hasSelectedTranslationArea || currentAreaIndex không hợp lệ) return;` (b) `clearSelectedArea` reset `hasSelectedTranslationArea=false`, `currentAreaIndex=-1` khi hết vùng. Bao các case: chưa chọn / clear / app restart / game restart.

### 1.4 "Text lạ, TTS nói tùm lum" — KHÔNG phải OCR
- **Nguyên nhân:** **STT (Dịch âm thanh / Whisper)** nghe **nhạc nền** → hallucinate câu YouTube-outro ("This is the end of the video", "See you next time. Bye!", tên nhân vật loạn). Text đó không liên quan ảnh OCR.
- **Xử lý:** tắt "Dịch âm thanh" cho game không có lời thoại thật. + mở rộng bộ lọc hallucination (`localWhisperService.NoisePattern`) chặn thêm các câu tiếng Anh phổ biến.

### 1.5 TTS bật mà không đọc
- **Nguyên nhân:** auto-TTS gate `IsTtsEnabled() && GetIsStarted()` — cần bấm **Start (Alt+G)** để có bản dịch OCR. Không phải bug.
- **Xử lý:** tách nút TTS/STT + tooltip (mục 2.9).

### 1.6 Popup lỗi Gemini chặn app
- **Fix:** 3 chỗ `MessageBox.Show` lỗi Gemini → `ShowFastNotification` **non-blocking**. `GeminiTranslationService.cs`.

### 1.7 Logger file bị ghi đè
- **Nguyên nhân:** `InitializeConsole()` gọi `Console.SetOut` đè logger.
- **Fix:** `DebugFileLogger.WrapWithFile` tee vào file session log.

---

## 2. Tính năng đã thêm

### 2.1 Debug Window (nút 🐛 Debug trên toolbar) — `DebugWindow.xaml`
- Tab **Tùy chọn:** bật/tắt "giữ tất cả ảnh chụp" vs ghi đè 1 ảnh; mở nhanh thư mục ảnh/log.
- Tab **Request / Response / Chi phí:** log mọi call Gemini — request gửi đi, response, token in/out, chi phí ước tính; tách `##|||##` thành danh sách câu của 1 request.
- Tab **Lịch sử dịch** + Tab **Thống kê** (mục 2.5).

### 2.2 Cửa sổ Lịch sử (nút "Lịch sử" trên ChatBox) — `HistoryWindow.xaml`
- Layout học tiếng Anh: câu gốc (đậm) trên, bản dịch (mờ) dưới. Ô tìm kiếm + tự cập nhật khi chơi.

### 2.3 File logging — `DebugFileLogger.cs`
- Tee toàn bộ Console ra `app/logs/session_*.log` (sống được cả khi app chạy elevated).

### 2.4 Cache dịch + SQLite (theo profile game) — `TranslationCache.cs`, `TranslationDatabase.cs`
- Gặp lại text cũ (cùng game) → dùng lại kết quả, **không gọi Gemini** (tiết kiệm token). Log dòng `CACHE HIT`.
- Lưu `app/rst_data.db`: bảng `translations` (cache) + `request_log` (thống kê).
- Khóa cache theo **profile = game_info**.

### 2.5 Thống kê chi phí/token (tab Thống kê trong Debug)
- Hôm nay / game hiện tại / tổng cộng: số request, cache hit, token in-out, chi phí ước tính (giá Flash-Lite).

### 2.6 Cancel request đang chạy (nút ✕ ChatBox)
- `CancellationTokenSource` truyền vào `PostAsync`; `GeminiTranslationService.CancelCurrent()`. Hủy không hiện lỗi.

### 2.7 Nhớ window + vùng, 1 bấm Start là chạy lại — `WindowFinder.cs`
- Lưu process/title window + vùng vào config. Khi bấm Start mà chưa có window → `TryRestoreSession()` tìm lại window theo **process** + khôi phục vùng. Fail-safe.

### 2.8 Dịch 1 lần vùng tùy chọn (nút ＋vùng ChatBox)
- Chọn vùng lạ → chụp+dịch 1 lần → tự quay lại vùng mặc định. `HandleOneOffArea`.

### 2.9 Tách TTS ↔ STT (nút riêng + tooltip)
- Màn chính nhóm Điều khiển: **▶ Bắt đầu** | **🎤 Dịch âm thanh (STT)** | **🔊 Đọc bản dịch (TTS)**.
- ChatBox: nút 🔊/🔇 bật/tắt TTS nhanh, đồng bộ 2 nơi.

### 2.10 Phím tắt panel click được + sáng/mờ theo trạng thái
- Mỗi row "Phím tắt toàn cục" click chạy hành động (dù hotkey tắt); toggle đang bật thì sáng. `KeyboardShortcuts.InvokeFunctionFromClick`.

### 2.11 Chỉ chụp khi có vùng + dừng dịch thì dừng chụp
- Gate tick theo `isStarted` + vùng hợp lệ (xem 1.3).

### 2.12 Nút Test API key (Gemini) + default model `gemini-3.5-flash-lite`
- `GeminiTranslationService.ValidateKeyAsync`; nút Test trong Settings.

### 2.13 Thanh trạng thái pipeline trên overlay
- 📸 Đang chụp → 🔤 OCR → ⏳ Waiting for {service}.

### 2.14 `build-and-run.bat`
- 1 click: tự nâng quyền admin → đóng app → build → mở lại.

---

## 3. Danh sách commit (7c42741..HEAD)

```
3c9a545 feat: separate TTS from STT with dedicated toggles
37356b2 fix: filter more Whisper hallucinations on music/silence
fff90e2 feat: one-off area translate then revert to default area
58f5711 feat: remember last game window + areas; one-press Start resumes session
d8d402b feat: cancel in-flight Gemini request
48ed886 feat: per-profile translation cache + SQLite store + usage stats
77e96e7 feat: capture/OCR fixes, debug tools, history, chatbox controls
```

---

## 4. Cách tiết kiệm token (không cần code)
1. Giảm/tắt "context pieces" (Settings → Ngữ cảnh) — đòn bẩy lớn nhất.
2. Ignore Phrases (Exact) cho UI lặp (vd "A Speak").
3. Tăng "Ngưỡng tương đồng văn bản".
4. Khoanh vùng nhỏ; Manga Mode = Tắt (gộp 1 request); model Flash-Lite.
5. Cache (2.4) tự giúp khi cảnh lặp lại.

---

## 5. Cần TEST thực tế (compile OK nhưng chưa chạy thử)
- [ ] Auto-resume trên Start (tìm lại window theo process).
- [ ] Dịch 1 vùng (＋vùng) rồi về vùng cũ.
- [ ] Cache: quay lại câu cũ thấy "CACHE HIT", token không tăng.
- [ ] Tách TTS/STT: Start + 🔊 TTS bật + 🎤 STT tắt → đọc phụ đề, không đọc bậy.
- [ ] Popup lỗi Gemini không còn chặn app.

## 6. Câu hỏi mở / việc còn cân nhắc
- Nhãn nút "Đọc bản dịch"/tooltip đang hardcode tiếng Việt (chưa qua LocalizationManager) — nếu đổi ngôn ngữ giao diện sẽ không dịch. Thêm localization key nếu cần.
- Toolbar ChatBox đang nhiều nút — có thể gom vào menu nếu thấy chật.
- Chi phí ở tab Thống kê là **ước tính** theo giá Flash-Lite; free tier thực tế = $0.
