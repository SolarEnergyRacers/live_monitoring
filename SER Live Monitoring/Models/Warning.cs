namespace SER_Live_Monitoring.Models;

public enum WarningLevel
{
    Warning,
    Error
}

public record Warning(string Message, WarningLevel Level);
