namespace AHNiRSE.Shared
{
    public static class LogMessages
    {
        // General
        public const string OperationStarted = "Operation started: {OperationName}";
        public const string OperationCompleted = "Operation completed successfully: {OperationName}";
        public const string OperationFailed = "Operation failed: {OperationName}. Error: {Error}";

        // Reports
        public const string ReportExecutionStarted = "Report execution started: {ReportName}";
        public const string ReportExecutionCompleted = "Report execution completed: {ReportName}";
        public const string ReportExecutionFailed = "Report execution failed: {ReportName}. Error: {Error}";

        // Authentication
        public const string AuthenticationStarted = "Authentication started";
        public const string AuthenticationSucceeded = "Authentication succeeded";
        public const string AuthenticationFailed = "Authentication failed: {Reason}";

        // Storage
        public const string FileUploaded = "File uploaded: {FileName}";
        public const string FileDownloaded = "File downloaded: {FileName}";
        public const string FileDeleted = "File deleted: {FileName}";
        public const string FileUploadFailed = "File upload failed: {FileName}. Error: {Error}";
        public const string FileDownloadFailed = "File download failed: {FileName}. Error: {Error}";

        // Database
        public const string DatabaseConnectionOpened = "Database connection opened";
        public const string DatabaseConnectionClosed = "Database connection closed";
        public const string DatabaseQueryExecuted = "Database query executed: {Query}";
        public const string DatabaseCommandExecuted = "Database command executed";

        // Export
        public const string ExportStarted = "Export started: {Format}";
        public const string ExportCompleted = "Export completed: {Format}";
        public const string ExportFailed = "Export failed: {Format}. Error: {Error}";
    }
}

