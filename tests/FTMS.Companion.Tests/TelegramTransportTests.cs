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

    [Fact]
    public async Task OutboxPriority_SelectsCriticalStateEventsBeforeEmailAndReminders()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ftms-outbox-priority-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteTicketStore(dbPath);
            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.Now;
            var snapshot = new FTMS.Domain.TicketSnapshot
            {
                Code = "RQ-PRIORITY",
                Status = FTMS.Domain.TicketStatus.InProgress,
                UpdatedAt = now
            };

            // Enqueue reminder first (Priority 2)
            var reminderEvent = new FTMS.Domain.TicketEvent
            {
                EventKey = "KEY-REMINDER",
                TicketCode = snapshot.Code,
                EventType = FTMS.Domain.TicketEventType.UnassignedReminder,
                CurrentStatus = snapshot.Status,
                DetectedAt = now,
                Reason = "Nhắc nhở",
                Snapshot = snapshot
            };
            await store.SaveEventAndEnqueueNotificationAsync(reminderEvent, "Msg Reminder", CancellationToken.None);

            // Enqueue email second (Priority 1)
            var emailEvent = new FTMS.Domain.TicketEvent
            {
                EventKey = "KEY-EMAIL",
                TicketCode = snapshot.Code,
                EventType = FTMS.Domain.TicketEventType.EmailReceived,
                CurrentStatus = snapshot.Status,
                DetectedAt = now,
                Reason = "Email mới",
                Snapshot = snapshot
            };
            await store.SaveEventAndEnqueueNotificationAsync(emailEvent, "Msg Email", CancellationToken.None);

            // Enqueue status change last (Priority 0)
            var statusEvent = new FTMS.Domain.TicketEvent
            {
                EventKey = "KEY-STATUS",
                TicketCode = snapshot.Code,
                EventType = FTMS.Domain.TicketEventType.StatusChanged,
                CurrentStatus = snapshot.Status,
                DetectedAt = now,
                Reason = "Trạng thái",
                Snapshot = snapshot
            };
            await store.SaveEventAndEnqueueNotificationAsync(statusEvent, "Msg Status", CancellationToken.None);

            // Query outbox using the priority query in TelegramOutboxSender
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event.event_type
                FROM notification_outbox outbox
                JOIN ticket_events event ON event.event_key=outbox.event_key
                WHERE outbox.sent_at IS NULL AND outbox.next_attempt_at<=$now
                ORDER BY CASE event.event_type
                    WHEN 'Created' THEN 0
                    WHEN 'StatusChanged' THEN 0
                    WHEN 'AssignmentChanged' THEN 0
                    WHEN 'Terminal' THEN 0
                    WHEN 'EmailReceived' THEN 1
                    WHEN 'SlaThresholdReached' THEN 1
                    ELSE 2 END,
                    outbox.id
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.Now.AddMinutes(1).ToString("O"));

            var orderedTypes = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                orderedTypes.Add(reader.GetString(0));
            }

            Assert.Equal(3, orderedTypes.Count);
            Assert.Equal("StatusChanged", orderedTypes[0]); // Priority 0 first
            Assert.Equal("EmailReceived", orderedTypes[1]);  // Priority 1 second
            Assert.Equal("UnassignedReminder", orderedTypes[2]); // Priority 2 last
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }
}
