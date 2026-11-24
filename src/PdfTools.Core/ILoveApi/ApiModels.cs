namespace PdfTools.Core.ILoveApi;

public enum ApiStage
{
    Auth,
    Start,
    Upload,
    Process,
    Download,
    Delete
}

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
    Credits,
    /// <summary>iLoveAPI returned HTTP 429 Too Many Requests after retries.</summary>
    RateLimited,
    /// <summary>Local circuit breaker paused calls due to recent service failures.</summary>
    CircuitOpen
}

public sealed class ApiException : Exception
{
    public ApiErrorKind Kind { get; }
    public int? StatusCode { get; }
    public ApiStage? Stage { get; }
    public string? Server { get; }
    public string? TaskId { get; }
    public TimeSpan? RetryAfter { get; }

    public ApiException(
        ApiErrorKind kind,
        string message,
        int? statusCode = null,
        Exception? inner = null,
        ApiStage? stage = null,
        string? server = null,
        string? taskId = null,
        TimeSpan? retryAfter = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        Stage = stage;
        Server = server;
        TaskId = taskId;
        RetryAfter = retryAfter;
    }

    internal ApiException WithContext(ApiStage stage, string? server, string? taskId)
        => Stage.HasValue && (Server != null || server == null) && (TaskId != null || taskId == null)
            ? this
            : new ApiException(Kind, Message, StatusCode, InnerException, Stage ?? stage, Server ?? server, TaskId ?? taskId, RetryAfter);
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
