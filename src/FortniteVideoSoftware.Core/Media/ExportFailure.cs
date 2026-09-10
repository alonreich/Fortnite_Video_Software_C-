using System;
using System.Collections.Generic;
using System.Text;

namespace FortniteVideoSoftware.Core.Media;

public enum ExportFailureCategory
{
    Unknown,
    MissingEncoder,
    CorruptInput,
    DiskFull,
    AccessDenied,
    StartupFailure,
    EncodingFailure,
    Timeout,
    Cancellation,
    DestinationError
}

public enum ExportStage
{
    Preflight,
    Analysis,
    Encoding,
    Concatenation,
    TwoPassTail,
    Finalizing,
    Thumbnail
}

public sealed record ExportAttemptIdentity
{
    public int AttemptIndex { get; init; } = 1;
    public string? Operation { get; init; }
    public string? Encoder { get; init; }
    public string? Description { get; init; }

    public override string ToString()
    {
        if (!string.IsNullOrWhiteSpace(Description)) return Description;
        string enc = !string.IsNullOrWhiteSpace(Encoder) ? $", encoder: {Encoder}" : "";
        string op = !string.IsNullOrWhiteSpace(Operation) ? Operation : "Export";
        return $"Attempt #{AttemptIndex} ({op}{enc})";
    }
}

public sealed record ExportFailure
{
    public ExportFailureCategory Category { get; init; } = ExportFailureCategory.Unknown;
    public ExportStage Stage { get; init; } = ExportStage.Encoding;
    public ExportAttemptIdentity? Attempt { get; init; }
    public int? ProcessExitCode { get; init; }
    public int? NativeErrorCode { get; init; }
    public string? NativeErrorSource { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? SpecificCause { get; init; }
    public IReadOnlyList<string> DiagnosticLines { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ExportFailure> EarlierAttempts { get; init; } = Array.Empty<ExportFailure>();

    public string FormatDiagnosticReport()
    {
        var sb = new StringBuilder();

        if (Attempt != null)
        {
            sb.AppendLine($"Attempt: {Attempt}");
        }

        sb.AppendLine($"Stage: {Stage}");
        sb.AppendLine($"Category: {Category}");

        if (ProcessExitCode.HasValue)
        {
            sb.AppendLine($"Process Exit Code: {ProcessExitCode.Value}");
        }

        if (NativeErrorCode.HasValue)
        {
            string source = !string.IsNullOrWhiteSpace(NativeErrorSource) ? $" ({NativeErrorSource})" : "";
            sb.AppendLine($"Native Error Code: {NativeErrorCode.Value}{source}");
        }

        if (!string.IsNullOrWhiteSpace(SpecificCause))
        {
            sb.AppendLine($"Root Cause: {SpecificCause}");
        }

        if (EarlierAttempts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Earlier Attempts:");
            foreach (var earlier in EarlierAttempts)
            {
                string desc = earlier.Attempt?.ToString() ?? "Earlier Attempt";
                string cause = !string.IsNullOrWhiteSpace(earlier.SpecificCause)
                    ? earlier.SpecificCause
                    : (!string.IsNullOrWhiteSpace(earlier.Summary) ? earlier.Summary : earlier.Category.ToString());
                sb.AppendLine($"  - {desc}: {cause}");
            }
        }

        if (DiagnosticLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Diagnostic Details:");
            foreach (var line in DiagnosticLines)
            {
                sb.AppendLine($"  {line}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
