using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FTMS.Domain;

namespace FTMS.Application;

public static partial class NotificationFormatter
{
    public static string Format(TicketEvent item, string ihubBaseUrl)
    {
        var ticket = item.Snapshot;
        var email = item.LatestEmail is { } eventEmail && !eventEmail.IsExcluded()
            ? eventEmail : ticket.LatestEmail is { } snapshotEmail && !snapshotEmail.IsExcluded()
                ? snapshotEmail : null;
        var text = new StringBuilder();
        text.AppendLine(item.EventType switch
        {
            TicketEventType.Created => "📨 <b>🔴 TICKET MỚI</b>",
            TicketEventType.EmailReceived => "📧 <b>🔴 TICKET ĐÃ CÓ PHẢN HỒI MỚI</b>",
            TicketEventType.UnassignedReminder => "🔔 <b>🟠 NHẮC TICKET CHƯA ĐƯỢC NHẬN</b>",
            TicketEventType.ResponseReminder => "🔔 <b>🟠 NHẮC TICKET ĐÃ CÓ PHẢN HỒI MỚI</b>",
            TicketEventType.SlaThresholdReached when ticket.SlaType == 3 => "🚨 <b>🔴 ĐÃ VI PHẠM SLA</b>",
            TicketEventType.SlaThresholdReached => "⏰ <b>🟠 SẮP VI PHẠM SLA</b>",
            TicketEventType.Terminal => ticket.Status switch
            {
                TicketStatus.Closed => "🔒 <b>⚫ TICKET ĐÃ ĐÓNG</b>",
                TicketStatus.Cancelled => "🚫 <b>⚫ TICKET ĐÃ HỦY</b>",
                TicketStatus.Unprocessed => "⛔ <b>⚫ TICKET KHÔNG XỬ LÝ</b>",
                _ => "✅ <b>🟢 TICKET KẾT THÚC</b>"
            },
            TicketEventType.StatusChanged when ticket.Status == TicketStatus.InProgress &&
                item.Reason.Contains("email mới", StringComparison.OrdinalIgnoreCase) => "📧 <b>🔴 TICKET ĐÃ CÓ PHẢN HỒI MỚI</b>",
            TicketEventType.StatusChanged => "🔄 <b>🔵 THAY ĐỔI TRẠNG THÁI</b>",
            TicketEventType.AssignmentChanged => AssignmentTitle(item),
            _ => "📣 <b>🔵 CẬP NHẬT TICKET</b>"
        });
        text.AppendLine($"🆔 <b>MÃ REQUEST:</b> <code>{Escape(ticket.Code)}</code>");
        var isStatusTransition = item.EventType is TicketEventType.StatusChanged or TicketEventType.Terminal &&
            item.PreviousStatus is not null && item.PreviousStatus.Value != ticket.Status;
        if (item.EventType is not TicketEventType.Created and not TicketEventType.UnassignedReminder and not TicketEventType.ResponseReminder)
            text.AppendLine(isStatusTransition
                ? $"📌 <b>Trạng thái:</b> {Escape(item.PreviousStatus!.Value.DisplayName())} ➔ {Escape(ticket.Status.DisplayName())}"
                : $"📌 <b>Trạng thái:</b> {Escape(ticket.Status.DisplayName())}");
        if (isStatusTransition && !string.IsNullOrWhiteSpace(item.ChangedBy) && item.ChangedBy.Trim() != "---")
            text.AppendLine($"👤 <b>Người thực hiện:</b> {Escape(item.ChangedBy.Trim())}");
        if (!string.IsNullOrWhiteSpace(item.Note))
            text.AppendLine($"📝 <b>Ghi chú:</b> {Escape(item.Note.Trim())}");
        else if (!string.IsNullOrWhiteSpace(item.Reason) &&
            !string.Equals(item.Reason, "Trạng thái ticket đã thay đổi", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.Reason, "Người xử lý hoặc phòng ban đã thay đổi", StringComparison.OrdinalIgnoreCase) &&
            !item.Reason.Contains("email mới", StringComparison.OrdinalIgnoreCase) &&
            !item.Reason.Contains("Phát hiện ticket mới", StringComparison.OrdinalIgnoreCase) &&
            item.EventType is TicketEventType.StatusChanged or TicketEventType.Terminal)
            text.AppendLine($"📝 <b>Ghi chú:</b> {Escape(item.Reason.Trim())}");
        if (item.EventType is not TicketEventType.AssignmentChanged and not TicketEventType.Created and
            not TicketEventType.UnassignedReminder and not TicketEventType.ResponseReminder)
            text.AppendLine($"👨‍💼 <b>Người xử lý:</b> {Escape(NormalizeEmpty(ticket.AssigneeName, "Chưa nhận"))}");
        if (ticket.CreatedAt is not null)
            text.AppendLine($"⏰ <b>Thời gian tạo:</b> {FormatVietnamTime(ticket.CreatedAt.Value)}");
        var ageMinutes = ticket.CreatedAt is null ? 0 : Math.Max(0, (int)(item.DetectedAt - ticket.CreatedAt.Value).TotalMinutes);
        if (item.EventType == TicketEventType.UnassignedReminder)
            text.AppendLine($"⏱ <b>Thời gian chưa nhận ticket:</b> {ageMinutes} phút");
        else if (item.EventType == TicketEventType.ResponseReminder)
        {
            var responseMinutes = ticket.ResponseReminderSince is null ? 0 :
                Math.Max(0, (int)(item.DetectedAt - ticket.ResponseReminderSince.Value).TotalMinutes);
            text.AppendLine($"⏱ <b>Thời gian từ lúc có phản hồi mới:</b> {responseMinutes} phút");
        }
        else if (item.EventType != TicketEventType.Created &&
                 (item.EventType != TicketEventType.Terminal || ticket.Status != TicketStatus.Closed))
            text.AppendLine($"🕰 <b>Thời gian từ lúc tạo ticket:</b> {ageMinutes} phút");
        var shouldShowChangeTime = item.EventType is not TicketEventType.Created &&
            item.EventType is not TicketEventType.UnassignedReminder &&
            item.EventType is not TicketEventType.ResponseReminder &&
            item.EventType is not TicketEventType.SlaThresholdReached;
        DateTimeOffset? changeTime = item.ChangedAt ?? ticket.UpdatedAt ??
            (item.EventType == TicketEventType.EmailReceived ? email?.SentAt : null) ?? item.DetectedAt;
        if (shouldShowChangeTime && changeTime is not null)
            text.AppendLine($"🗓 <b>Thời gian thay đổi:</b> {FormatVietnamTime(changeTime.Value)}");
        if (item.EventType == TicketEventType.AssignmentChanged)
        {
            if (AssigneeChanged(item))
                text.AppendLine($"👥 <b>Người xử lý:</b> {Escape(NormalizeEmpty(item.PreviousAssigneeName, "Chưa nhận"))} ➔ {Escape(NormalizeEmpty(ticket.AssigneeName, "Chưa nhận"))}");
            if (DepartmentChanged(item))
                text.AppendLine($"🏢 <b>Phòng ban:</b> {Escape(NormalizeEmpty(item.PreviousDepartmentName, "Chưa có"))} ➔ {Escape(NormalizeEmpty(ticket.DepartmentName, "Chưa có"))}");
        }
        if (item.EventType == TicketEventType.SlaThresholdReached)
        {
            text.AppendLine($"⚠️ <b>SLA:</b> {Escape(item.Reason)}");
            if (ticket.SlaDeviationMinutes is not null)
                text.AppendLine(ticket.SlaType == 3
                    ? $"⛔ <b>Số phút vi phạm:</b> {Math.Abs(ticket.SlaDeviationMinutes.Value)} phút"
                    : $"⏳ <b>Thời gian còn lại:</b> {ticket.SlaDeviationMinutes.Value} phút");
        }
        var originalTicketTitle = string.IsNullOrWhiteSpace(ticket.Title) ? email?.Subject : ticket.Title;
        var emailBody = CleanEmail(email?.Body);
        if (string.IsNullOrWhiteSpace(emailBody) && !string.IsNullOrWhiteSpace(email?.Body))
        {
            emailBody = email.Body.Trim();
        }
        var shouldRenderBlockquote = email is not null &&
            (!string.IsNullOrWhiteSpace(emailBody) || !string.IsNullOrWhiteSpace(email.From) || email.SentAt is not null || !string.IsNullOrWhiteSpace(originalTicketTitle));
        if (shouldRenderBlockquote)
        {
            text.AppendLine();
            text.AppendLine("<blockquote>");
            if (!string.IsNullOrWhiteSpace(email?.From)) text.AppendLine($"From: {Escape(email.From)}");
            if (email?.SentAt is not null) text.AppendLine($"Time: {FormatVietnamTime(email.SentAt.Value)}");
            if (!string.IsNullOrWhiteSpace(originalTicketTitle))
                text.AppendLine($"📝 <b>Tiêu đề: {Escape(originalTicketTitle)}</b>");
            if (!string.IsNullOrWhiteSpace(emailBody))
            {
                text.AppendLine();
                text.AppendLine(Escape(emailBody));
            }
            text.AppendLine("</blockquote>");
        }
        return text.ToString().Trim();
    }

