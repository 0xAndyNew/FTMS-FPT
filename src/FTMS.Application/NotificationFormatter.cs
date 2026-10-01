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
        var text = new StringBuilder();
        text.AppendLine(item.EventType switch
        {
            TicketEventType.Created => "📨 <b>🔴 TICKET MỚI</b>",
            TicketEventType.EmailReceived => "📧 <b>🔴 EMAIL MỚI CỦA TICKET</b>",
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
            text.AppendLine($"👤 <b>Người thay đổi:</b> {Escape(item.ChangedBy.Trim())}");
        if (item.EventType is not TicketEventType.AssignmentChanged and not TicketEventType.Created and
            not TicketEventType.UnassignedReminder and not TicketEventType.ResponseReminder)
            text.AppendLine($"👨‍💼 <b>Người xử lý:</b> {Escape(NormalizeEmpty(ticket.AssigneeName, "Chưa nhận"))}");
        var ageMinutes = ticket.CreatedAt is null ? 0 : Math.Max(0, (int)(item.DetectedAt - ticket.CreatedAt.Value).TotalMinutes);
        if (item.EventType is TicketEventType.Created or TicketEventType.UnassignedReminder)
            text.AppendLine($"⏱ <b>Thời gian chưa nhận ticket:</b> {ageMinutes} phút");
        else if (item.EventType == TicketEventType.ResponseReminder)
        {
            var responseMinutes = ticket.ResponseReminderSince is null ? 0 :
                Math.Max(0, (int)(item.DetectedAt - ticket.ResponseReminderSince.Value).TotalMinutes);
            text.AppendLine($"⏱ <b>Thời gian từ lúc có phản hồi mới:</b> {responseMinutes} phút");
        }
        else if (item.EventType != TicketEventType.Terminal || ticket.Status != TicketStatus.Closed)
            text.AppendLine($"🕰 <b>Thời gian tồn tại từ lúc nhận ticket:</b> {ageMinutes} phút");
        var changeTime = item.ChangedAt ?? (ticket.Status == TicketStatus.Closed ? ticket.UpdatedAt : null);
        if (isStatusTransition && changeTime is not null)
            text.AppendLine($"🗓 <b>Thời gian thay đổi:</b> {FormatVietnamTime(changeTime.Value, includeSeconds: false)}");
        if (item.EventType == TicketEventType.AssignmentChanged)
        {
            if (!string.Equals(item.PreviousAssigneeName, ticket.AssigneeName, StringComparison.OrdinalIgnoreCase))
                text.AppendLine($"👥 <b>Người xử lý:</b> {Escape(NormalizeEmpty(item.PreviousAssigneeName, "Chưa nhận"))} ➔ {Escape(NormalizeEmpty(ticket.AssigneeName, "Chưa nhận"))}");
            if (!string.Equals(item.PreviousDepartmentName, ticket.DepartmentName, StringComparison.OrdinalIgnoreCase))
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
        var email = item.LatestEmail is { } eventEmail && !eventEmail.IsExcluded()
            ? eventEmail : ticket.LatestEmail is { } snapshotEmail && !snapshotEmail.IsExcluded()
                ? snapshotEmail : null;
        var originalTicketTitle = string.IsNullOrWhiteSpace(ticket.Title) ? email?.Subject : ticket.Title;
        var emailBody = CleanEmail(email?.Body);
        if (email is not null || !string.IsNullOrWhiteSpace(originalTicketTitle))
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
        var quotedHeader = QuotedHeaderRegex().Match(value);
        if (quotedHeader.Success && quotedHeader.Index > 20)
            value = value[..quotedHeader.Index].Trim();
        foreach (var marker in new[]
        {
            "THÔNG BÁO BẢO MẬT", "THONG BAO BAO MAT", "CONFIDENTIALITY NOTICE",
            "__________________________", "*************************"
        })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) value = value[..index].Trim();
        }
        foreach (var marker in new[]
        {
            "Thanks & Best regards", "Thanks and Best regards", "Best regards", "Kind regards", "Regards,",
            "Trân trọng", "Tải Outlook for", "-----Original Message-----", "\nTừ:", "\nTừ:", "\nFrom:"
        })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 20) value = value[..index].Trim();
        }
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
            value = value[..signatureStart].Trim();
        }
        value = value.Length > 2500 ? value[..2500] + "..." : value;
        return value;
    }

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string AssignmentTitle(TicketEvent item)
    {
        var hadAssignee = !string.IsNullOrWhiteSpace(item.PreviousAssigneeName) && item.PreviousAssigneeName.Trim() != "---";
        var hasAssignee = !string.IsNullOrWhiteSpace(item.Snapshot.AssigneeName) && item.Snapshot.AssigneeName.Trim() != "---";
        if (!hadAssignee && hasAssignee) return "🙋 <b>🟢 TICKET ĐÃ CÓ NGƯỜI NHẬN</b>";
        if (!string.Equals(item.PreviousAssigneeName, item.Snapshot.AssigneeName, StringComparison.OrdinalIgnoreCase))
            return "👥 <b>🔵 TICKET ĐÃ CHUYỂN NGƯỜI XỬ LÝ</b>";
        return "🏢 <b>🔵 TICKET ĐÃ CHUYỂN PHÒNG BAN</b>";
    }

    private static string NormalizeEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "---" ? fallback : value.Trim();

    private static string FormatVietnamTime(DateTimeOffset value, bool includeSeconds = true) =>
        value.ToOffset(TimeSpan.FromHours(7)).ToString(includeSeconds ? "dd/MM/yyyy HH:mm:ss" : "dd/MM/yyyy HH:mm",
            CultureInfo.InvariantCulture) +
        " (UTC+07:00)";

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
    [GeneratedRegex(@"<hr\b|id\s*=\s*['""](?:divRplyFwdMsg|x_divRplyFwdMsg|appendonsend)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex LatestMessageHtmlRegex();
    [GeneratedRegex(@"<br\s*/?>|</?(?:p|div|tr|li|blockquote|h[1-6])\b[^>]*>|</(?:td|th)>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();
    [GeneratedRegex(@"[^\S\r\n]+")]
    private static partial Regex LineWhitespaceRegex();
    [GeneratedRegex(@"(?:\r?\n\s*){3,}")]
    private static partial Regex MultiLineRegex();
    [GeneratedRegex(@"From:\s*.{0,200}?\b(?:Sent|Date):", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex QuotedHeaderRegex();
}
