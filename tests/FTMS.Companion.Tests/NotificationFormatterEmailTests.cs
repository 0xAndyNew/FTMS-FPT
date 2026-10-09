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
    public async Task ChangeDetector_CreatedEvent_RetriesMissingEmailAndUsesFullEmail()
    {
        var sentAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var email = Email("101", sentAt, "Nội dung email đầy đủ sau khi thử lại");
        var calls = 0;
        var client = new TestFtmsClient(() => ++calls == 1 ? null : email);
        var snapshot = Snapshot("RQ-CREATED-RETRY", TicketStatus.New, null, sentAt) with
        {
            AssigneeId = null,
            AssigneeName = "---"
        };

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot>(), [snapshot], client,
            new AppSettings(), CancellationToken.None);

        var createdEvent = Assert.Single(events);
        Assert.Equal(TicketEventType.Created, createdEvent.EventType);
        Assert.Equal(2, calls);
        Assert.Equal(email.Body, createdEvent.LatestEmail?.Body);
        Assert.Contains(email.Body!, NotificationFormatter.Format(createdEvent, "https://ftms.fpt.net/ihub"));
    }

    [Fact]
    public async Task ChangeDetector_CreatedEvent_UsesCompleteListEmailWithoutFetching()
    {
        var sentAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var email = Email("104", sentAt, "Nội dung email đầy đủ từ danh sách");
        var calls = 0;
        var client = new TestFtmsClient(() =>
        {
            calls++;
            return null;
        });
        var snapshot = Snapshot("RQ-CREATED-FAST", TicketStatus.New, email, sentAt) with
        {
            AssigneeId = null,
            AssigneeName = "---"
        };

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot>(), [snapshot], client,
            new AppSettings(), CancellationToken.None);

        var createdEvent = Assert.Single(events);
        Assert.Equal(TicketEventType.Created, createdEvent.EventType);
        Assert.Equal(0, calls);
        Assert.Equal(email.Body, createdEvent.LatestEmail?.Body);
        var message = NotificationFormatter.Format(createdEvent, "https://ftms.fpt.net/ihub");
        Assert.Contains("Nội dung email đầy đủ từ danh s&#225;ch", message);
    }

    [Fact]
    public async Task ChangeDetector_UnassignedReminder_RetriesMissingEmailWithoutDuplicateEmailEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var email = Email("102", now, "Nội dung email cho nhắc ticket chưa nhận");
        var oldSnapshot = Snapshot("RQ-REMINDER-RETRY", TicketStatus.New, null, now.AddMinutes(-6)) with
        {
            CreatedAt = now.AddMinutes(-6),
            AssigneeId = null,
            AssigneeName = "---"
        };
        var currentSnapshot = oldSnapshot with { UpdatedAt = now };
        var calls = 0;
        var client = new TestFtmsClient(() => ++calls == 1 ? null : email);

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        var reminder = Assert.Single(events);
        Assert.Equal(TicketEventType.UnassignedReminder, reminder.EventType);
        Assert.Equal(2, calls);
        Assert.Equal(email.Body, reminder.LatestEmail?.Body);
        Assert.DoesNotContain(events, item => item.EventType == TicketEventType.EmailReceived);
    }

    [Fact]
    public async Task ChangeDetector_CreatedEvent_AllowsEmailFetchLongerThanOldDeadline()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var email = Email("103", sentAt, "Nội dung email tải chậm nhưng thành công");
        var client = new TestFtmsClient(onGetLatestEmailAsync: async cancellationToken =>
        {
            await Task.Delay(900, cancellationToken);
            return email;
        });
        var snapshot = Snapshot("RQ-CREATED-SLOW", TicketStatus.New, null, sentAt);

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot>(), [snapshot], client,
            new AppSettings(), CancellationToken.None);

        var createdEvent = Assert.Single(events);
        Assert.Equal(email.Body, createdEvent.LatestEmail?.Body);
    }

    [Fact]
    public async Task ChangeDetector_CreatedEvent_PropagatesCallerCancellation()
    {
        var client = new TestFtmsClient(onGetLatestEmailAsync: async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        });
        var snapshot = Snapshot("RQ-CREATED-CANCELLED", TicketStatus.New, null,
            DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TicketChangeDetector().DetectAsync(
                new Dictionary<string, TicketSnapshot>(), [snapshot], client,
                new AppSettings(), cancellation.Token));
    }

    [Fact]
    public async Task ChangeDetector_CreatedEvent_DefinitiveNoEmailStillCreatesNotification()
    {
        var calls = 0;
        var client = new TestFtmsClient(() =>
        {
            calls++;
            return null;
        });
        var snapshot = Snapshot("RQ-CREATED-NO-EMAIL", TicketStatus.New, null, DateTimeOffset.UtcNow);

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot>(), [snapshot], client,
            new AppSettings(), CancellationToken.None);

        var createdEvent = Assert.Single(events);
        Assert.Equal(2, calls);
        Assert.Null(createdEvent.LatestEmail);
        var message = NotificationFormatter.Format(createdEvent, "https://ftms.fpt.net/ihub");
        Assert.Contains("TICKET MỚI", message);
        Assert.DoesNotContain("<blockquote>", message);
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

        var calls = 0;
        var client = new TestFtmsClient(onGetLatestEmail: () =>
        {
            calls++;
            return truncatedEmail;
        });

        var events = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot> { [code] = oldSnapshot },
            [newSnapshot],
            client,
            new AppSettings(),
            CancellationToken.None);

        var changeEvent = Assert.Single(events);
        Assert.Equal(TicketEventType.AssignmentChanged, changeEvent.EventType);
        Assert.NotNull(changeEvent.LatestEmail);
        Assert.Equal("Full email body with all details", changeEvent.LatestEmail.Body);
        Assert.NotNull(changeEvent.Snapshot.LatestEmail);
        Assert.Equal("Full email body with all details", changeEvent.Snapshot.LatestEmail.Body);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ChangeDetector_NewerTruncatedPreview_DoesNotUseOlderCompleteEmail()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var oldEmail = Email("100", sentAt.AddSeconds(-30), "Nội dung email cũ đầy đủ");
        var preview = Email("101", sentAt, "Dear KT preview...");
        var fullEmail = Email("101", sentAt, "Nội dung email mới đầy đủ sau khi tải lại");
        var oldSnapshot = Snapshot("RQ-NEWER-PREVIEW", TicketStatus.Assigned, oldEmail,
            sentAt.AddMinutes(-5)) with
        {
            AssigneeId = null,
            AssigneeName = "---"
        };
        var currentSnapshot = oldSnapshot with
        {
            AssigneeId = 42,
            AssigneeName = "DuyPK21",
            LatestEmail = preview,
            UpdatedAt = sentAt
        };
        var calls = 0;
        var client = new TestFtmsClient(() => ++calls == 1 ? preview : fullEmail);

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        var assignmentEvent = Assert.Single(events, e => e.EventType == TicketEventType.AssignmentChanged);
        Assert.Equal(2, calls);
        Assert.Equal("101", assignmentEvent.LatestEmail?.Id);
        Assert.Equal(fullEmail.Body, assignmentEvent.LatestEmail?.Body);
    }

    [Fact]
    public async Task ChangeDetector_AssignmentWithoutEmail_FetchesOnlyForCriticalEvent()
    {
        var calls = 0;
        var oldSnapshot = Snapshot("RQ-ASSIGNMENT", TicketStatus.Assigned,
            Email("100", DateTimeOffset.UtcNow.AddMinutes(-5), "Nội dung cũ"), DateTimeOffset.UtcNow.AddMinutes(-5)) with
        {
            AssigneeId = null,
            AssigneeName = "---",
            LatestEmail = null
        };
        var currentSnapshot = oldSnapshot with
        {
            AssigneeId = 42,
            AssigneeName = "DuyPK21",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var client = new TestFtmsClient(() =>
        {
            calls++;
            return null;
        });

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        var assignmentEvent = Assert.Single(events);
        Assert.Equal(TicketEventType.AssignmentChanged, assignmentEvent.EventType);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ChangeDetector_AssignmentWithTruncatedPreview_RetriesForFullEmail()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var preview = Email("101", sentAt, "Dear KT preview...");
        var fullEmail = Email("101", sentAt, "Nội dung email đầy đủ sau khi tải lại");
        var oldSnapshot = Snapshot("RQ-ASSIGNMENT-EMAIL", TicketStatus.Assigned,
            Email("100", sentAt.AddMinutes(-5), "Nội dung cũ"), sentAt.AddMinutes(-5)) with
        {
            AssigneeId = null,
            AssigneeName = "---",
            LatestEmail = null
        };
        var currentSnapshot = oldSnapshot with
        {
            AssigneeId = 42,
            AssigneeName = "DuyPK21",
            LatestEmail = preview,
            UpdatedAt = sentAt
        };
        var calls = 0;
        var client = new TestFtmsClient(() => ++calls == 1 ? preview : fullEmail);

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        var assignmentEvent = Assert.Single(events, e => e.EventType == TicketEventType.AssignmentChanged);
        Assert.Equal(fullEmail.Body, assignmentEvent.LatestEmail?.Body);
        Assert.DoesNotContain(events, e => e.EventType == TicketEventType.EmailReceived);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ChangeDetector_StatusChange_EmitsImmediateStatusAndSeparateEmailReceived()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var oldEmail = Email("100", sentAt.AddMinutes(-5), "Nội dung cũ");
        var newEmail = Email("101", sentAt, "Phản hồi mới của khách hàng");
        var oldSnapshot = Snapshot("RQ-PARALLEL", TicketStatus.Paused, oldEmail, sentAt.AddMinutes(-5));
        var currentSnapshot = oldSnapshot with
        {
            Status = TicketStatus.InProgress,
            LatestEmail = newEmail,
            UpdatedAt = sentAt
        };
        var client = new TestFtmsClient(onGetLatestEmail: () => newEmail);

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        var statusEvent = Assert.Single(events, e => e.EventType == TicketEventType.StatusChanged);
        Assert.Equal("101", statusEvent.LatestEmail?.Id);
        Assert.Equal(newEmail.Body, statusEvent.LatestEmail?.Body);
        Assert.DoesNotContain(events, e => e.EventType == TicketEventType.EmailReceived);

        var statusMsg = NotificationFormatter.Format(statusEvent, "https://ftms.fpt.net/ihub");
        Assert.Contains("From: sender@fpt.com", statusMsg);
        Assert.Contains("Phản hồi mới của", statusMsg);
        Assert.Contains("<blockquote>", statusMsg);
    }

    [Fact]
    public async Task ChangeDetector_PausedTicketWithNewEmailAndResume_EmitsOneResponseStatusChange()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var oldEmail = Email("100", sentAt.AddMinutes(-5), "Nội dung cũ");
        var newEmail = Email("101", sentAt, "Phản hồi mới của khách hàng");
        var oldSnapshot = Snapshot("RQ-PAUSED-RESUME", TicketStatus.Paused, oldEmail, sentAt.AddMinutes(-5));
        var currentSnapshot = oldSnapshot with
        {
            Status = TicketStatus.InProgress,
            LatestEmail = newEmail,
            UpdatedAt = sentAt
        };
        var detector = new TicketChangeDetector();
        var client = new TestFtmsClient(() => newEmail);

        var events = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        var statusEvent = Assert.Single(events, e => e.EventType == TicketEventType.StatusChanged);
        Assert.Equal(TicketEventType.StatusChanged, statusEvent.EventType);
        Assert.Equal(TicketStatus.Paused, statusEvent.PreviousStatus);
        Assert.Equal(TicketStatus.InProgress, statusEvent.CurrentStatus);
        Assert.Equal("101", statusEvent.LatestEmail?.Id);
        Assert.Equal(newEmail.Body, statusEvent.LatestEmail?.Body);
        Assert.DoesNotContain(events, e => e.EventType == TicketEventType.EmailReceived);

        var statusMessage = NotificationFormatter.Format(statusEvent, "https://ftms.fpt.net/ihub");
        Assert.Contains("Tạm ngưng ➔ Đang thực hiện", statusMessage);
        Assert.Contains("Phản hồi mới của kh&#225;ch h&#224;ng", statusMessage);
    }

    [Fact]
    public async Task ChangeDetector_StatusChange_UsesCompleteListEmailWithoutRefreshingHistory()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var email = new LatestEmail("100", sentAt.AddMinutes(-38), "TrungNT105@fpt.com",
            "Tiêu đề kiểm thử", "Nội dung phản hồi đầy đủ");
        var oldSnapshot = Snapshot("RQ202610070261", TicketStatus.Paused, email, sentAt.AddMinutes(-5));
        var currentSnapshot = oldSnapshot with
        {
            Status = TicketStatus.InProgress,
            UpdatedAt = sentAt
        };
        var calls = 0;
        var client = new TestFtmsClient(() =>
        {
            calls++;
            return null;
        });

        var events = await new TicketChangeDetector().DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        var statusEvent = Assert.Single(events, e => e.EventType == TicketEventType.StatusChanged);
        Assert.Equal("100", statusEvent.LatestEmail?.Id);
        Assert.Equal(email.Body, statusEvent.LatestEmail?.Body);
        Assert.DoesNotContain(events, e => e.EventType == TicketEventType.EmailReceived);
        Assert.Equal(0, calls);

        var message = NotificationFormatter.Format(statusEvent, "https://ftms.fpt.net/ihub");
        Assert.Contains("From: TrungNT105@fpt.com", message);
        Assert.Contains("Nội dung phản hồi đầy đủ", message);
    }

    [Fact]
    public async Task ChangeDetector_PausedTicketWithNewEmail_EmitsResponseWithoutStatusChange()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var oldEmail = Email("100", sentAt.AddMinutes(-5), "Nội dung cũ");
        var newEmail = Email("101", sentAt, "Phản hồi mới của khách hàng");
        var oldSnapshot = Snapshot("RQ-PAUSED", TicketStatus.Paused, oldEmail, sentAt.AddMinutes(-5));
        var currentSnapshot = oldSnapshot with { LatestEmail = newEmail, UpdatedAt = sentAt };
        var detector = new TicketChangeDetector();
        var client = new TestFtmsClient(() => newEmail);

        var events = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        var emailEvent = Assert.Single(events);
        Assert.Equal(TicketEventType.EmailReceived, emailEvent.EventType);
        Assert.Equal(TicketStatus.Paused, emailEvent.CurrentStatus);

        var message = NotificationFormatter.Format(emailEvent, "https://ftms.fpt.net/ihub");
        Assert.Contains("TICKET ĐÃ CÓ PHẢN HỒI MỚI", message);
        Assert.Contains("Trạng thái:</b> Tạm ngưng", message);
    }

    [Fact]
    public async Task ChangeDetector_ClosingTicketWithNewEmail_EmitsOnlyTerminalEvent()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var oldEmail = Email("100", sentAt.AddMinutes(-5), "Nội dung cũ");
        var newEmail = Email("101", sentAt, "Phản hồi đến cùng lúc ticket đóng");
        var oldSnapshot = Snapshot("RQ-CLOSING", TicketStatus.InProgress, oldEmail, sentAt.AddMinutes(-5));
        var currentSnapshot = oldSnapshot with
        {
            Status = TicketStatus.Closed,
            LatestEmail = newEmail,
            UpdatedAt = sentAt,
            IsTerminal = true
        };
        var detector = new TicketChangeDetector();
        var client = new TestFtmsClient(() => newEmail);

        var events = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        var terminalEvent = Assert.Single(events);
        Assert.Equal(TicketEventType.Terminal, terminalEvent.EventType);
        Assert.Equal("101", terminalEvent.LatestEmail?.Id);
        Assert.Equal(newEmail.Body, terminalEvent.LatestEmail?.Body);
        Assert.DoesNotContain(events, item => item.EventType == TicketEventType.EmailReceived);
    }

    [Fact]
    public async Task ChangeDetector_ClosedTicketEmailUpdate_DoesNotEmitEmailEvent()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var oldEmail = Email("100", sentAt.AddMinutes(-5), "Nội dung cũ");
        var newEmail = Email("101", sentAt, "Email lịch sử cập nhật sau khi đóng");
        var oldSnapshot = Snapshot("RQ-CLOSED", TicketStatus.Closed, oldEmail, sentAt.AddMinutes(-5)) with { IsTerminal = true };
        var currentSnapshot = oldSnapshot with { LatestEmail = newEmail, UpdatedAt = sentAt };
        var detector = new TicketChangeDetector();
        var client = new TestFtmsClient(() => newEmail);

        var events = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot> { [oldSnapshot.Code] = oldSnapshot },
            [currentSnapshot], client, new AppSettings(), CancellationToken.None);

        Assert.Empty(events);
    }

    [Fact]
    public void NotificationFormatter_StatusChanged_FormatsActorAndNote_WithoutEmptyBlockquote()
    {
        var snapshot = new TicketSnapshot
        {
            Code = "RQ202610070280",
            Status = TicketStatus.Paused,
            Title = "[Miền Bắc]-[HNID49102]-[Rack]-[LỖI KHÔNG CẬP NHẬT ĐƯỢC THÔNG TIN WEB]",
            AssigneeName = "HieuDX2 - TOC - Phòng Dịch vụ Data Center",
            CreatedAt = new DateTimeOffset(2026, 10, 7, 19, 44, 0, TimeSpan.FromHours(7))
        };
        var evt = new TicketEvent
        {
            EventKey = "KEY-PAUSED",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.StatusChanged,
            PreviousStatus = TicketStatus.InProgress,
            CurrentStatus = TicketStatus.Paused,
            DetectedAt = new DateTimeOffset(2026, 10, 7, 21, 11, 41, TimeSpan.FromHours(7)),
            ChangedAt = new DateTimeOffset(2026, 10, 7, 21, 11, 0, TimeSpan.FromHours(7)),
            ChangedBy = "HieuDX2 - TOC - Phòng Dịch vụ Data Center",
            Reason = "Pending Customer",
            Note = "Pending Customer",
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net");

        Assert.Contains("👤 <b>Người thực hiện:</b> HieuDX2 - TOC - Ph&#242;ng Dịch vụ Data Center", message);
        Assert.Contains("📝 <b>Ghi chú:</b> Pending Customer", message);
        Assert.Contains("Đang thực hiện ➔ Tạm ngưng", message);
        Assert.DoesNotContain("<blockquote>", message);
    }

    [Fact]
    public void NotificationFormatter_StatusChanged_WithEmail_FormatsEmailInBlockquote()
    {
        var email = new LatestEmail("101", new DateTimeOffset(2026, 10, 7, 21, 11, 41, TimeSpan.FromHours(7)),
            "HieuDX2@fpt.com", "Tiêu đề", "<p>Dear anh</p><p>FPT đã hỗ trợ xử lý lỗi anh vui lòng thử lại .</p>");
        var snapshot = new TicketSnapshot
        {
            Code = "RQ202610070280",
            Status = TicketStatus.Paused,
            Title = "[Miền Bắc]-[HNID49102]-[Rack]-[LỖI KHÔNG CẬP NHẬT ĐƯỢC THÔNG TIN WEB]",
            AssigneeName = "HieuDX2",
            LatestEmail = email,
            CreatedAt = new DateTimeOffset(2026, 10, 7, 19, 44, 0, TimeSpan.FromHours(7))
        };
        var evt = new TicketEvent
        {
            EventKey = "KEY-PAUSED-WITH-EMAIL",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.StatusChanged,
            PreviousStatus = TicketStatus.InProgress,
            CurrentStatus = TicketStatus.Paused,
            DetectedAt = new DateTimeOffset(2026, 10, 7, 21, 11, 41, TimeSpan.FromHours(7)),
            ChangedAt = new DateTimeOffset(2026, 10, 7, 21, 11, 0, TimeSpan.FromHours(7)),
            ChangedBy = "HieuDX2",
            Reason = "Pending Customer",
            Note = "Pending Customer",
            LatestEmail = email,
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net");

        Assert.Contains("👤 <b>Người thực hiện:</b> HieuDX2", message);
        Assert.Contains("📝 <b>Ghi chú:</b> Pending Customer", message);
        Assert.Contains("<blockquote>", message);
        Assert.Contains("From: HieuDX2@fpt.com", message);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("FPT đã hỗ trợ xử lý lỗi"), message);
    }

    [Fact]
    public void NotificationFormatter_StatusChanged_WithSnapshotEmail_FormatsEmailInBlockquote()
    {
        var email = new LatestEmail("102", new DateTimeOffset(2026, 10, 7, 21, 23, 0, TimeSpan.FromHours(7)),
            "customer@fpt.com", "Tiêu đề yêu cầu", "<p>Kính gửi TOC,</p><p>Nhờ kiểm tra máy chủ web bị quá tải.</p>");
        var snapshot = new TicketSnapshot
        {
            Code = "RQ202610070280",
            Status = TicketStatus.Closed,
            Title = "[Miền Bắc]-[HNID49102]-[Rack]-[LỖI KHÔNG CẬP NHẬT ĐƯỢC THÔNG TIN WEB]",
            AssigneeName = "HieuDX2 - TOC - Phòng Dịch vụ Data Center",
            LatestEmail = email,
            CreatedAt = new DateTimeOffset(2026, 10, 7, 19, 44, 0, TimeSpan.FromHours(7)),
            ClosedAt = new DateTimeOffset(2026, 10, 7, 21, 23, 0, TimeSpan.FromHours(7)),
            ClosedByName = "HieuDX2"
        };
        var evt = new TicketEvent
        {
            EventKey = "RQ202610070280|Terminal|InProgress|Closed",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.Terminal,
            PreviousStatus = TicketStatus.InProgress,
            CurrentStatus = TicketStatus.Closed,
            DetectedAt = new DateTimeOffset(2026, 10, 7, 21, 23, 10, TimeSpan.FromHours(7)),
            ChangedAt = new DateTimeOffset(2026, 10, 7, 21, 23, 0, TimeSpan.FromHours(7)),
            ChangedBy = "HieuDX2",
            Reason = "Trạng thái ticket đã thay đổi",
            LatestEmail = null, // Event itself has null LatestEmail, falls back to snapshot.LatestEmail
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net");

        Assert.Contains("TICKET ĐÃ ĐÓNG", message);
        Assert.Contains($"{System.Net.WebUtility.HtmlEncode(TicketStatus.InProgress.DisplayName())} ➔ {System.Net.WebUtility.HtmlEncode(TicketStatus.Closed.DisplayName())}", message);
        Assert.Contains("👤 <b>Người thực hiện:</b> HieuDX2", message);
        Assert.Contains("<blockquote>", message);
        Assert.Contains("From: customer@fpt.com", message);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("Nhờ kiểm tra máy chủ web bị quá tải"), message);
    }

    [Fact]
    public async Task ChangeDetector_EmailReceived_EventKey_DoesNotChangeWhenTicketStatusChanges()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var email = Email("200", sentAt, "Nội dung email kiểm thử");
        var initialSnapshotWithoutEmail = Snapshot("RQ-DEDUP", TicketStatus.Paused, null, sentAt.AddMinutes(-5));
        var pausedSnapshotWithEmail = Snapshot("RQ-DEDUP", TicketStatus.Paused, email, sentAt);
        var inProgressSnapshot = pausedSnapshotWithEmail with { Status = TicketStatus.InProgress };

        var detector = new TicketChangeDetector();
        var client = new TestFtmsClient(() => email);

        // First poll: ticket already existed, now new email arrives
        var events1 = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot> { [initialSnapshotWithoutEmail.Code] = initialSnapshotWithoutEmail },
            [pausedSnapshotWithEmail], client, new AppSettings(), CancellationToken.None);
        var emailEvent1 = Assert.Single(events1, e => e.EventType == TicketEventType.EmailReceived);
        var expectedKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("RQ-DEDUP|EmailReceived|200")));
        Assert.Equal(expectedKey, emailEvent1.EventKey);

        // Next poll: ticket status changes to InProgress with same email
        var events2 = await detector.DetectAsync(
            new Dictionary<string, TicketSnapshot> { [pausedSnapshotWithEmail.Code] = pausedSnapshotWithEmail },
            [inProgressSnapshot], client, new AppSettings(), CancellationToken.None);

        // Should NOT emit EmailReceived again because email has already been seen
        Assert.DoesNotContain(events2, e => e.EventType == TicketEventType.EmailReceived);
    }

    [Fact]
    public void ChangeDetector_Terminal_PreservesAssigneeName_CreatedAt_AndLatestEmail()
    {
        var createdAt = new DateTimeOffset(2026, 10, 7, 16, 48, 2, TimeSpan.FromHours(7));
        var closedAt = new DateTimeOffset(2026, 10, 7, 22, 26, 0, TimeSpan.FromHours(7));
        var email = new LatestEmail("202610070240", createdAt.AddMinutes(5), "customer@unilever.com",
            "ZBS_Hỗ trợ add temp cho KH UNow Unilever_07/10/2026",
            "<p>Kính gửi TOC,</p><p>Nhờ add template tin nhắn Zalo ZBS gấp giúp bên em.</p>");
        var activeSnapshot = new TicketSnapshot
        {
            Code = "RQ202610070240",
            Status = TicketStatus.InProgress,
            Title = "[Miền Nam]-[SGME00751]-[OTT Zalo]-[ZBS_Hỗ trợ add temp cho KH UNow Unilever_07/10/2026]",
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddMinutes(10),
            AssigneeId = 1234,
            AssigneeName = "AnhHVN3 - TOC - Phòng Dịch vụ Data Center",
            DepartmentId = 10,
            DepartmentName = "TOC - Phòng Dịch vụ Data Center",
            LatestEmail = email
        };

        // History API row often has null assigneeName, null latestEmail, and inverted US format createdAt
        var historySnapshot = new TicketSnapshot
        {
            Code = "RQ202610070240",
            Status = TicketStatus.Closed,
            Title = "[Miền Nam]-[SGME00751]-[OTT Zalo]-[ZBS_Hỗ trợ add temp cho KH UNow Unilever_07/10/2026]",
            CreatedAt = new DateTimeOffset(2026, 7, 10, 16, 48, 2, TimeSpan.FromHours(7)), // Inverted US date
            UpdatedAt = closedAt,
            ClosedAt = closedAt,
            ClosedByName = "AnhHVN3",
            AssigneeId = null,
            AssigneeName = null, // Missing in raw history
            LatestEmail = null // Missing in raw history
        };

        var detector = new TicketChangeDetector();
        var events = detector.DetectCritical(
            new Dictionary<string, TicketSnapshot> { [activeSnapshot.Code] = activeSnapshot },
            [historySnapshot], new AppSettings());

        var terminalEvent = Assert.Single(events);
        Assert.Equal(TicketEventType.Terminal, terminalEvent.EventType);
        Assert.Equal("AnhHVN3 - TOC - Phòng Dịch vụ Data Center", terminalEvent.Snapshot.AssigneeName);
        Assert.Equal(createdAt, terminalEvent.Snapshot.CreatedAt);
        Assert.NotNull(terminalEvent.Snapshot.LatestEmail);
        Assert.Equal(email.Id, terminalEvent.Snapshot.LatestEmail.Id);

        var message = NotificationFormatter.Format(terminalEvent, "https://ftms.fpt.net");
        Assert.Contains("TICKET ĐÃ ĐÓNG", message);
        Assert.Contains("Người xử lý:</b> AnhHVN3 - TOC - Ph&#242;ng Dịch vụ Data Center", message);
        Assert.DoesNotContain("Chưa nhận", message);
        Assert.Contains("07/10/2026 16:48", message);
        Assert.DoesNotContain("10/07/2026 16:48", message);
        Assert.Contains("<blockquote>", message);
        Assert.Contains("From: customer@unilever.com", message);
        Assert.Contains("ZBS_Hỗ trợ add temp cho KH UNow Unilever_07/10/2026", message);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("Nhờ add template tin nhắn Zalo"), message);
    }

    [Fact]
    public void NotificationFormatter_CreatedEvent_DoesNotIncludeUnassignedElapsedMinutes()
    {
        var createdAt = new DateTimeOffset(2026, 10, 8, 8, 58, 0, TimeSpan.FromHours(7));
        var detectedAt = createdAt.AddMinutes(1);
        var snapshot = new TicketSnapshot
        {
            Code = "RQ202610080017",
            Status = TicketStatus.New,
            CreatedAt = createdAt,
            AssigneeName = "Chưa nhận"
        };
        var evt = new TicketEvent
        {
            EventKey = "created-test-key",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.Created,
            CurrentStatus = TicketStatus.New,
            DetectedAt = detectedAt,
            Reason = "Phát hiện ticket mới",
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net/ihub");

        Assert.Contains("TICKET MỚI", message);
        Assert.Contains("Thời gian tạo:</b> 08/10/2026 08:58 (UTC+07:00)", message);
        Assert.DoesNotContain("Thời gian chưa nhận ticket", message);
        Assert.DoesNotContain("Thời gian từ lúc tạo ticket", message);
    }

    [Fact]
    public void NotificationFormatter_UnassignedReminder_IncludesUnassignedElapsedMinutes()
    {
        var createdAt = new DateTimeOffset(2026, 10, 8, 8, 58, 0, TimeSpan.FromHours(7));
        var detectedAt = createdAt.AddMinutes(15);
        var snapshot = new TicketSnapshot
        {
            Code = "RQ202610080017",
            Status = TicketStatus.New,
            CreatedAt = createdAt,
            AssigneeName = "Chưa nhận"
        };
        var evt = new TicketEvent
        {
            EventKey = "reminder-test-key",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.UnassignedReminder,
            CurrentStatus = TicketStatus.New,
            DetectedAt = detectedAt,
            Reason = "Ticket chưa được nhận sau 15 phút",
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net/ihub");

        Assert.Contains("NHẮC TICKET CHƯA ĐƯỢC NHẬN", message);
        Assert.Contains("Thời gian chưa nhận ticket:</b> 15 phút", message);
    }

    [Theory]
    [InlineData(TicketStatus.Assigned)]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.New)]
    [InlineData(TicketStatus.Paused)]
    [InlineData(TicketStatus.Completed)]
    public void NotificationFormatter_EmailReceived_AcrossStatuses_AlwaysUsesResponseTitle(TicketStatus status)
    {
        var sentAt = new DateTimeOffset(2026, 10, 8, 9, 15, 0, TimeSpan.FromHours(7));
        var email = Email("200", sentAt, "Khách hàng phản hồi thêm thông tin");
        var snapshot = Snapshot("IN202610080050", status, email, sentAt);
        var evt = new TicketEvent
        {
            EventKey = "email-received-key",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.EmailReceived,
            CurrentStatus = status,
            DetectedAt = sentAt.AddSeconds(5),
            Reason = "FTMS có email mới",
            LatestEmail = email,
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net/ihub");

        Assert.Contains("TICKET ĐÃ CÓ PHẢN HỒI MỚI", message);
        Assert.DoesNotContain("EMAIL MỚI CỦA TICKET", message);
    }

    [Fact]
    public void NotificationFormatter_EmailReceived_IncludesBothEmailTimeAndChangeTime()
    {
        var emailSentAt = new DateTimeOffset(2026, 10, 8, 9, 14, 30, TimeSpan.FromHours(7));
        var changeAt = new DateTimeOffset(2026, 10, 8, 9, 15, 0, TimeSpan.FromHours(7));
        var email = Email("201", emailSentAt, "Khách hàng gửi phản hồi mới");
        var snapshot = Snapshot("IN202610080055", TicketStatus.InProgress, email, changeAt);
        var evt = new TicketEvent
        {
            EventKey = "email-time-test-key",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.EmailReceived,
            CurrentStatus = TicketStatus.InProgress,
            DetectedAt = changeAt.AddSeconds(5),
            ChangedAt = changeAt,
            Reason = "FTMS có email mới",
            LatestEmail = email,
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net/ihub");

        Assert.Contains("TICKET ĐÃ CÓ PHẢN HỒI MỚI", message);
        Assert.Contains("Trạng thái:</b> Đang thực hiện", message);
        Assert.Contains("Thời gian thay đổi:</b> 08/10/2026 09:15 (UTC+07:00)", message);
        Assert.Contains("Time: 08/10/2026 09:14:30 (UTC+07:00)", message);
    }

    [Fact]
    public void NotificationFormatter_StatusChanged_FromPausedToInProgress_WithEmail_IncludesBothTimes()
    {
        var emailSentAt = new DateTimeOffset(2026, 10, 8, 9, 14, 0, TimeSpan.FromHours(7));
        var changeAt = new DateTimeOffset(2026, 10, 8, 9, 15, 0, TimeSpan.FromHours(7));
        var email = Email("202", emailSentAt, "Khách hàng phản hồi và ticket tự chuyển InProgress");
        var snapshot = Snapshot("RQ202610080060", TicketStatus.InProgress, email, changeAt);
        var evt = new TicketEvent
        {
            EventKey = "resume-test-key",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.StatusChanged,
            PreviousStatus = TicketStatus.Paused,
            CurrentStatus = TicketStatus.InProgress,
            DetectedAt = changeAt.AddSeconds(3),
            ChangedAt = changeAt,
            Reason = "FTMS có email mới",
            LatestEmail = email,
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net/ihub");

        Assert.Contains("TICKET ĐÃ CÓ PHẢN HỒI MỚI", message);
        Assert.Contains("Trạng thái:</b> Tạm ngưng ➔ Đang thực hiện", message);
        Assert.Contains("Thời gian thay đổi:</b> 08/10/2026 09:15 (UTC+07:00)", message);
        Assert.Contains("Time: 08/10/2026 09:14:00 (UTC+07:00)", message);
    }

    [Fact]
    public void NotificationFormatter_AssignmentChanged_IncludesChangeTime()
    {
        var changeAt = new DateTimeOffset(2026, 10, 8, 9, 20, 0, TimeSpan.FromHours(7));
        var snapshot = Snapshot("RQ202610080061", TicketStatus.Assigned, null, changeAt) with
        {
            AssigneeName = "DuyPK21"
        };
        var evt = new TicketEvent
        {
            EventKey = "assign-test-key",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.AssignmentChanged,
            PreviousStatus = TicketStatus.Assigned,
            CurrentStatus = TicketStatus.Assigned,
            PreviousAssigneeName = "Chưa nhận",
            DetectedAt = changeAt.AddSeconds(2),
            ChangedAt = changeAt,
            Reason = "Người xử lý hoặc phòng ban đã thay đổi",
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net/ihub");

        Assert.Contains("TICKET ĐÃ CÓ NGƯỜI NHẬN", message);
        Assert.Contains("Thời gian thay đổi:</b> 08/10/2026 09:20 (UTC+07:00)", message);
    }

    [Fact]
    public void NotificationFormatter_Terminal_ClosedTicket_IncludesChangeTime()
    {
        var closedAt = new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.FromHours(7));
        var snapshot = Snapshot("RQ202610080062", TicketStatus.Closed, null, closedAt);
        var evt = new TicketEvent
        {
            EventKey = "closed-test-key",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.Terminal,
            PreviousStatus = TicketStatus.InProgress,
            CurrentStatus = TicketStatus.Closed,
            DetectedAt = closedAt.AddSeconds(5),
            ChangedAt = closedAt,
            ChangedBy = "HieuDX2",
            Reason = "Đã hỗ trợ xong",
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net/ihub");

        Assert.Contains("TICKET ĐÃ ĐÓNG", message);
        Assert.Contains("Thời gian thay đổi:</b> 08/10/2026 09:30 (UTC+07:00)", message);
    }

    [Fact]
    public void NotificationFormatter_CreatedTicket_DoesNotIncludeChangeTime()
    {
        var createdAt = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(7));
        var snapshot = Snapshot("RQ202610080063", TicketStatus.New, null, createdAt) with
        {
            CreatedAt = createdAt
        };
        var evt = new TicketEvent
        {
            EventKey = "created-test-key-2",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.Created,
            CurrentStatus = TicketStatus.New,
            DetectedAt = createdAt.AddSeconds(20),
            Reason = "Phát hiện ticket mới",
            Snapshot = snapshot
        };

        var message = NotificationFormatter.Format(evt, "https://ftms.fpt.net/ihub");

        Assert.Contains("TICKET MỚI", message);
        Assert.Contains("Thời gian tạo:</b> 08/10/2026 09:00 (UTC+07:00)", message);
        Assert.DoesNotContain("Thời gian thay đổi", message);
    }

    private static LatestEmail Email(string id, DateTimeOffset sentAt, string body) =>
        new(id, sentAt, "sender@fpt.com", "Tiêu đề kiểm thử", body);

    private static TicketSnapshot Snapshot(string code, TicketStatus status, LatestEmail? email, DateTimeOffset updatedAt) => new()
    {
        Code = code,
        Status = status,
        Title = email?.Subject ?? "Tiêu đề",
        CreatedAt = updatedAt.AddHours(-1),
        UpdatedAt = updatedAt,
        AssigneeId = 42,
        AssigneeName = "DuyPK21",
        DepartmentId = 10,
        DepartmentName = "TOC - Phòng Dịch vụ Data Center",
        LatestEmail = email
    };

    private sealed class TestFtmsClient(
        Func<LatestEmail?>? onGetLatestEmail = null,
        Func<CancellationToken, Task<LatestEmail?>>? onGetLatestEmailAsync = null,
        Func<TicketStatus, CancellationToken, Task<StatusHistoryEntry?>>? onGetLatestStatusHistoryAsync = null) : IFtmsClient
    {
        public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CurrentUserIdentity?> GetCurrentUserAsync(CancellationToken cancellationToken) => Task.FromResult<CurrentUserIdentity?>(null);
        public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TicketSnapshot>>([]);
        public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(bool includeHistory, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TicketSnapshot>>([]);
        public Task<IReadOnlyList<TicketSnapshot>> GetClosedTicketsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TicketSnapshot>>([]);
        public Task<TicketClaimResult> ClaimTicketAsync(string ticketCode, long expectedUserId, CancellationToken cancellationToken) =>
            Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok"));
        public Task<LatestEmail?> GetLatestEmailAsync(string ticketCode, CancellationToken cancellationToken) =>
            onGetLatestEmailAsync?.Invoke(cancellationToken) ?? Task.FromResult(onGetLatestEmail?.Invoke());
        public Task<StatusHistoryEntry?> GetLatestStatusHistoryAsync(string ticketCode, TicketStatus status, CancellationToken cancellationToken) =>
            onGetLatestStatusHistoryAsync?.Invoke(status, cancellationToken) ?? Task.FromResult<StatusHistoryEntry?>(null);
        public Task BeginLoginRecoveryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
