# CLAUDE.md

Tệp này cung cấp hướng dẫn cho Claude Code (claude.ai/code) khi làm việc với mã nguồn trong kho lưu trữ này.

---

## 1. Nguyên Tắc Hành Vi Khi Lập Trình (Behavioral Guidelines)

Hướng dẫn nhằm giảm thiểu các sai sót thường gặp của mô hình AI khi viết mã. Kết hợp với các chỉ dẫn đặc thù của dự án bên dưới.

**Đánh đổi (Tradeoff):** Các nguyên tắc này ưu tiên **sự cẩn trọng hơn tốc độ**. Đối với các tác vụ đơn giản, hãy vận dụng sự phán đoán linh hoạt.

### 1.1. Suy nghĩ kỹ trước khi viết mã (Think Before Coding)
**Đừng giả định. Đừng che giấu sự mơ hồ. Làm rõ các đánh đổi.**
- Nêu rõ các giả định của bạn trước khi làm. Nếu chưa chắc chắn, hãy hỏi người dùng.
- Nếu có nhiều cách hiểu hoặc hướng giải quyết, hãy trình bày rõ ràng — không âm thầm chọn một cách.
- Nếu có giải pháp đơn giản hơn, hãy nêu ra và phản biện khi cần thiết.
- Nếu có điều gì chưa rõ, hãy dừng lại, chỉ rõ điểm vướng mắc và hỏi ngay.

### 1.2. Đơn giản là trên hết (Simplicity First)
**Lượng mã tối thiểu để giải quyết vấn đề. Không suy diễn hoặc dự đoán tương lai.**
- Không viết thêm tính năng ngoài những gì được yêu cầu.
- Không tạo các cấu trúc trừu tượng (abstraction) cho đoạn mã chỉ dùng một lần.
- Không tự ý thêm "tính linh hoạt" hoặc "tùy biến cấu hình" nếu không được yêu cầu.
- Không viết code bắt lỗi/xử lý cho các tình huống bất khả thi.
- Nếu bạn viết 200 dòng nhưng có thể rút gọn còn 50 dòng mà vẫn giải quyết trọn vẹn vấn đề, hãy viết lại.
- Tự hỏi: *"Một kỹ sư cấp cao có thấy mã này bị phức tạp hóa quá mức không?"* Nếu có, hãy đơn giản hóa ngay.

### 1.3. Thay đổi có chủ đích và chính xác (Surgical Changes)
**Chỉ chạm vào những gì bắt buộc. Chỉ dọn dẹp những gì do chính bạn tạo ra.**
- Khi chỉnh sửa mã hiện có:
  - Không tự ý "cải tiến" mã lân cận, comment hoặc cách định dạng xung quanh.
  - Không tái cấu trúc (refactor) những phần đang hoạt động tốt và không bị lỗi.
  - Tuân thủ phong cách code sẵn có trong file, kể cả khi bạn quen viết kiểu khác.
  - Nếu phát hiện code chết hoặc thừa không liên quan đến tác vụ, hãy đề cập cho người dùng — không tự ý xóa.
- Khi thay đổi của bạn tạo ra mã dư thừa/mồ côi:
  - Xóa bỏ các lệnh import, biến, hàm mà chính thay đổi của BẠN làm cho nó không còn được sử dụng.
  - Không tự tiện xóa mã chết đã tồn tại từ trước trừ khi có yêu cầu cụ thể.
- **Tiêu chuẩn kiểm chứng:** Mỗi dòng mã thay đổi trong git diff phải giải thích được lý do trực tiếp từ yêu cầu của người dùng.

### 1.4. Thực thi theo mục tiêu rõ ràng (Goal-Driven Execution)
**Xác định tiêu chí thành công. Lặp lại và kiểm tra cho đến khi xác minh xong.**
- Chuyển đổi yêu cầu thành các mục tiêu có thể kiểm chứng được:
  - *"Thêm kiểm tra tính hợp lệ"* → *"Viết test cho dữ liệu đầu vào không hợp lệ, sau đó triển khai code để test pass"*.
  - *"Sửa lỗi (Fix bug)"* → *"Viết test tái hiện lại lỗi đó, sau đó sửa code để test pass"*.
  - *"Tái cấu trúc X"* → *"Đảm bảo toàn bộ test pass trước và sau khi sửa đổi"*.
