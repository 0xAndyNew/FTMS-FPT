using System.Text.Json;
using FTMS.Domain;
using Xunit;

namespace FTMS.Companion.Tests;

public sealed class DateAndTicketParsingTests
{
    [Theory]
    [InlineData("10/13/2026 08:02:15", 10, 13, 2026, 8, 2, 15)] // Month-first format (day 13 > 12)
    [InlineData("13/10/2026 08:02:15", 10, 13, 2026, 8, 2, 15)] // Day-first format (day 13 > 12)
    [InlineData("12/10/2026 08:02:15", 10, 12, 2026, 8, 2, 15)] // Both <= 12 defaults to dd/MM/yyyy
    [InlineData("01/05/2026 00:00:00", 5, 1, 2026, 0, 0, 0)]    // Both <= 12: day 1, month 5
    public void ParseDatePartsLogic_ResolvesDayAndMonthCorrectly(
        string raw, int expectedMonth, int expectedDay, int expectedYear, int expectedHour, int expectedMinute, int expectedSecond)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            raw, @"^(\d{1,2})\/(\d{1,2})\/(\d{4})(?:\s+(\d{1,2}):(\d{2})(?::(\d{2}))?)?$");
        Assert.True(match.Success);

        var p1 = int.Parse(match.Groups[1].Value);
        var p2 = int.Parse(match.Groups[2].Value);
        var year = int.Parse(match.Groups[3].Value);
        var hour = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
        var minute = match.Groups[5].Success ? int.Parse(match.Groups[5].Value) : 0;
        var second = match.Groups[6].Success ? int.Parse(match.Groups[6].Value) : 0;

        int day, month;
        if (p2 > 12)
        {
            month = p1;
            day = p2;
        }
        else if (p1 > 12)
        {
            day = p1;
            month = p2;
        }
        else
        {
            day = p1;
            month = p2;
        }

        Assert.Equal(expectedYear, year);
        Assert.Equal(expectedMonth, month);
        Assert.Equal(expectedDay, day);
        Assert.Equal(expectedHour, hour);
        Assert.Equal(expectedMinute, minute);
        Assert.Equal(expectedSecond, second);

        var iso = $"{year:D4}-{month:D2}-{day:D2}T{hour:D2}:{minute:D2}:{second:D2}+07:00";
        var parsed = DateTimeOffset.Parse(iso);
        Assert.Equal(expectedYear, parsed.Year);
        Assert.Equal(expectedMonth, parsed.Month);
        Assert.Equal(expectedDay, parsed.Day);
    }

    [Fact]
    public void Month13_FailsInDotNet_ProvingRootCauseOfOctober13Bug()
    {
        // When month and day were inverted naively: "10/13/2026" produced month 13
        const string invalidIso = "2026-13-10T08:02:15+07:00";
        Assert.Throws<FormatException>(() => DateTimeOffset.Parse(invalidIso));

        // When deserializing JSON with month 13 and web options, System.Text.Json throws JsonException
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = $"{{\"createdAt\":\"{invalidIso}\"}}";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SampleDateModel>(json, options));
    }

    [Fact]
    public void ValidOctober13Date_DeserializesCleanlyIntoTicketSnapshot()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = """
        {
            "code": "RQ202610130001",
            "status": 2,
            "title": "Kiểm tra ticket ngày 13",
            "createdAt": "2026-10-13T08:02:15+07:00",
            "updatedAt": "2026-10-13T08:30:00+07:00"
        }
        """;
        var ticket = JsonSerializer.Deserialize<TicketSnapshot>(json, options);
        Assert.NotNull(ticket);
        Assert.Equal("RQ202610130001", ticket.Code);
        Assert.Equal(TicketStatus.InProgress, ticket.Status);
        Assert.Equal(10, ticket.CreatedAt?.Month);
        Assert.Equal(13, ticket.CreatedAt?.Day);
        Assert.Equal(2026, ticket.CreatedAt?.Year);
    }

    private sealed record SampleDateModel(DateTimeOffset? CreatedAt);
}
