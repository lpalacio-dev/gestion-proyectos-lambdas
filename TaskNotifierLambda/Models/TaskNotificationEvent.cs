namespace TaskNotifierLambda.Models
{
    public class TaskNotificationEvent
    {
        public string EventType { get; set; } = string.Empty; // "TaskAssigned", "TaskStatusChanged", "TaskDueSoon"
        public string TaskId { get; set; } = string.Empty;
        public string TaskTitle { get; set; } = string.Empty;
        public string? TaskDescription { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public string AssignedUserEmail { get; set; } = string.Empty;
        public string AssignedUserName { get; set; } = string.Empty;
        public string? AssignerName { get; set; }
        public string? OldStatus { get; set; }
        public string? NewStatus { get; set; }
        public DateTime? DueDate { get; set; }
    }
}
