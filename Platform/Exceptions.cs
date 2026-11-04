namespace AHNiRSE.Platform;

// ─────────────────────────────────────────────────────────────────────────────
// Typed exceptions thrown by the platform code (database, blob storage); the report cycle
// or background loop that called it catches and logs them.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Base for all AHNiRSE exceptions.</summary>
public class AppException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Storage read/write failure (disk permissions, Azure connection, etc.).</summary>
public class StorageException(string message, Exception? inner = null)
    : AppException(message, inner);

/// <summary>Database connection or query failure.</summary>
public class DatabaseException(string message, Exception? inner = null)
    : AppException(message, inner);
