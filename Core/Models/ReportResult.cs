namespace AHNiRSE.Core.Models
{
    public class ReportResult
    {
        public string? ReportName { get; set; }
        public DateTime GeneratedAt { get; set; }
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? OutputFilePath { get; set; }
        public long? FileSizeBytes { get; set; }
        public List<Dictionary<string, object?>>? Data { get; set; }
    }
}

