// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace FortniteVideoSoftware.Core.Media;

/// <summary>
/// Conservatively identifies the root cause of an FFmpeg export failure.
/// Prefers process-start exceptions and explicit FFmpeg error codes, then tested text patterns,
/// and returns an honest Unknown category when evidence is insufficient.
/// </summary>
public static class FfmpegErrorClassifier
{
    private static readonly (string Needle, ExportFailureCategory Category, string Summary)[] KnownPatterns =
    [
        // Disk space
        ("No space left on device", ExportFailureCategory.DiskFull, "The drive ran out of free space while writing the video."),
        ("Disk quota exceeded", ExportFailureCategory.DiskFull, "The drive ran out of free space while writing the video."),
        ("There is not enough space on the disk", ExportFailureCategory.DiskFull, "The drive ran out of free space while writing the video."),
        ("Not enough space", ExportFailureCategory.DiskFull, "The drive ran out of free space while writing the video."),

        // Permissions / Access
        ("Permission denied", ExportFailureCategory.AccessDenied, "Windows blocked access to a file or folder needed for this export."),
        ("Access is denied", ExportFailureCategory.AccessDenied, "Windows blocked access to a file or folder needed for this export."),
        ("Operation not permitted", ExportFailureCategory.AccessDenied, "Windows blocked access to a file or folder needed for this export."),

        // Corrupt / unreadable input
        ("moov atom not found", ExportFailureCategory.CorruptInput, "The source video file is corrupt, incomplete, or in an unreadable format."),
        ("Invalid data found when processing input", ExportFailureCategory.CorruptInput, "The source video file is corrupt or is not a video format the app can read."),
        ("error reading header", ExportFailureCategory.CorruptInput, "The source video file header is unreadable."),
        ("could not find codec parameters", ExportFailureCategory.CorruptInput, "The source video file format could not be identified."),
        ("EBML header parsing failed", ExportFailureCategory.CorruptInput, "The source video file header is corrupt."),

        // Missing / unsupported encoder
        ("Unknown encoder", ExportFailureCategory.MissingEncoder, "The requested video encoder is not available on this system."),
        ("Cannot load nvcuda", ExportFailureCategory.MissingEncoder, "The NVIDIA driver could not be loaded, so the graphics card cannot be used for this export."),
        ("No NVENC capable devices found", ExportFailureCategory.MissingEncoder, "No NVIDIA encoder was found on this machine."),
        ("Error creating a MFX session", ExportFailureCategory.MissingEncoder, "The Intel hardware encoder is not supported or failed to initialize."),
        ("The current mfx implementation is not supported", ExportFailureCategory.MissingEncoder, "The Intel hardware encoder is not supported or failed to initialize."),
        ("Device creation failed", ExportFailureCategory.MissingEncoder, "Hardware acceleration device creation failed."),
        ("No VAAPI device", ExportFailureCategory.MissingEncoder, "The hardware acceleration device was not found."),
        ("Failed to create Direct3D", ExportFailureCategory.MissingEncoder, "Direct3D hardware acceleration could not be initialized."),

        // Encoding session / filter / internal pipeline failures
        ("OpenEncodeSessionEx failed", ExportFailureCategory.EncodingFailure, "The graphics card refused a new encoding session. Close other apps that are recording or streaming and try again."),
        ("Impossible to convert between the formats", ExportFailureCategory.EncodingFailure, "The video filter chain could not process the frames it was given."),
        ("Error initializing filter", ExportFailureCategory.EncodingFailure, "A video filter failed to initialize."),
        ("Filtergraph cannot have both cpu and cuda frames", ExportFailureCategory.EncodingFailure, "Hardware and software video frame formats could not be bridged."),
        ("Error while opening encoder", ExportFailureCategory.EncodingFailure, "The video encoder failed to open."),
        ("Output file is empty", ExportFailureCategory.EncodingFailure, "FFmpeg completed without writing any video frames to the output file."),
        ("Conversion failed", ExportFailureCategory.EncodingFailure, "FFmpeg stopped before completing the export.")
    ];

