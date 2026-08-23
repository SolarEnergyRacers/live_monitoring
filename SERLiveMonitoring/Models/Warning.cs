namespace SERLiveMonitoring.Models;

public enum WarningLevel
{
    Warning,
    Error
}

public record Warning(string Message, WarningLevel Level);
