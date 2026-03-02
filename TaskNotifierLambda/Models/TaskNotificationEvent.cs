namespace TaskNotifierLambda.Models;

/// <summary>
/// Modelo del payload que publica el backend en SNS.
/// Debe coincidir exactamente con el objeto anónimo en TaskService.cs.
/// </summary>
public class TaskNotificationEvent
{
    public string EventType { get; set; } = "";  // "TaskAssigned" | "TaskStatusChanged"
    public string TaskId { get; set; } = "";
    public string TaskTitle { get; set; } = "";
    public string? TaskDescription { get; set; }
    public string ProjectName { get; set; } = "";
    public string AssignedUserEmail { get; set; } = "";
    public string AssignedUserName { get; set; } = "";
    public string? AssignerName { get; set; }
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }
    public DateTime? DueDate { get; set; }
}