    public static ExportFailure Classify(
        ExportStage stage,
        ExportAttemptIdentity attempt,
        int? processExitCode,
        Exception? processStartException,
        bool isTimeout,
        bool isCancellation,
        FfmpegDiagnosticCollector? collector = null,
        IReadOnlyList<string>? explicitLines = null,
        IReadOnlyList<ExportFailure>? earlierAttempts = null)
    {
        var diagnostics = collector != null
            ? collector.GetDiagnosticLines()
            : (explicitLines ?? Array.Empty<string>());

        var previousAttempts = earlierAttempts ?? Array.Empty<ExportFailure>();

        // 1. User cancellation
        if (isCancellation)
        {
            return new ExportFailure
            {
                Category = ExportFailureCategory.Cancellation,
                Stage = stage,
                Attempt = attempt,
                ProcessExitCode = processExitCode,
                Summary = "The export was cancelled.",
                SpecificCause = "User cancellation",
                DiagnosticLines = diagnostics,
                EarlierAttempts = previousAttempts
            };
        }

        // 2. Timeout
        if (isTimeout)
        {
            return new ExportFailure
            {
                Category = ExportFailureCategory.Timeout,
                Stage = stage,
                Attempt = attempt,
                ProcessExitCode = processExitCode,
                Summary = "The export timed out before completing.",
                SpecificCause = "Process execution timed out",
                DiagnosticLines = diagnostics,
                EarlierAttempts = previousAttempts
            };
        }

        // 3. Process startup exceptions
        if (processStartException != null)
        {
            return ClassifyStartupException(processStartException, stage, attempt, diagnostics, previousAttempts);
        }

        int? nativeCode = collector?.ExplicitErrorCode;
        string? nativeSource = collector?.ExplicitErrorSource;

        // 4. Explicit FFmpeg codes (when unambiguous)
        if (nativeCode.HasValue)
        {
            switch (nativeCode.Value)
            {
                case -28: // ENOSPC
                    return new ExportFailure
                    {
                        Category = ExportFailureCategory.DiskFull,
                        Stage = stage,
                        Attempt = attempt,
                        ProcessExitCode = processExitCode,
                        NativeErrorCode = -28,
                        NativeErrorSource = nativeSource ?? "FFmpeg",
                        Summary = "The drive ran out of free space while writing the video.",
                        SpecificCause = "FFmpeg error -28 (No space left on device)",
                        DiagnosticLines = diagnostics,
                        EarlierAttempts = previousAttempts
                    };
                case -13: // EACCES
                    return new ExportFailure
                    {
                        Category = ExportFailureCategory.AccessDenied,
                        Stage = stage,
                        Attempt = attempt,
                        ProcessExitCode = processExitCode,
                        NativeErrorCode = -13,
                        NativeErrorSource = nativeSource ?? "FFmpeg",
                        Summary = "Windows blocked access to a file or folder needed for export.",
                        SpecificCause = "FFmpeg error -13 (Permission denied)",
                        DiagnosticLines = diagnostics,
                        EarlierAttempts = previousAttempts
                    };
                case -1094995529: // AVERROR_INVALIDDATA
                    return new ExportFailure
                    {
                        Category = ExportFailureCategory.CorruptInput,
                        Stage = stage,
                        Attempt = attempt,
                        ProcessExitCode = processExitCode,
                        NativeErrorCode = -1094995529,
                        NativeErrorSource = nativeSource ?? "FFmpeg",
                        Summary = "The source video file is corrupt or has an invalid format.",
                        SpecificCause = "FFmpeg error -1094995529 (Invalid data found when processing input)",
                        DiagnosticLines = diagnostics,
                        EarlierAttempts = previousAttempts
                    };
                case -9: // MFX unsupported session
                    return new ExportFailure
                    {
                        Category = ExportFailureCategory.MissingEncoder,
                        Stage = stage,
                        Attempt = attempt,
                        ProcessExitCode = processExitCode,
                        NativeErrorCode = -9,
                        NativeErrorSource = nativeSource ?? "FFmpeg",
                        Summary = "The Intel hardware encoder is not supported or failed to initialize.",
                        SpecificCause = "MFX session error -9 (Unsupported implementation)",
                        DiagnosticLines = diagnostics,
                        EarlierAttempts = previousAttempts
                    };
            }
        }

        // 5. Tested text pattern fallback
        // Check both early significant lines and tail lines
        foreach (var (needle, category, summary) in KnownPatterns)
        {
            foreach (string line in diagnostics)
            {
                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return new ExportFailure
                    {
                        Category = category,
                        Stage = stage,
                        Attempt = attempt,
                        ProcessExitCode = processExitCode,
                        NativeErrorCode = nativeCode,
                        NativeErrorSource = nativeSource,
                        Summary = summary,
                        SpecificCause = line,
                        DiagnosticLines = diagnostics,
                        EarlierAttempts = previousAttempts
                    };
                }
            }
        }

