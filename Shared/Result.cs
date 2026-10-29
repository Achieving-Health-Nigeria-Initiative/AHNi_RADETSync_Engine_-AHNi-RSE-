namespace AHNiRSE.Shared;

// ─────────────────────────────────────────────────────────────────────────────
// Result<T> — a simple discriminated union so callers never catch generic
// Exception to check for expected failures. Pattern:
//
//   var result = await orchestrator.RunAsync(ct);
//   if (!result.IsSuccess) logger.LogError(result.Error);
// ─────────────────────────────────────────────────────────────────────────────

public class Result<T>
{
    public bool IsSuccess { get; private init; }
    public T? Value { get; private init; }
    public string Error { get; private init; } = string.Empty;

    public static Result<T> Ok(T value) => new() { IsSuccess = true, Value = value };
    public static Result<T> Fail(string error) => new() { IsSuccess = false, Error = error };
}

// ─────────────────────────────────────────────────────────────────────────────
// Typed exceptions — thrown by infrastructure, caught and logged at Worker level.
// Never throw raw Exception in application code.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Base for all AHNiRSE exceptions.</summary>
public class AppException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>License key missing, expired, or rejected by the auth endpoint.</summary>
public class AuthException(string message, Exception? inner = null)
    : AppException(message, inner);

/// <summary>Storage read/write failure (disk permissions, Azure connection, etc.).</summary>
public class StorageException(string message, Exception? inner = null)
    : AppException(message, inner);

/// <summary>Database connection or query failure.</summary>
public class DatabaseException(string message, Exception? inner = null)
    : AppException(message, inner);
