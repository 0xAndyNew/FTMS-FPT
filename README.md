# FTMS Companion

**FTMS Companion** là ứng dụng desktop Windows hiệu năng cao dành cho kỹ thuật viên và đội ngũ hỗ trợ vận hành (TOC - Phòng Dịch vụ Data Center, FPT Telecom International). Ứng dụng giúp giám sát, theo dõi ticket FTMS theo thời gian thực, hiển thị bảng điều khiển cá nhân hóa, đồng thời tích hợp thông báo và tương tác **hai chiều (Two-Way Interactive)** với Telegram (nhận ticket từ xa ngay trên thông báo Telegram).

---

## Mục lục

1. [Tổng quan dự án](#1-tổng-quan-dự-án)
2. [Các tính năng nổi bật](#2-các-tính-năng-nổi-bật)
3. [Kiến trúc hệ thống](#3-kiến-trúc-hệ-thống)
4. [Công nghệ sử dụng](#4-công-nghệ-sử-dụng)
5. [Cấu trúc thư mục](#5-cấu-trúc-thư-mục)
6. [Quy tắc nghiệp vụ & Xử lý dữ liệu](#6-quy-tắc-nghiệp-vụ--xử-lý-dữ-liệu)
7. [Hướng dẫn cài đặt & Triển khai](#7-hướng-dẫn-cài-đặt--triển-khai)
8. [Hướng dẫn cấu hình](#8-hướng-dẫn-cấu-hình)
9. [Hướng dẫn xây dựng & Đóng gói (Build & Package)](#9-hướng-dẫn-xây-dựng--đóng-gói-build--package)
10. [Kiểm thử tự động (Automated Testing)](#10-kiểm-thử-tự-động-automated-testing)
11. [Bảo mật & Lưu trữ cục bộ](#11-bảo-mật--lưu-trữ-cục-bộ)
12. [Xử lý sự cố (Troubleshooting)](#12-xử-lý-sự-cố-troubleshooting)

---

## 1. Tổng quan dự án

- **Tên ứng dụng:** FTMS Companion
- **Phiên bản:** `1.0.8`
- **Nền tảng:** Windows 10 / 11 (64-bit)
- **Framework:** .NET 8 (WPF + WinForms Interop)
- **Đơn vị phát triển:** FPT / FTI (FPT Telecom International)
- **Mục tiêu:**
  - Loại bỏ hoàn toàn độ trễ trong việc tiếp nhận thông tin yêu cầu hỗ trợ (YCHT / Ticket).
  - Tự động hóa cảnh báo sắp vi phạm SLA, ticket chưa có người nhận hoặc ticket có phản hồi mới từ khách hàng.
  - Cho phép kỹ thuật viên tiếp nhận xử lý ticket trực tiếp từ tin nhắn Telegram mà không cần mở trình duyệt máy tính.
  - Tích hợp trình duyệt FTMS nhúng tối ưu hóa trải nghiệm làm việc cho kỹ thuật viên.

---

## 2. Các tính năng nổi bật

### 2.1. Kiến trúc WebView2 kép (Dual WebView2 Engine)
- **`FtmsWebView` (Interactive Browser):** Trình duyệt chính hiển thị giao diện FTMS iHUB cho người dùng thao tác. Được tích hợp các script chuyên dụng:
  - *User Activity Tracking:* Theo dõi thao tác chuột/phím, tạm dừng việc tự động refresh nếu người dùng đang nhập liệu hoặc thao tác.
  - *Sticky Pager Script:* Cố định thanh phân trang (pagination) của Kendo UI grid luôn nổi trên màn hình, giúp duyệt danh sách thuận tiện ở mọi độ phân giải.
  - *Bot Blocker Script:* Ngăn chặn tự động các chatbot AI (`ftmslite.fpt.vn/agent-ai/js/bot.js`) gây chậm trang web FTMS.
- **`MonitorWebView` (Background Monitor):** WebView2 chạy ngầm (kích thước 1x1 px) độc lập với giao diện chính. Thực hiện polling API `/ihub/request/GetListRequestV12` chu kỳ 2 giây/lần. Hoạt động ngầm liên tục mà không gây giật lag hay ảnh hưởng tới thao tác của người dùng trên giao diện.

### 2.2. Bảng điều khiển thu nhỏ theo thời gian thực (Compact Dashboard HUD)
- **Tổng quan hệ thống:**
  - `TICKET MỚI (Tất cả)`: Hiển thị số lượng ticket mới trên toàn hệ thống đang chờ tiếp nhận.
- **Bảng công việc cá nhân hóa (Personal Workload):**
  - Tự động nhận diện tài khoản FTMS đăng nhập (`Username`, `UserId`).
  - Thống kê chi tiết khối lượng công việc của riêng người dùng:
    - 🔵 **Phân công (Assigned):** Số ticket được phân công cho bạn.
    - 🟢 **Đang thực hiện (InProgress):** Số ticket bạn đang xử lý.
    - 🟠 **Tạm ngưng (Paused):** Số ticket đang tạm dừng.
    - 🟩 **Đã đóng hôm nay (Closed Today):** Số ticket bạn đã hoàn tất xử lý và đóng trong ngày (tính theo giờ Việt Nam UTC+07:00).
- **Giám sát SLA cá nhân (Personal SLA):**
  - 🟡 **Sắp hạn (SLA Risk):** Ticket của bạn sắp đến ngưỡng vi phạm.
  - 🔴 **Quá hạn (SLA Violated):** Ticket của bạn đã vượt quá thời hạn SLA cam kết.

### 2.3. Tự động phục hồi phiên đăng nhập (Auto-Login Recovery State Machine)
- Quản lý trạng thái đăng nhập qua máy trạng thái hữu hạn (`LoginRecoveryState`):
  `Idle` ➔ `Navigating` ➔ `FindingLoginMethod` ➔ `FollowingSso` ➔ `WaitingForUser`.
- Khi phiên hết hạn (chuyển hướng sang `/id/login` hoặc `/adfs/`), ứng dụng tự động tìm kiếm và nhấn nút đăng nhập **FPT Corporation** thông qua Single Sign-On (SSO / ADFS).
- Trường hợp hệ thống yêu cầu xác thực OTP/MFA hoặc đổi mật khẩu, ứng dụng tự động đẩy thông báo Windows Balloon Tip nhắc người dùng thao tác.

### 2.4. Phân lập dữ liệu theo tài khoản (Multi-User Account Isolation)
- Khi nhiều nhân viên dùng chung một máy tính, ứng dụng phân lập hoàn toàn cơ sở dữ liệu SQLite theo `UserId` (`ftms-user-{accountId}.db`).
- Khi phát hiện đổi tài khoản trên trình duyệt FTMS, ứng dụng tự động hủy tiến trình giám sát cũ và kích hoạt phiên giám sát riêng cho tài khoản mới.

### 2.5. Thông báo Telegram phong phú & Tương tác 2 chiều (Two-Way Telegram Bot)
- **Nội dung HTML trực quan:**
  - Định dạng chuẩn nhận diện rõ loại biến động: *Ticket Mới*, *Email Mới*, *Thay Đổi Trạng Thái*, *Chuyển Người Xử Lý*, *Nhắc Ticket Chưa Nhận*, *Cảnh Báo SLA*, *Ticket Đã Đóng/Hủy*.
  - Hiển thị đầy đủ: Mã ticket, người thay đổi, người xử lý, thời gian thay đổi (UTC+07:00), trích dẫn nội dung email (người gửi, thời gian, tiêu đề, thân thư).
- **Nút tương tác (Inline Keyboard):**
  - `[🔎 Mở ticket]`: Mở liên kết trực tiếp đến trang chỉnh sửa ticket (`/ihub/request/edit/...` hoặc `/ihub/case/edit/...`).
  - `[🙋 Nhận ticket]`: Chỉ xuất hiện trên các ticket ở trạng thái *Tạo mới* hoặc *Phân công*.
- **Cơ chế nhận ticket từ xa (Remote Claim Ticket):**
  - Kỹ thuật viên nhấn `[🙋 Nhận ticket]` trên ứng dụng Telegram điện thoại hoặc máy tính.
  - Background receiver (`TelegramCallbackReceiver`) lắng nghe webhook/getUpdates từ Telegram, xác thực chat ID hợp lệ.
  - Gọi ngầm API FTMS `TakeAndAssignmentV12` để nhận ticket về tài khoản FTMS của kỹ thuật viên.
  - Cập nhật lại giao diện tin nhắn Telegram: ẩn nút nhận và gửi thông báo xác nhận thành công (*"Đã nhận RQ... trên FTMS"*).

### 2.6. Hỗ trợ Proxy doanh nghiệp cho Telegram
- Cho phép cấu hình HTTP Proxy riêng cho Telegram (`http://host:port`) mà không làm ảnh hưởng đến đường truyền nội bộ của FTMS trong WebView2.
- Tự động che dấu (redact) Bot Token trong toàn bộ mã nguồn, log và thông báo lỗi qua bộ lọc `TelegramErrorSanitizer`.

### 2.7. Tự động bảo trì & Dọn dẹp dữ liệu (Auto Maintenance & Log Cleaning)
- Tự động dọn dẹp nhật ký lỗi (`DailyLogCleaner`), giữ lại log trong ngày hiện tại.
- Tự động dọn dẹp các ticket đã đóng (`Closed`) từ các ngày trước, chỉ lưu lại ticket đóng trong ngày để phục vụ thống kê dashboard.
- Tự động chạy WAL Checkpoint và `VACUUM` khi dữ liệu phân mảnh vượt quá 20% dung lượng.

### 2.8. Chạy ngầm Khay hệ thống (System Tray)
- Khi bấm nút đóng cửa sổ (X), ứng dụng tự động thu nhỏ xuống System Tray và tiếp tục gửi thông báo Telegram ngầm.
- Menu chuột phải tại khay hệ thống hỗ trợ mở nhanh giao diện hoặc thoát hoàn toàn ứng dụng.
- Tùy chọn tự động khởi động cùng Windows qua Registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

---

## 3. Kiến trúc hệ thống

Dự án được xây dựng theo mô hình **Clean Architecture** (Kiến trúc củ hành) kết hợp nguyên lý tách biệt trách nhiệm:

```
                      ┌────────────────────────────────────────┐
                      │              FTMS.Desktop              │
                      │  (WPF, Dual WebView2, Tray, Settings)  │
                      └───────────────────┬────────────────────┘
                                          │
                      ┌───────────────────▼────────────────────┐
                      │           FTMS.Infrastructure          │
                      │   (Sqlite, Telegram Outbox/Callback,   │
                      │       HTTP Proxy, DailyLogCleaner)     │
                      └───────────────────┬────────────────────┘
                                          │
                      ┌───────────────────▼────────────────────┐
                      │            FTMS.Application            │
                      │  (TicketMonitor, ChangeDetector,       │
                      │   NotificationFilter, Formatter)       │
                      └───────────────────┬────────────────────┘
                                          │
                      ┌───────────────────▼────────────────────┐
                      │              FTMS.Domain               │
                      │ (Models, Enums, Regex, Business Rules) │
                      └────────────────────────────────────────┘
```

### 3.1. Các tầng dự án

| Dự án | Vai trò & Trách nhiệm |
|---|---|
| **`FTMS.Domain`** | Chứa các thực thể dữ liệu (`TicketSnapshot`, `TicketEvent`, `LatestEmail`, `DashboardSummary`), Enums trạng thái (`TicketStatus`), bộ mở rộng và quy tắc lọc email (`IsAutomatedAcknowledgement`, `IsIgnoredSender`). Không phụ thuộc vào bất kỳ thư viện bên ngoài nào. |
| **`FTMS.Application`** | Chứa logic nghiệp vụ cốt lõi: phát hiện thay đổi ticket (`TicketChangeDetector`), bộ lọc phòng ban TOC (`TicketNotificationFilter`), định dạng tin nhắn Telegram (`NotificationFormatter`), bộ điều phối giám sát định kỳ (`TicketMonitor`) và các giao diện hợp đồng (`IFtmsClient`, `ITicketStore`, `INotificationSender`). |
| **`FTMS.Infrastructure`** | Hiện thực các tầng giao tiếp ngoại vi: Lưu trữ SQLite chế độ WAL (`SqliteTicketStore`), gửi tin nhắn Telegram hàng đợi Outbox (`TelegramOutboxSender`), nhận sự kiện callback nút bấm Telegram (`TelegramCallbackReceiver`), cơ chế Proxy (`TelegramHttpProxy`), dọn dẹp log (`DailyLogCleaner`) và khử token trong lỗi (`TelegramErrorSanitizer`). |
| **`FTMS.Desktop`** | Tầng hiển thị WPF (`CompactWindow`, `CompactSettingsWindow`), quản lý 2 phiên WebView2, nhúng các JavaScript script tương tác trang web FTMS, quản lý System Tray Icon, mã hóa bảo vệ cấu hình bằng Windows DPAPI (`SettingsStore`). |
| **`FTMS.Companion.Tests`** | Bộ kiểm thử tự động sử dụng xUnit: kiểm tra logic đếm dashboard, lưu trữ SQLite, dọn dẹp log, proxy Telegram và cơ chế nhận ticket qua callback. |

### 3.2. Sơ đồ luồng dữ liệu (Data Flow)

```
[FTMS Server] <==== HTTP/Cookies ====> [MonitorWebView (WebView2)]
                                                │
                                                ▼ (JSON)
                                       [IFtmsClient (C#)]
                                                │
                                                ▼
                                    [TicketChangeDetector]
                                                │
                          ┌─────────────────────┴─────────────────────┐
                          ▼                                           ▼
                 [SummaryChanged]                              [TicketEvents]
                          │                                           │
                          ▼                                           ▼
                 [Compact Dashboard]                       [TicketNotificationFilter]
                 (Cập nhật HUD UI)                                    │ (TOC Only)
                                                                      ▼
                                                          [NotificationFormatter]
                                                                      │
                                                                      ▼
                                                          [Sqlite: Outbox Table]
                                                                      │
                                                                      ▼
                                                          [TelegramOutboxSender]
                                                                      │
                                                                      ▼ (HTTPS / Proxy)
                                                           [Telegram Bot API]
                                                                      │
                                                                      ▼
                                                            [User Telegram App]
                                                                      │
                                                   (Bấm nút "🙋 Nhận ticket")
                                                                      ▼
                                                          [TelegramCallbackReceiver]
                                                                      │
                                                                      ▼
                                                          [ClaimTicketAsync (FTMS)]
```

---

## 4. Công nghệ sử dụng

| Công nghệ | Phiên bản | Mục đích sử dụng |
|---|---|---|
| **.NET SDK** | `8.0` | Nền tảng phát triển ứng dụng chính |
| **WPF & WinForms** | `net8.0-windows` | Giao diện đồ họa máy tính & Tích hợp khay hệ thống (NotifyIcon) |
| **Microsoft.Web.WebView2** | `1.0.2903.40` | Trình duyệt nhân Chromium nhúng hiển thị FTMS và gọi API ngầm |
| **Microsoft.Data.Sqlite** | `8.0.8` | Cơ sở dữ liệu SQLite cục bộ (hỗ trợ WAL, transaction) |
| **ProtectedData (DPAPI)** | `8.0.0` | Mã hóa an toàn Telegram Bot Token bằng Windows Data Protection API |
| **Inno Setup** | `6.x` | Bộ công cụ đóng gói phần mềm cài đặt chuẩn Windows (Setup.exe) |
| **xUnit** | `2.9.2` | Framework viết kiểm thử tự động (Unit / Integration Tests) |

---

## 5. Cấu trúc thư mục

```
D:\FTMS-FPT
├── FTMS.Companion.sln          # Solution chính của dự án (.NET 8)
├── README.md                   # Tài liệu chi tiết dự án
├── audit1.md                   # Báo cáo đánh giá và cải tiến hệ thống
├── picture/
│   └── ftms-app.ico            # Biểu tượng (Icon) ứng dụng
├── installer/
│   ├── FTMS.Companion.iss      # Kịch bản đóng gói Inno Setup 6
│   ├── build-installer.ps1     # Script tự động publish .NET và build bộ cài Setup
│   └── assets/
│       └── MicrosoftEdgeWebview2Setup.exe  # Bộ cài runtime WebView2 tự động
├── dist/                       # Thư mục chứa file cài đặt đầu ra (.exe)
├── src/
│   ├── FTMS.Domain/            # [Core] Entity, Value Object, Enums & Business Rules
│   │   └── Models.cs           # Định nghĩa cấu trúc dữ liệu toàn hệ thống
│   ├── FTMS.Application/       # [Use Cases] Nghiệp vụ & Điều phối
│   │   ├── Contracts.cs        # Interfaces: IFtmsClient, ITicketStore, INotificationSender
│   │   ├── TicketMonitor.cs    # Vòng lặp giám sát, đồng bộ dữ liệu định kỳ
│   │   ├── TicketChangeDetector.cs   # Thuật toán so khớp & phát hiện sự kiện ticket
│   │   ├── TicketNotificationFilter.cs # Lọc ticket thuộc phòng TOC Data Center
│   │   └── NotificationFormatter.cs  # Tạo nội dung HTML gửi Telegram & làm sạch email
│   ├── FTMS.Infrastructure/    # [Data & External] Kết nối SQLite & Telegram
│   │   ├── SqliteTicketStore.cs      # Thao tác DB SQLite, outbox, transaction, dọn dẹp
│   │   ├── TelegramOutboxSender.cs    # Hàng đợi gửi tin nhắn Telegram & cơ chế nút bấm
│   │   ├── TelegramCallbackReceiver.cs# Lắng nghe và xử lý sự kiện bấm nút nhận ticket
│   │   ├── TelegramHttpProxy.cs       # Quản lý proxy riêng cho kết nối Telegram
│   │   ├── TelegramErrorSanitizer.cs  # Khử token Telegram trong thông báo lỗi
│   │   └── DailyLogCleaner.cs         # Tự động dọn dẹp file log theo ngày
│   └── FTMS.Desktop/           # [UI & Presentation] Ứng dụng Desktop
│       ├── App.xaml / App.xaml.cs     # Điểm khởi chạy ứng dụng & bắt lỗi unhandled
│       ├── CompactWindow.xaml / .cs   # Cửa sổ chính, HUD Dashboard, Dual WebView2
│       ├── CompactSettingsWindow.xaml # Cửa sổ cài đặt Telegram, Proxy, Auto-refresh
│       ├── SettingsStore.cs           # Quản lý cấu hình & mã hóa DPAPI
│       ├── TelegramHttpClientFactory.cs # Factory tạo HttpClient tích hợp proxy
│       ├── WebViewFtmsClient.cs       # Cầu nối C# và JavaScript DOM trong WebView2
│       ├── WebViewLoginRecovery.cs    # State machine tự động phục hồi đăng nhập SSO
│       ├── FtmsUserActivityScript.cs  # Script nhận diện tương tác chuột/phím
│       ├── FtmsStickyPagerScript.cs   # Script cố định thanh phân trang Kendo UI
│       └── FtmsBotBlockerScript.cs    # Script chặn chatbot AI gây chậm trang
├── tests/
│   └── FTMS.Companion.Tests/   # Bộ test tự động (20 tests passed)
│       ├── TicketMonitorTests.cs
│       ├── SqliteTicketStoreTests.cs
│       ├── TelegramCallbackReceiverTests.cs
│       ├── TelegramTransportTests.cs
│       └── DailyLogCleanerTests.cs
└── tools/
    └── generate_icon.py        # Công cụ tạo icon ứng dụng
```

---

## 6. Quy tắc nghiệp vụ & Xử lý dữ liệu

### 6.1. Bảng mã trạng thái FTMS (Status Mapping)

| Mã ID | Tên trạng thái | Nhãn hiển thị | Mô tả nghiệp vụ |
|:---:|---|---|---|
| `0` | New | Tạo mới | Ticket vừa được tạo, chưa có người nhận |
| `1` | Assigned | Phân công | Ticket đã được chuyển tới nhóm/kỹ thuật viên |
| `2` | InProgress | Đang thực hiện | Kỹ thuật viên đang xử lý yêu cầu |
| `3` | Completed | Hoàn thành | Đã hoàn tất xử lý kỹ thuật |
| `4` | Paused | Tạm ngưng | Tạm dừng chờ khách hàng phản hồi / bên thứ 3 |
| `5` | Closed | Đã đóng | Ticket đã đóng (Terminal Status) |
| `7` | Cancelled | Đã hủy | Ticket bị hủy bỏ (Terminal Status) |
| `8` | Unprocessed | Không xử lý | Không thuộc phạm vi xử lý (Terminal Status) |

### 6.2. Quy tắc lọc thông báo phòng ban (Department Filter)
Ứng dụng áp dụng thuật toán chuẩn hóa chuỗi Unicode (loại bỏ dấu tiếng Việt, ký tự đặc biệt, chuyển về chữ thường) để đảm bảo thông báo chỉ gửi tới đúng đội ngũ:
- Phòng ban hợp lệ: **`TOC - Phòng Dịch vụ Data Center`** (`toc phong dich vu data center`).

### 6.3. Quy tắc làm sạch nội dung Email (Email Cleaning & Filtering)
- **Bỏ qua các email tự động:**
  - Danh sách gửi bị bỏ qua: `fti.sd02@fpt.com`, `ihub.akabot2@fpt.com`, `ducvm19@fpt.com`.
  - Bỏ qua các email phản hồi tự động tiếp nhận (*"Thông tin yêu cầu hỗ trợ đã được tiếp nhận..."*, *"Kỹ thuật FPT nhận thông tin YCHT..."*).
- **Trích xuất nội dung thực:**
  - Tách bỏ lịch sử email trích dẫn cũ (cắt tại `<hr>`, `-----Original Message-----`, `From: ... Sent:`).
  - Tách bỏ toàn bộ chữ ký công ty (*"Thanks & Best regards"*, *"Trân trọng"*, *"FPT Telecom International"*, v.v.).
  - Bỏ thông báo bảo mật (*"THÔNG BÁO BẢO MẬT"*, *"CONFIDENTIALITY NOTICE"*).
  - Giới hạn độ dài an toàn tối đa 2500 ký tự để tin nhắn Telegram không bị quá tải.

### 6.4. Cơ chế chống gửi trùng lặp (Event Deduplication)
Mỗi sự kiện gửi Telegram được tạo một `EventKey` duy nhất tính theo mã băm **SHA-256**:
```
SHA-256("{TicketCode}|{EventType}|{PreviousStatus}|{CurrentStatus}|{EmailId}|{Discriminator}")
```
Bảng `notification_outbox` đặt ràng buộc `UNIQUE(event_key)`. Nếu sự kiện đã được lưu hoặc đang gửi, hệ thống tự động bỏ qua, bảo đảm không bao giờ xảy ra tình trạng spam tin nhắn.

### 6.5. Cảnh báo nhắc nhở & SLA (Reminders & SLA Logic)
- **Nhắc ticket chưa nhận (`UnassignedReminder`):** Ticket ở trạng thái *Tạo mới* hoặc *Phân công* chưa có người nhận sẽ được gửi nhắc nhở sau mỗi chu kỳ 5 phút (`unassigned-1`, `unassigned-2`, ...).
- **Nhắc ticket có phản hồi mới (`ResponseReminder`):** Ticket *Đang thực hiện* có email mới từ khách hàng mà chưa được phản hồi tiếp theo sẽ gửi nhắc sau mỗi 5 phút.
- **Cảnh báo SLA (`SlaThresholdReached`):**
  - Cảnh báo sắp vi phạm tại các mốc: **5 phút**, **3 phút**, **1 phút** trước khi chạm hạn.
  - Cảnh báo vi phạm ngay khi `SlaType == 3` (quá hạn).

---

## 7. Hướng dẫn cài đặt & Triển khai

### 7.1. Cài đặt người dùng cuối
1. Tải bộ cài đặt mới nhất: `dist/FTMS-Companion-Setup-1.0.8.exe`.
2. Chạy file cài đặt với quyền Administrator (nếu máy chưa có WebView2 Runtime, bộ cài sẽ tự động tải và cài đặt Microsoft Edge WebView2 ngầm).
3. Làm theo hướng dẫn trên màn hình để hoàn tất. Biểu tượng ứng dụng sẽ xuất hiện trong Start Menu và Desktop (nếu tùy chọn).

### 7.2. Yêu cầu hệ thống
- Hệ điều hành: Windows 10 hoặc Windows 11 (64-bit).
- Trình duyệt: Microsoft Edge WebView2 Runtime (đã tích hợp trong bộ cài).
- Mạng: Kết nối được mạng nội bộ FPT/VPN để truy cập `https://ftms.fpt.net`.

---

## 8. Hướng dẫn cấu hình

Mở ứng dụng, nhấn vào biểu tượng ⚙️ (**Cài đặt**) trên thanh công cụ:

### 8.1. Cấu hình Telegram
1. **Bot Token:** Nhập token nhận từ `@BotFather` (ví dụ: `123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ`).
2. **Chat ID:** Nhập ID của người dùng hoặc Group Telegram (ví dụ: `-1001234567890` hoặc `6800804130`).
3. **HTTP proxy cho Telegram (Tùy chọn):**
   - Nếu máy tính nằm trong mạng công ty yêu cầu proxy để ra ngoài Internet (Telegram), nhập URL proxy theo định dạng: `http://host:port` (ví dụ: `http://10.20.30.40:3128` hoặc lấy từ file cấu hình proxy sẵn có).
   - Nếu mạng ra được Internet trực tiếp, **hãy để trống ô này**.
4. Bấm **Gửi thử** để kiểm tra kết nối. Ứng dụng sẽ gửi một tin nhắn mẫu tới Telegram để xác nhận cấu hình chuẩn xác.
5. Nhấn **Lưu cài đặt**. Token sẽ tự động được mã hóa bảo vệ bằng Windows DPAPI.

### 8.2. Cấu hình Tự động làm mới & Khởi động
- **Tự động làm mới:** Tích chọn để tự động bấm nút làm mới danh sách FTMS. Chu kỳ mặc định: `30` giây (tối thiểu 5 giây, tối đa 3600 giây).
- **Khởi động cùng Windows:** Tích chọn nếu muốn FTMS Companion tự động chạy ngầm mỗi khi bật máy tính.

---

## 9. Hướng dẫn xây dựng & Đóng gói (Build & Package)

### 9.1. Công cụ yêu cầu cho lập trình viên
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6](https://jrsoftware.org/isdl.php) (để đóng gói installer)
- Visual Studio 2022 (với workload *.NET Desktop Development*) hoặc Visual Studio Code / JetBrains Rider

### 9.2. Build giải pháp bằng .NET CLI
```bash
# Di chuyển vào thư mục dự án
cd D:\FTMS-FPT

# Khôi phục dependencies
dotnet restore

# Build toàn bộ solution ở chế độ Release
dotnet build FTMS.Companion.sln -c Release
```

### 9.3. Đóng gói bộ cài đặt Setup.exe tự động
Chạy script PowerShell đi kèm để tự động publish single-file và đóng gói Inno Setup:
```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```
Bộ cài đặt hoàn chỉnh `FTMS-Companion-Setup-1.0.8.exe` sẽ được tạo trong thư mục `D:\FTMS-FPT\dist`.

---

## 10. Kiểm thử tự động (Automated Testing)

Dự án có bộ test suite hoàn chỉnh trong thư mục `tests/FTMS.Companion.Tests`:

```bash
# Chạy toàn bộ các bài test
dotnet test
```

### Kết quả kiểm thử:
```
Test run for D:\FTMS-FPT\tests\FTMS.Companion.Tests\bin\Debug\net8.0\FTMS.Companion.Tests.dll (.NETCoreApp,Version=v8.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed: 0, Passed: 20, Skipped: 0, Total: 20, Duration: 155 ms
```

### Các nhóm kiểm thử chính:
- `TicketMonitorTests`: Kiểm tra việc tính toán thống kê dashboard, đếm ticket đóng hôm nay theo người dùng, đảm bảo cơ chế dọn dẹp chỉ kích hoạt đúng 1 lần/ngày.
- `SqliteTicketStoreTests`: Kiểm tra việc lưu trữ snapshot, cơ chế dọn dẹp ticket đã đóng các ngày trước nhưng bảo tồn ticket đang mở và ticket đóng hôm nay.
- `TelegramCallbackReceiverTests`: Kiểm tra cơ chế xử lý callback nhận ticket, quản lý offset theo từng Bot Token, đảm bảo retry an toàn khi gặp lỗi xác thực hoặc mạng.
- `TelegramTransportTests`: Kiểm tra parser HTTP Proxy, khả năng thay đổi proxy lúc runtime, kiểm tra bộ lọc khử token Telegram trong thông báo lỗi (`Sanitizer`).
- `DailyLogCleanerTests`: Kiểm tra tự động xóa log cũ và cắt tỉa log theo múi giờ Việt Nam UTC+07:00.

---

## 11. Bảo mật & Lưu trữ cục bộ

### 11.1. Vị trí dữ liệu cục bộ
Toàn bộ dữ liệu của người dùng được lưu trữ cục bộ tại thư mục:
```
%LOCALAPPDATA%\FTMS.Companion
```
Thư mục này bao gồm:
- `settings.json`: Cấu hình ứng dụng (Telegram Chat ID, Proxy URL, chu kỳ refresh).
- `ftms-user-{userId}.db`: Cơ sở dữ liệu SQLite lưu trữ ticket và outbox của từng tài khoản.
- `telegram-{botId}.offset`: Lưu offset getUpdates của Telegram bot để tránh đọc lại tin cũ.
- `error.log`: Nhật ký ghi lại các ngoại lệ hệ thống (tự động dọn dẹp hàng ngày).
- `WebView2/`: Thư mục lưu cache và session cookies của trình duyệt Microsoft Edge WebView2.

### 11.2. Cơ chế bảo mật
- **Không lưu mật khẩu:** Ứng dụng **hoàn toàn không lưu trữ** mật khẩu tài khoản FTMS / SSO của người dùng. Việc đăng nhập diễn ra trực tiếp trên trình duyệt nhúng WebView2 qua cơ chế cookie phiên của hệ thống FPT.
- **Mã hóa DPAPI:** Telegram Bot Token được mã hóa bằng thuật toán `ProtectedData.Protect` (chuẩn Windows DPAPI, phạm vi `CurrentUser`). Chỉ tài khoản Windows hiện tại mới có thể giải mã token này.
- **Che giấu Token:** Bộ lọc `TelegramErrorSanitizer` quét và thay thế toàn bộ chuỗi token dạng `\d{5,}:[A-Za-z0-9_-]{20,}` thành `[REDACTED_TELEGRAM_TOKEN]` trước khi ghi ra log hoặc hiển thị lên màn hình.

---

## 12. Xử lý sự cố (Troubleshooting)

### 1. Ứng dụng báo "Phiên đăng nhập FTMS đã hết hạn"
- **Nguyên nhân:** Phiên làm việc SSO của FPT đã hết hạn hoặc cookie trình duyệt cần làm mới.
- **Cách xử lý:** Ứng dụng sẽ tự động chuyển hướng về trang đăng nhập và tự động nhấn nút *FPT Corporation*. Nếu tài khoản có bật xác thực 2 lớp (MFA/OTP), hãy hoàn tất xác thực trên cửa sổ FTMS hiển thị. Khi đăng nhập thành công, hệ thống tự động kết nối lại.

### 2. Không nhận được thông báo Telegram
- **Kiểm tra Bot Token & Chat ID:** Vào **Cài đặt** (⚙️) ➔ Kiểm tra Token và Chat ID ➔ Bấm **Gửi thử**.
- **Kiểm tra Proxy:** Nếu đang dùng mạng nội bộ công ty chặn Telegram trực tiếp, hãy nhập HTTP Proxy hợp lệ vào ô cài đặt.
- **Kiểm tra người nhận/nhóm:** Đảm bảo bạn đã bấm `/start` với bot nếu gửi vào chat cá nhân, hoặc đã thêm bot vào nhóm và cấp quyền gửi tin nhắn nếu gửi vào Group.

### 3. Nhấn "🙋 Nhận ticket" trên Telegram nhưng báo thất bại
- **Nguyên nhân:**
  - Ticket đã được một kỹ thuật viên khác tiếp nhận trước đó.
  - Ticket đã đóng hoặc đã hủy.
  - Phiên FTMS trên máy tính đang bị hết hạn chưa kịp đăng nhập lại.
- **Khắc phục:** Mở cửa sổ FTMS Companion để xác nhận tài khoản đang ở trạng thái *"Đã kết nối"*, sau đó thử lại.

### 4. Kiểm tra file nhật ký lỗi
- Mở thư mục `%LOCALAPPDATA%\FTMS.Companion` và kiểm tra tệp `error.log` để xem thông tin chi tiết về các ngoại lệ nếu có.

---

*Tài liệu được cập nhật tự động và kiểm tra toàn diện ngày 28/09/2026 cho phiên bản FTMS Companion 1.0.8.*