        // 6. Honest Unknown when evidence is insufficient
        string unknownSummary = previousAttempts.Count > 0
            ? "The export failed after all encoder attempts were exhausted."
            : "The export failed due to an unexpected FFmpeg error.";

        return new ExportFailure
        {
            Category = ExportFailureCategory.Unknown,
            Stage = stage,
            Attempt = attempt,
            ProcessExitCode = processExitCode,
            NativeErrorCode = nativeCode,
            NativeErrorSource = nativeSource,
            Summary = unknownSummary,
            SpecificCause = null,
            DiagnosticLines = diagnostics,
            EarlierAttempts = previousAttempts
        };
    }

    public static ExportFailure ClassifyException(
        Exception ex,
        ExportStage stage,
        ExportAttemptIdentity attempt,
        IReadOnlyList<ExportFailure>? earlierAttempts = null)
    {
        return ClassifyStartupException(ex, stage, attempt, [ex.ToString()], earlierAttempts ?? Array.Empty<ExportFailure>());
    }

    private static ExportFailure ClassifyStartupException(
        Exception ex,
        ExportStage stage,
        ExportAttemptIdentity attempt,
        IReadOnlyList<string> diagnostics,
        IReadOnlyList<ExportFailure> earlierAttempts)
    {
        if (ex is Win32Exception win32)
        {
            int code = win32.NativeErrorCode;
            if (code == 5) // ERROR_ACCESS_DENIED
            {
                return new ExportFailure
                {
                    Category = ExportFailureCategory.AccessDenied,
                    Stage = stage,
                    Attempt = attempt,
                    NativeErrorCode = 5,
                    NativeErrorSource = "Win32",
                    Summary = "Windows blocked access when attempting to start FFmpeg.",
                    SpecificCause = win32.Message,
                    DiagnosticLines = diagnostics,
                    EarlierAttempts = earlierAttempts
                };
            }
            if (code == 2) // ERROR_FILE_NOT_FOUND
            {
                return new ExportFailure
                {
                    Category = ExportFailureCategory.StartupFailure,
                    Stage = stage,
                    Attempt = attempt,
                    NativeErrorCode = 2,
                    NativeErrorSource = "Win32",
                    Summary = "The FFmpeg executable was not found.",
                    SpecificCause = win32.Message,
                    DiagnosticLines = diagnostics,
                    EarlierAttempts = earlierAttempts
                };
            }
            return new ExportFailure
            {
                Category = ExportFailureCategory.StartupFailure,
                Stage = stage,
                Attempt = attempt,
                NativeErrorCode = code,
                NativeErrorSource = "Win32",
                Summary = $"Could not start the export process: {win32.Message}",
                SpecificCause = win32.Message,
                DiagnosticLines = diagnostics,
                EarlierAttempts = earlierAttempts
            };
        }

        if (ex is UnauthorizedAccessException)
        {
            return new ExportFailure
            {
                Category = ExportFailureCategory.AccessDenied,
                Stage = stage,
                Attempt = attempt,
                Summary = "Windows blocked access when starting the export process.",
                SpecificCause = ex.Message,
                DiagnosticLines = diagnostics,
                EarlierAttempts = earlierAttempts
            };
        }

        if (ex is FileNotFoundException)
        {
            return new ExportFailure
            {
                Category = ExportFailureCategory.StartupFailure,
                Stage = stage,
                Attempt = attempt,
                Summary = "The FFmpeg executable was not found.",
                SpecificCause = ex.Message,
                DiagnosticLines = diagnostics,
                EarlierAttempts = earlierAttempts
            };
        }

        return new ExportFailure
        {
            Category = ExportFailureCategory.StartupFailure,
            Stage = stage,
            Attempt = attempt,
            Summary = $"Could not start the export process: {ex.Message}",
            SpecificCause = ex.Message,
            DiagnosticLines = diagnostics,
            EarlierAttempts = earlierAttempts
        };
    }
}
