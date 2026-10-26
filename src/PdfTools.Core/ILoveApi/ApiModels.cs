namespace PdfTools.Core.ILoveApi;

public enum ApiErrorKind
{
    /// <summary>Key missing/rejected or token refused.</summary>
    Auth,
    /// <summary>No connectivity / timeout.</summary>
    Network,
    /// <summary>iLoveAPI 5xx.</summary>
    Server,
    /// <summary>Request rejected or processing failed (bad file, limits, credits...).</summary>
    Rejected,
    /// <summary>Input PDF is password protected.</summary>
    Password,
    /// <summary>Account is out of credits / over quota.</summary>
    Credits
}

public sealed class ApiException : Exception
{
    public ApiErrorKind Kind { get; }
    public int? StatusCode { get; }

    public ApiException(ApiErrorKind kind, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }
}

/// <summary>Result of /start: the server to talk to and the task id.</summary>
public sealed record ApiTask(string Server, string Task);

/// <summary>A file that has been uploaded to a task.</summary>
public sealed record UploadedFile(string ServerFilename, string Filename);

public sealed record ProcessResult(
    string Status,
    string? DownloadFilename,
    long FileSize,
    long OutputFileSize,
    int OutputFileNumber);