    public static string CleanEmail(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        html = LatestMessageHtmlRegex().Split(html, 2)[0];
        var value = BreakRegex().Replace(html, "\n");
        value = WebUtility.HtmlDecode(HtmlTagRegex().Replace(value, " "));
        value = LineWhitespaceRegex().Replace(value, " ");
        value = MultiLineRegex().Replace(value, "\n").Trim();
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var fallbackClean = value;

        // 1. Cut quoted email thread / original message headers
        var quotedHeader = QuotedHeaderRegex().Match(value);
        if (quotedHeader.Success && quotedHeader.Index > 20)
        {
            var candidate = value[..quotedHeader.Index].Trim();
            if (candidate.Length >= 15) value = candidate;
        }

        // 2. Cut standard confidentiality notices and separator lines
        foreach (var marker in new[]
        {
            "THÔNG BÁO BẢO MẬT", "THONG BAO BAO MAT", "CONFIDENTIALITY NOTICE", "IMPORTANT NOTICE",
            "__________________________", "*************************"
        })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var candidate = value[..index].Trim();
                if (candidate.Length >= 15) value = candidate;
            }
        }

        // 3. Cut mobile signatures and original message markers
        foreach (var marker in new[]
        {
            "Tải Outlook for", "Get Outlook for", "Sent from my iPhone", "Sent from my iPad",
            "-----Original Message-----", "-----Tin nhắn gốc-----"
        })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 20)
            {
                var candidate = value[..index].Trim();
                if (candidate.Length >= 15) value = candidate;
            }
        }

        // 4. Cut closing signatures intelligently:
        // Only match when closing phrase is on its own line (or starts a closing block),
        // preventing accidental truncation of sentences like "Chúng tôi trân trọng thông báo..."
        var closingMatch = ClosingLineRegex().Match(value);
        if (closingMatch.Success && closingMatch.Index > 20)
        {
            // Verify there is substantial content before the closing phrase
            var beforeClosing = value[..closingMatch.Index].Trim();
            if (beforeClosing.Length >= 15)
            {
                value = beforeClosing;
            }
        }

        // 5. Cut corporate signature blocks by known company/department names
        foreach (var marker in new[]
        {
            "FPT Telecom International Co., Ltd", "FPT Telecom International",
            "CTY TNHH MTV Viễn Thông Quốc Tế FPT", "CÔNG TY TNHH MTV VIỄN THÔNG QUỐC TẾ FPT",
            "Nhân viên Hỗ trợ kỹ thuật", "Data Center Services Provider", "ITSM - PHÒNG HỖ TRỢ KHÁCH HÀNG"
        })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index <= 20) continue;
            var previousLine = value.LastIndexOf('\n', index - 1);
            var signatureStart = previousLine > 0 ? value.LastIndexOf('\n', previousLine - 1) : -1;
            if (signatureStart < 0) signatureStart = previousLine >= 0 ? previousLine + 1 : index;
            if (signatureStart > 0)
            {
                var candidate = value[..signatureStart].Trim();
                if (candidate.Length >= 15) value = candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(fallbackClean))
            value = fallbackClean;

        // Telegram limit is 4096 chars per message; allow up to 3500 chars for body alone
        if (value.Length > 3500)
            value = value[..3500] + "...";

        return value;
    }

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string AssignmentTitle(TicketEvent item)
    {
        var hadAssignee = item.PreviousAssigneeId is not null and not 0 || HasAssigneeName(item.PreviousAssigneeName);
        var hasAssignee = item.Snapshot.AssigneeId is not null and not 0 || HasAssigneeName(item.Snapshot.AssigneeName);
        if (!hadAssignee && hasAssignee) return "🙋 <b>🟢 TICKET ĐÃ CÓ NGƯỜI NHẬN</b>";
        if (AssigneeChanged(item)) return "👥 <b>🔵 TICKET ĐÃ CHUYỂN NGƯỜI XỬ LÝ</b>";
        return "🏢 <b>🔵 TICKET ĐÃ CHUYỂN PHÒNG BAN</b>";
    }

    private static bool AssigneeChanged(TicketEvent item) =>
        item.PreviousAssigneeId is not null || item.Snapshot.AssigneeId is not null
            ? item.PreviousAssigneeId != item.Snapshot.AssigneeId
            : !string.Equals(item.PreviousAssigneeName, item.Snapshot.AssigneeName,
                StringComparison.OrdinalIgnoreCase);

    private static bool DepartmentChanged(TicketEvent item) =>
        item.PreviousDepartmentId is not null || item.Snapshot.DepartmentId is not null
            ? item.PreviousDepartmentId != item.Snapshot.DepartmentId
            : !string.Equals(item.PreviousDepartmentName, item.Snapshot.DepartmentName,
                StringComparison.OrdinalIgnoreCase);

    private static bool HasAssigneeName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim() != "---" &&
        !value.Trim().Equals("Chưa nhận", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "---" ? fallback : value.Trim();

    private static string FormatVietnamTime(DateTimeOffset value) =>
        value.ToOffset(TimeSpan.FromHours(7)).ToString("HH:mm:ss dd/MM/yyyy", CultureInfo.InvariantCulture);

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
    [GeneratedRegex(@"<hr\b[^>]*tabindex\s*=\s*['""]?-1|<hr\b[^>]*id\s*=\s*['""]?(?:divRplyFwdMsg|x_divRplyFwdMsg|appendonsend)['""]?|<(?:div|span|p)\b[^>]*id\s*=\s*['""](?:divRplyFwdMsg|x_divRplyFwdMsg|appendonsend)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex LatestMessageHtmlRegex();
    [GeneratedRegex(@"<br\s*/?>|</?(?:p|div|tr|li|blockquote|h[1-6])\b[^>]*>|</(?:td|th)>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();
    [GeneratedRegex(@"[^\S\r\n]+")]
    private static partial Regex LineWhitespaceRegex();
    [GeneratedRegex(@"(?:\r?\n\s*){3,}")]
    private static partial Regex MultiLineRegex();
    [GeneratedRegex(@"(?:\r?\n|^)\s*(?:From|Từ|Từ)\s*:\s*.{0,200}?\b(?:Sent|Date|Gửi|Đã gửi|Ngày)\s*:", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex QuotedHeaderRegex();
    [GeneratedRegex(@"(?m)^\s*(?:(?:many\s+)?thanks?\s*(?:&|and)\s*(?:best\s*)?regards?|best\s*regards?|kind\s*regards?|regards|trân\s*trọng(?:\s+(?:cảm\s*ơn|kính\s*chào))?|tran\s*trong|thân\s*ái|than\s*ai)\b[,\s.:!]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ClosingLineRegex();
}
