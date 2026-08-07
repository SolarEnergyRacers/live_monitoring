namespace SER_Live_Monitoring.Models;

public enum DriverMessageSeverity
{
    Info,
    Warn
}
public record DriverMessage
{
    public Guid Id { get; init; }
    public DriverMessageSeverity Severity { get; init; }
    public string Text { get; init; } = "";
    public DateTime SentAt { get; init; }
    public DateTime? ConfirmedAt { get; init; }
}
