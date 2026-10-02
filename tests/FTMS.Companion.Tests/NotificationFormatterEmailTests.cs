using FTMS.Application;
using FTMS.Domain;
using Xunit;

namespace FTMS.Companion.Tests;

public sealed class NotificationFormatterEmailTests
{
    [Fact]
    public void CleanEmail_SeparatesParagraphsAndDivsWithoutCollapsingText()
    {
        var html = "<p>Dear KT</p><p>Nhờ KT hỗ trợ đăng ký bổ sung gấp thêm 03 khách tham gia tham quan</p>";
        var cleaned = NotificationFormatter.CleanEmail(html);

        Assert.Contains("Dear KT", cleaned);
        Assert.Contains("Nhờ KT hỗ trợ", cleaned);
        Assert.DoesNotContain("Dear KTNhờ KT", cleaned);
    }

    [Fact]
    public void CleanEmail_HandlesTableCellsCleanly()
    {
        var html = "<table><tr><td>Họ tên:</td><td>Nguyễn Văn A</td></tr><tr><td>Số CMND:</td><td>123456789</td></tr></table>";
        var cleaned = NotificationFormatter.CleanEmail(html);

        Assert.Contains("Họ tên:", cleaned);
        Assert.Contains("Nguyễn Văn A", cleaned);
        Assert.DoesNotContain("Họ tên:Nguyễn", cleaned);
    }

    [Fact]
    public void CleanEmail_DoesNotCutOnTranTrongInsideBody()
    {
        var html = "<p>Kính gửi quý khách,</p><p>Công ty chúng tôi trân trọng thông báo về lịch bảo trì định kỳ hệ thống máy chủ vào ngày 10/10/2026. Chi tiết máy chủ: Server A, Server B.</p>";
        var cleaned = NotificationFormatter.CleanEmail(html);

        Assert.Contains("trân trọng thông báo", cleaned);
        Assert.Contains("Server A, Server B", cleaned);
    }

    [Fact]
    public void CleanEmail_DoesNotCutOnFormFieldsWithFromOrTu()
    {
        var html = "<p>Nhờ IT hỗ trợ cấu hình:</p><p>Từ: Chi nhánh Quận 9</p><p>Đến: Chi nhánh Tân Thuận</p><p>Ghi chú: Cần hoàn thành trước 17h.</p>";
        var cleaned = NotificationFormatter.CleanEmail(html);

        Assert.Contains("Từ: Chi nhánh Quận 9", cleaned);
        Assert.Contains("Đến: Chi nhánh Tân Thuận", cleaned);
        Assert.Contains("Cần hoàn thành trước 17h", cleaned);
    }

    [Fact]
    public void CleanEmail_StripsClosingLineAtEndCleanly()
    {
        var html = "<p>Kính gửi anh Tuấn,</p><p>Nhờ anh kiểm tra giúp em ticket này nhé.</p><p>Trân trọng,</p><p>Nguyễn Văn A - Phòng Kỹ thuật</p>";
        var cleaned = NotificationFormatter.CleanEmail(html);

        Assert.Contains("Nhờ anh kiểm tra giúp em ticket này nhé", cleaned);
        Assert.DoesNotContain("Nguyễn Văn A - Phòng Kỹ thuật", cleaned);
    }

    [Fact]
    public async Task ChangeDetector_DetectsUnicodeEllipsisPreview()
    {
        var detector = new TicketChangeDetector();
        var code = "RQ-TEST-3";
        var unicodeEllipsisPreview = "Dear anh, nhờ anh kiểm tra giúp em hệ thống…";
        var fullEmailBody = "<p>Dear anh, nhờ anh kiểm tra giúp em hệ thống mạng đang bị chập chờn từ sáng nay.</p>";

        var calls = 0;
        var client = new TestFtmsClient(onGetLatestEmail: () =>
        {
            calls++;
            return calls == 1
                ? new LatestEmail("101", DateTimeOffset.UtcNow, "sender@fpt.com", "Subject", unicodeEllipsisPreview)
                : new LatestEmail("101", DateTimeOffset.UtcNow, "sender@fpt.com", "Subject", fullEmailBody);
        });

        var snapshot = new TicketSnapshot
        {
            Code = code,
            Status = TicketStatus.New,
            LatestEmail = new LatestEmail("101", DateTimeOffset.UtcNow, "sender@fpt.com", "Subject", unicodeEllipsisPreview)
        };

        var events = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot>(),
            [snapshot],
            client,
            new AppSettings(),
            CancellationToken.None);

