namespace SER_Live_Monitoring.Models;

public enum DriverMessageSeverity
{
    Info,
    Warn
}

public class DriverMessage
{
    public Guid Id { get; set; }
    public DriverMessageSeverity Severity { get; set; }
    public string Text { get; set; } = "";
    public DateTime SentAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}
