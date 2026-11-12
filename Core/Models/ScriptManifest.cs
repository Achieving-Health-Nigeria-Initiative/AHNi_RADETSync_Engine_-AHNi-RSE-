namespace AHNiRSE.Core.Models
{
    public class ScriptManifest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public string? Author { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastModifiedAt { get; set; }
        public bool Enabled { get; set; } = true;
        public string? CronExpression { get; set; }
    }
}