        Assert.Equal(2, calls); // Must have retried because unicode ellipsis was detected
        var createdEvent = Assert.Single(events);
        Assert.NotNull(createdEvent.LatestEmail);
        Assert.Contains("chập chờn từ sáng nay", createdEvent.LatestEmail.Body);
    }

    [Fact]
    public async Task ChangeDetector_RetriesWhenEmailIsTruncatedPreview()
    {
        var detector = new TicketChangeDetector();
        var code = "RQ-TEST-1";
        var truncatedPreview = "Dear KTNhờ KT hỗ trợ đăng ký bổ sung gấp thêm 03 khách tham gia tham quan cùng ĐOÀN CÔNG TÁC ngày 1/...";
        var fullEmailBody = "<p>Dear KT</p><p>Nhờ KT hỗ trợ đăng ký bổ sung gấp thêm 03 khách tham gia tham quan cùng ĐOÀN CÔNG TÁC ngày 1/10/2026. Danh sách gồm: 1. Nguyễn Văn A, 2. Trần Văn B, 3. Lê Văn C.</p>";

        var calls = 0;
        var client = new TestFtmsClient(onGetLatestEmail: () =>
        {
            calls++;
            if (calls == 1)
            {
                return new LatestEmail("100", DateTimeOffset.UtcNow, "sender@fpt.com", "Subject", truncatedPreview);
            }
            return new LatestEmail("100", DateTimeOffset.UtcNow, "sender@fpt.com", "Subject", fullEmailBody);
        });

        var snapshot = new TicketSnapshot
        {
            Code = code,
            Status = TicketStatus.New,
            LatestEmail = new LatestEmail("100", DateTimeOffset.UtcNow, "sender@fpt.com", "Subject", truncatedPreview)
        };

        var events = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot>(),
            [snapshot],
            client,
            new AppSettings(),
            CancellationToken.None);

        Assert.Equal(2, calls); // Must have retried because call 1 was truncated preview
        var createdEvent = Assert.Single(events);
        Assert.NotNull(createdEvent.LatestEmail);
        Assert.Contains("Danh sách gồm", createdEvent.LatestEmail.Body);
    }

    [Fact]
    public async Task ChangeDetector_PreservesExistingFullEmail_WhenNewPollHasTruncatedPreview()
    {
        var detector = new TicketChangeDetector();
        var code = "RQ-TEST-2";
        var fullEmail = new LatestEmail("100", DateTimeOffset.UtcNow, "sender@fpt.com", "Subject", "Full email body with all details");
        var truncatedEmail = new LatestEmail("100", DateTimeOffset.UtcNow, "sender@fpt.com", "Subject", "Dear KT preview... ");

        var oldSnapshot = new TicketSnapshot
        {
            Code = code,
            Status = TicketStatus.New,
            AssigneeId = 0,
            AssigneeName = "Chưa nhận",
            LatestEmail = fullEmail
        };

        var newSnapshot = oldSnapshot with
        {
            Status = TicketStatus.InProgress,
            AssigneeId = 10,
            AssigneeName = "DuyPK21",
            LatestEmail = truncatedEmail
        };

        // Client returns null or truncated for this poll
        var client = new TestFtmsClient(onGetLatestEmail: () => truncatedEmail);

        var events = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot> { [code] = oldSnapshot },
            [newSnapshot],
            client,
            new AppSettings(),
            CancellationToken.None);

        var statusEvent = events.FirstOrDefault(e => e.EventType == TicketEventType.StatusChanged);
        Assert.NotNull(statusEvent);
        Assert.NotNull(statusEvent.LatestEmail);
        Assert.Equal("Full email body with all details", statusEvent.LatestEmail.Body);
    }

    private sealed class TestFtmsClient(Func<LatestEmail?>? onGetLatestEmail = null) : IFtmsClient
    {
        public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CurrentUserIdentity?> GetCurrentUserAsync(CancellationToken cancellationToken) => Task.FromResult<CurrentUserIdentity?>(null);
        public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TicketSnapshot>>([]);
        public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(bool includeHistory, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TicketSnapshot>>([]);
        public Task<IReadOnlyList<TicketSnapshot>> GetClosedTicketsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TicketSnapshot>>([]);
        public Task<TicketClaimResult> ClaimTicketAsync(string ticketCode, long expectedUserId, CancellationToken cancellationToken) =>
            Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok"));
        public Task<LatestEmail?> GetLatestEmailAsync(string ticketCode, CancellationToken cancellationToken) =>
            Task.FromResult(onGetLatestEmail?.Invoke());
        public Task<StatusHistoryEntry?> GetLatestStatusHistoryAsync(string ticketCode, TicketStatus status, CancellationToken cancellationToken) =>
            Task.FromResult<StatusHistoryEntry?>(null);
        public Task BeginLoginRecoveryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
