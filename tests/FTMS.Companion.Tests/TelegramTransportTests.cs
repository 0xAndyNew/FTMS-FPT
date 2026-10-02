using FTMS.Infrastructure;
using Xunit;

namespace FTMS.Companion.Tests;

public sealed class TelegramTransportTests
{
    private static readonly Uri Destination = new("https://api.telegram.org/bot1/sendMessage");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankProxyUsesDirectConnection(string? value)
    {
        var proxy = new TelegramHttpProxy(() => value);
        Assert.True(proxy.IsBypassed(Destination));
        Assert.Equal(Destination, proxy.GetProxy(Destination));
    }

    [Fact]
    public void ConfiguredProxyIsUsedAndCanChangeAtRuntime()
    {
        string? value = "http://proxy.example:8080";
        var proxy = new TelegramHttpProxy(() => value);
        Assert.False(proxy.IsBypassed(Destination));
        Assert.Equal(new Uri(value), proxy.GetProxy(Destination));

        value = null;
        Assert.True(proxy.IsBypassed(Destination));
        Assert.Equal(Destination, proxy.GetProxy(Destination));
    }

    [Theory]
    [InlineData("https://proxy.example:8080")]
    [InlineData("http://user:pass@proxy.example:8080")]
    [InlineData("http://proxy.example:8080/path")]
    [InlineData("http://proxy.example:8080?x=1")]
    public void InvalidProxyIsRejected(string value)
    {
        Assert.False(TelegramHttpProxy.TryParse(value, out _));
    }

    [Fact]
    public void SanitizerRemovesTokenAndKeepsUsefulContext()
    {
        const string token = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi";
        var message = $"The proxy tunnel request to proxy 'https://api.telegram.org/bot{token}/sendMessage' failed with status code '405'.";
        var sanitized = TelegramErrorSanitizer.Sanitize(message, token);
        Assert.DoesNotContain(token, sanitized);
        Assert.Contains("sendMessage", sanitized);
        Assert.Contains("405", sanitized);
        Assert.Contains("[REDACTED_TELEGRAM_TOKEN]", sanitized);
    }

    [Fact]
    public void SanitizerDoesNotHideNormalColonValues()
    {
        const string message = "Proxy localhost:8080 failed at 12:30 for chat 6800804130 and RQ20260001.";
        Assert.Equal(message, TelegramErrorSanitizer.Sanitize(message));
    }

    [Fact]
    public void CanReceiveTicket_ReturnsFalse_WhenTicketHasAssignee()
    {
        var snapshot = new FTMS.Domain.TicketSnapshot
        {
            Code = "RQ20261002-0001",
            Status = FTMS.Domain.TicketStatus.InProgress,
            AssigneeId = 12345,
            AssigneeName = "Nguyen Van A"
        };
        var evt = new FTMS.Domain.TicketEvent
        {
            EventKey = "key1",
            TicketCode = "RQ20261002-0001",
            EventType = FTMS.Domain.TicketEventType.AssignmentChanged,
            CurrentStatus = FTMS.Domain.TicketStatus.InProgress,
            PreviousStatus = FTMS.Domain.TicketStatus.New,
            DetectedAt = DateTimeOffset.Now,
            Reason = "Assigned",
            PreviousAssigneeName = "Chưa nhận",
            Snapshot = snapshot
        };

        var canReceive = TelegramOutboxSender.CanReceiveTicket(evt, "Mã RQ: RQ20261002-0001\nNgười xử lý: Nguyen Van A");
        Assert.False(canReceive);
    }

    [Fact]
    public void CanReceiveTicket_ReturnsFalse_WhenMessageIndicatesClaimed()
    {
        var canReceive = TelegramOutboxSender.CanReceiveTicket(null,
            "🙋 <b>🟢 TICKET ĐÃ CÓ NGƯỜI NHẬN</b>\nMã RQ: RQ20261002-0001\nNgười xử lý: Nguyen Van A");
        Assert.False(canReceive);
    }

    [Fact]
    public void CanReceiveTicket_ReturnsTrue_WhenUnassignedAndNew()
    {
        var snapshot = new FTMS.Domain.TicketSnapshot
        {
            Code = "RQ20261002-0002",
            Status = FTMS.Domain.TicketStatus.New,
            AssigneeId = null,
            AssigneeName = "Chưa nhận"
        };
        var evt = new FTMS.Domain.TicketEvent
        {
            EventKey = "key2",
            TicketCode = "RQ20261002-0002",
            EventType = FTMS.Domain.TicketEventType.Created,
            CurrentStatus = FTMS.Domain.TicketStatus.New,
            DetectedAt = DateTimeOffset.Now,
            Reason = "Created",
            Snapshot = snapshot
        };

        var canReceive = TelegramOutboxSender.CanReceiveTicket(evt, "📨 <b>🔴 TICKET MỚI</b>\nMã RQ: RQ20261002-0002\nNgười xử lý: Chưa nhận");
        Assert.True(canReceive);
    }

    [Fact]
    public void NotificationFormatter_IncludesCreatedTimeAndCorrectLabel()
    {
        var createdAt = new DateTimeOffset(2026, 10, 2, 14, 30, 0, TimeSpan.FromHours(7));
        var detectedAt = createdAt.AddMinutes(25);
        var snapshot = new FTMS.Domain.TicketSnapshot
        {
            Code = "RQ20261002-0003",
            Status = FTMS.Domain.TicketStatus.InProgress,
            CreatedAt = createdAt,
            AssigneeName = "Nguyen Van B"
        };
        var evt = new FTMS.Domain.TicketEvent
        {
            EventKey = "key3",
            TicketCode = "RQ20261002-0003",
            EventType = FTMS.Domain.TicketEventType.StatusChanged,
            CurrentStatus = FTMS.Domain.TicketStatus.InProgress,
            PreviousStatus = FTMS.Domain.TicketStatus.New,
            DetectedAt = detectedAt,
            Reason = "StatusChanged",
            Snapshot = snapshot
        };

        var formatted = FTMS.Application.NotificationFormatter.Format(evt, "https://ftms.fpt.net");

        Assert.Contains("⏰ <b>Thời gian tạo:</b> 02/10/2026 14:30 (UTC+07:00)", formatted);
        Assert.Contains("🕰 <b>Thời gian từ lúc tạo ticket:</b> 25 phút", formatted);
        Assert.DoesNotContain("tồn tại từ lúc nhận ticket", formatted);
    }
}