- Đối với tác vụ nhiều bước, hãy nêu ngắn gọn kế hoạch:
  ```
  1. [Bước làm] → xác minh: [cách kiểm tra]
  2. [Bước làm] → xác minh: [cách kiểm tra]
  3. [Bước làm] → xác minh: [cách kiểm tra]
  ```
- Tiêu chí thành công vững chắc giúp bạn tự lập kiểm tra và hoàn thành công việc; tiêu chí mơ hồ (*"làm cho nó chạy"*) sẽ đòi hỏi phải hỏi lại liên tục.

> **Dấu hiệu nhận biết các nguyên tắc đang phát huy hiệu quả:** Ít thay đổi không cần thiết trong diff, ít phải viết lại vì code quá phức tạp, và các câu hỏi làm rõ xuất hiện trước khi bắt tay viết code thay vì xuất hiện sau khi đã làm sai.

---

## 2. Các Lệnh Thông Dụng Trong Dự Án (Commonly Used Commands)

### 2.1. Biên dịch & Phục hồi gói (Build & Restore)
- **Biên dịch toàn bộ giải pháp (Debug):**
  ```bash
  dotnet build FTMS.Companion.sln
  ```
- **Biên dịch toàn bộ giải pháp (Release):**
  ```bash
  dotnet build FTMS.Companion.sln -c Release
  ```
- **Biên dịch riêng dự án Desktop (WPF):**
  ```bash
  dotnet build src/FTMS.Desktop/FTMS.Desktop.csproj
  ```
- **Khôi phục thư viện phụ thuộc (Dependencies):**
  ```bash
  dotnet restore
  ```

### 2.2. Kiểm thử tự động (Testing với xUnit)
- **Chạy toàn bộ bài test:**
  ```bash
  dotnet test
  ```
- **Chạy riêng dự án test:**
  ```bash
  dotnet test tests/FTMS.Companion.Tests/FTMS.Companion.Tests.csproj
  ```
- **Chạy một lớp test cụ thể:**
  ```bash
  dotnet test --filter "FullyQualifiedName~TicketMonitorTests"
  ```
- **Chạy một phương thức test đơn lẻ:**
  ```bash
  dotnet test --filter "FullyQualifiedName~CountsTicketsClosedTodayByLoggedInCloser"
  ```
- **Chạy nhóm test định dạng & làm sạch email:**
  ```bash
  dotnet test --filter "FullyQualifiedName~NotificationFormatterEmailTests"
  ```

### 2.3. Đóng gói & Tạo bộ cài đặt (Packaging & Installer)
- **Xuất bản ứng dụng đơn tệp tự chứa (Self-contained single-file publish):**
  ```powershell
  dotnet publish src\FTMS.Desktop\FTMS.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o installer\publish
  ```
- **Tạo bộ cài đặt Windows Setup.exe tự động qua Inno Setup 6:**
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
  ```
  *Đầu ra bộ cài sẽ nằm tại thư mục `dist\FTMS-Companion-Setup-<version>.exe`. Yêu cầu máy cài sẵn Inno Setup 6 (`ISCC.exe`).*

---

## 3. Kiến Trúc Hệ Thống & Cấu Trúc Mã Nguồn (Architecture & Structure)

**FTMS Companion** là ứng dụng desktop Windows (.NET 8, WPF kết hợp WinForms interop) phục vụ kỹ thuật viên phòng TOC Data Center (FPT Telecom International). Ứng dụng giám sát ticket FTMS theo thời gian thực, hiển thị bảng điều khiển cá nhân hóa HUD và hỗ trợ tương tác 2 chiều với Telegram (nhận ticket từ xa qua nút bấm inline).

Dự án áp dụng mô hình **Clean Architecture**:

```
FTMS.Desktop (WPF, Dual WebView2, Khay hệ thống, Cài đặt DPAPI)
  └── FTMS.Infrastructure (SQLite WAL, Hàng đợi Telegram Outbox, Long-Polling, Proxy, Khử token)
        └── FTMS.Application (TicketMonitor, TicketChangeDetector, Lọc phòng TOC, Định dạng HTML)
              └── FTMS.Domain (Snapshot, Sự kiện, Email, Enums trạng thái, Quy tắc nghiệp vụ)
