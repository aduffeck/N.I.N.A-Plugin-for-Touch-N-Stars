#nullable enable
using System.Text.RegularExpressions;

namespace TouchNStars.Server.Services.Alarm;

/// <summary>A log record: the header line DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE and the first continuation line.</summary>
public sealed record LogRecord(string Level, string Source, string Member, string Message, string Detail);

/// <summary>
/// Joins N.I.N.A. log lines into records. A record starts with a header line and takes the lines up to the next
/// header (exception text). Logger.Error(ex) writes a header without a message field, the exception follows on the
/// next lines; its first line is the record's <see cref="LogRecord.Detail"/>. Not thread safe (the service loop).
/// </summary>
public sealed class LogRecordParser {
    private static readonly Regex Header = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{4}\|(TRACE|DEBUG|INFO|WARNING|ERROR|FATAL)\|",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private string[]? pending;
    private string pendingDetail = "";

    public bool HasPending => pending != null;

    /// <summary>Feeds one line; returns the record this line completed, if any.</summary>
    public LogRecord? Feed(string line) {
        line = line.TrimEnd('\r');
        if (Header.IsMatch(line)) {
            var done = Flush();
            pending = line.Split('|', 6);
            pendingDetail = "";
            return done;
        }

        // Banner lines of the log file are no continuation
        if (line.StartsWith("DATE|LEVEL|") || line.StartsWith("-")) {
            return null;
        }

        if (pending != null && pendingDetail.Length == 0 && line.Trim().Length > 0) {
            pendingDetail = line.Trim();
        }
        return null;
    }

    /// <summary>Completes the pending record (no more lines came for it).</summary>
    public LogRecord? Flush() {
        if (pending == null) {
            return null;
        }

        var fields = pending;
        pending = null;
        string Field(int i) => i < fields.Length ? fields[i].Trim() : "";
        return new LogRecord(Field(1), Field(2), Field(3), Field(5), pendingDetail);
    }
}