```

### Phân công trách nhiệm giữa các tầng:

- **`FTMS.Domain` (`net8.0`)**: Chứa thực thể cốt lõi, Value Object và quy tắc nghiệp vụ. Hoàn toàn không phụ thuộc thư viện ngoài.
  - Các cấu trúc dữ liệu chính: `TicketSnapshot`, `TicketEvent`, `LatestEmail`, `DashboardSummary`.
  - Enum trạng thái: `TicketStatus` (0: New, 1: Assigned, 2: InProgress, 3: Completed, 4: Paused, 5: Closed, 7: Cancelled, 8: Unprocessed).
  - Bộ lọc email: `IsAutomatedAcknowledgement` (thư tự động tiếp nhận), `IsIgnoredSender` (địa chỉ gửi bị bỏ qua), `IsExcluded`.
- **`FTMS.Application` (`net8.0`)**: Tầng nghiệp vụ và điều phối cùng các giao diện hợp đồng (`IFtmsClient`, `ITicketStore`, `INotificationSender`).
  - `TicketMonitor`: Vòng lặp giám sát ngầm, lưu cache ticket đang mở/đã đóng, tính toán khối lượng công việc cá nhân cho dashboard, điều phối dọn dẹp hàng ngày.
  - `TicketChangeDetector`: So khớp khác biệt giữa các snapshot, tính mã băm SHA-256 cho sự kiện, phát hiện vi phạm/sắp hạn SLA (5/3/1 phút), phát hiện email mới và lập lịch nhắc nhở.
  - `TicketNotificationFilter`: Giới hạn thông báo chỉ gửi tới đội ngũ `TOC - Phòng Dịch vụ Data Center` qua chuẩn hóa Unicode không dấu.
  - `NotificationFormatter`: Tạo nội dung HTML gửi Telegram chuẩn blockquote, làm sạch thân thư (bỏ chữ ký, bỏ trích dẫn email cũ, thông báo bảo mật, định dạng lại bảng biểu và đoạn văn).
- **`FTMS.Infrastructure` (`net8.0`)**: Tầng hạ tầng và giao tiếp ngoại vi.
  - `SqliteTicketStore`: Cơ sở dữ liệu SQLite chế độ WAL (`PRAGMA journal_mode=WAL`), quản lý transaction, hàng đợi `notification_outbox` và bảng chống trùng `notification_ledger`.
  - `TelegramOutboxSender`: Gửi tin nhắn từ hàng đợi ra Telegram, tự động chia nhỏ tin nhắn nếu vượt quá 4000 ký tự (bảo toàn thẻ HTML/blockquote) và gắn các nút hành động inline.
  - `TelegramCallbackReceiver`: Lắng nghe Telegram Bot API qua cơ chế Long-polling (`getUpdates`), xử lý sự kiện bấm nút `receive:{code}` để nhận ticket từ xa.
  - `TelegramHttpProxy` & `TelegramErrorSanitizer`: Định tuyến proxy HTTP độc lập cho Telegram và tự động che giấu token Bot (`\d{5,}:[A-Za-z0-9_-]{20,}`) trong log và thông báo lỗi.
  - `DailyLogCleaner`: Tự động cắt tỉa nhật ký, chỉ lưu log của ngày hiện tại theo giờ Việt Nam.
- **`FTMS.Desktop` (`net8.0-windows`)**: Tầng giao diện người dùng.
  - Kiến trúc Dual WebView2: `FtmsWebView` (trình duyệt tương tác cho kỹ thuật viên) và `MonitorWebView` (trình duyệt ngầm 1x1 px phục vụ gọi API). Cả hai dùng chung thư mục phiên `%LOCALAPPDATA%\FTMS.Companion\WebView2`.
  - Các script nhúng vào trang: `FtmsUserActivityScript` (tạm dừng tự động làm mới khi người dùng thao tác chuột/phím), `FtmsStickyPagerScript` (cố định thanh phân trang Kendo UI), `FtmsBotBlockerScript` (chặn chatbot AI gây nặng trang), `FtmsStatusMutationScript` (chặn bắt request POST đổi trạng thái trên web để kích hoạt đồng bộ ngay lập tức).
  - `WebViewLoginRecovery`: Máy trạng thái hữu hạn (`Idle` ➔ `Navigating` ➔ `FindingLoginMethod` ➔ `FollowingSso` ➔ `WaitingForUser`) để tự động khôi phục đăng nhập SSO ADFS khi hết hạn phiên.
  - `SettingsStore`: Lưu trữ cấu hình tại `%LOCALAPPDATA%\FTMS.Companion\settings.json`, mã hóa Bot Token qua Windows DPAPI (`CurrentUser`).
- **`FTMS.Companion.Tests` (`net8.0`)**: Bộ kiểm thử tự động xUnit cho monitor, database SQLite, Telegram callback, proxy và xử lý email.

---

## 4. Các Quy Tắc Nghiệp Vụ & Lưu Ý Vận Hành Cốt Lõi

1. **Múi giờ Việt Nam (UTC+07:00):**
   Mọi phép tính toán liên quan đến thời gian trong nghiệp vụ (đếm số vé đóng hôm nay, dọn dẹp dữ liệu lưu trữ theo ngày, mốc thời gian hiển thị thông báo Telegram) BẮT BUỘC phải chuyển đổi sang múi giờ Việt Nam: `ToOffset(TimeSpan.FromHours(7))`.

2. **Phân lập dữ liệu theo tài khoản người dùng (Multi-User DB Isolation):**
   Khi nhiều nhân viên dùng chung một máy tính, dữ liệu SQLite được tách biệt hoàn toàn theo `UserId` FTMS (`ftms-user-{userId}.db` trong `%LOCALAPPDATA%\FTMS.Companion`). Khi `CompactWindow` phát hiện có sự thay đổi người dùng đăng nhập trên trình duyệt, hệ thống sẽ hủy tiến trình giám sát cũ và kích hoạt phiên lưu trữ + giám sát mới cho tài khoản đó.

3. **Chống gửi trùng lặp & Sổ ghi nhận (Event Deduplication & Ledger):**
   - Mỗi sự kiện được gán mã băm duy nhất: `EventKey = SHA256("{Code}|{EventType}|{PrevStatus}|{CurStatus}|{EmailId}|{Discriminator}")`.
   - Bảng `notification_outbox` áp dụng ràng buộc `UNIQUE(event_key)`.
   - Bảng `notification_ledger` bảo đảm tính bền vững qua các lần khởi động lại ứng dụng:
     - Sự kiện `Created`: Chỉ gửi đúng 1 lần cho mỗi ticket.
     - `UnassignedReminder`: Nhắc nhở tại các mốc 5, 10, 15, 20, 25, 30 phút (`unassigned-1` đến `unassigned-6`), sau đó dừng.
     - `ResponseReminder`: Nhắc nhở tại các mốc 5, 10, 15, 20, 25, 30 phút (`response-1` đến `response-6`), sau đó dừng.

4. **Luồng nhận ticket 2 chiều từ xa (Two-Way Remote Claim):**
   Khi kỹ thuật viên bấm `[🙋 Nhận ticket]` trên Telegram:
   - `TelegramCallbackReceiver` kiểm tra xác thực đúng `ChatId` được cấu hình.
   - Gọi `IFtmsClient.ClaimTicketAsync` (thông qua API `TakeAndAssignmentV12`).
   - Nếu ticket là dạng RQ, hệ thống tự động đổi trạng thái sang **Tạm ngưng / Pending Customer** với lý do ghi chú: **"Hỗ trợ KH"**.
   - Chỉ khi toàn bộ quy trình nhận ticket và đổi trạng thái hoàn tất thành công, nút nhận trên tin nhắn Telegram mới được gỡ bỏ qua `editMessageReplyMarkup`.

5. **Đồng bộ trạng thái tức thì (Instant Status Sync):**
   `FtmsStatusMutationScript` chặn bắt các request fetch/XHR đổi trạng thái ngay bên trong `FtmsWebView` và gửi thông điệp `ftms-status-mutation` về C#. Khi nhận được, `monitor.SyncUntilStatusAsync()` sẽ được gọi ngay lập tức với các bước thử lại (0ms, 500ms, 1500ms) để cập nhật dữ liệu và gửi thông báo tức thì thay vì chờ hết chu kỳ polling 30 giây.
