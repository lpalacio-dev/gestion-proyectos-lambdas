using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using TaskNotifierLambda.Models;

namespace TaskNotifierLambda.Services
{
    /// <summary>
    /// Servicio responsable de enviar emails via Amazon SES.
    /// Encapsula la construcción de los templates HTML y el envío.
    /// 
    /// Cada método corresponde a un tipo de evento de tu TaskService:
    ///   CreateTaskAsync  → SendTaskAssignedEmailAsync
    ///   UpdateTaskAsync  → SendTaskStatusChangedEmailAsync  (cuando Status cambia)
    ///   UpdateTaskAsync  → SendTaskReassignedEmailAsync     (cuando AssignedToId cambia)
    /// </summary>
    public class EmailService
    {
        private readonly IAmazonSimpleEmailService _sesClient;
        private readonly string _senderEmail;

        // URL base de tu app — se usa en los botones "Ver Tarea"
        // En producción será tu dominio real, en dev puede ser localhost
        private readonly string _appBaseUrl;

        public EmailService(IAmazonSimpleEmailService sesClient, string senderEmail, string appBaseUrl = "https://tuapp.com")
        {
            _sesClient = sesClient;
            _senderEmail = senderEmail;
            _appBaseUrl = appBaseUrl;
        }

        // ====================================================================
        // MÉTODOS PÚBLICOS: uno por tipo de EventType
        // ====================================================================

        /// <summary>
        /// Envía notificación cuando una tarea es asignada a un usuario.
        /// Disparado desde TaskService.CreateTaskAsync() o UpdateTaskAsync() cuando AssignedToId cambia.
        /// </summary>
        public async Task SendTaskAssignedEmailAsync(TaskNotificationEvent evt)
        {
            var subject = $"📋 Nueva tarea asignada: {evt.TaskTitle}";
            var html = BuildTaskAssignedHtml(evt);
            await SendEmailAsync(evt.AssignedUserEmail, subject, html);
        }

        /// <summary>
        /// Envía notificación cuando el estado de una tarea cambia.
        /// Disparado desde TaskService.UpdateTaskAsync() cuando Status cambia.
        /// </summary>
        public async Task SendTaskStatusChangedEmailAsync(TaskNotificationEvent evt)
        {
            var statusEmoji = evt.NewStatus switch
            {
                "InProgress" => "🔄",
                "Completed" => "✅",
                _ => "📌"
            };
            var subject = $"{statusEmoji} Estado actualizado: {evt.TaskTitle}";
            var html = BuildStatusChangedHtml(evt);
            await SendEmailAsync(evt.AssignedUserEmail, subject, html);
        }

        /// <summary>
        /// Envía alerta cuando una tarea está próxima a vencer.
        /// Puede ser disparado manualmente o por Lambda 3 (ProjectCleaner) en el futuro.
        /// </summary>
        public async Task SendTaskDueSoonEmailAsync(TaskNotificationEvent evt)
        {
            var subject = $"⚠️ Tarea próxima a vencer: {evt.TaskTitle}";
            var html = BuildTaskDueSoonHtml(evt);
            await SendEmailAsync(evt.AssignedUserEmail, subject, html);
        }

        // ====================================================================
        // MÉTODO PRIVADO: envío real via SES
        // ====================================================================

        private async Task SendEmailAsync(string recipientEmail, string subject, string htmlBody)
        {
            var sendRequest = new SendEmailRequest
            {
                Source = _senderEmail,
                Destination = new Destination
                {
                    ToAddresses = new List<string> { recipientEmail }
                },
                Message = new Message
                {
                    Subject = new Content(subject),
                    Body = new Body
                    {
                        Html = new Content
                        {
                            Charset = "UTF-8",
                            Data = htmlBody
                        }
                    }
                }
            };

            await _sesClient.SendEmailAsync(sendRequest);
        }

        // ====================================================================
        // TEMPLATES HTML — uno por tipo de notificación
        // ====================================================================

        /// <summary>Email verde: nueva tarea asignada</summary>
        private string BuildTaskAssignedHtml(TaskNotificationEvent evt)
        {
            var dueDateHtml = evt.DueDate.HasValue
                ? $"<p><strong>📅 Fecha límite:</strong> {evt.DueDate.Value:dd/MM/yyyy}</p>"
                : string.Empty;

            var descriptionHtml = !string.IsNullOrWhiteSpace(evt.TaskDescription)
                ? $"<p style='color:#555;'>{evt.TaskDescription}</p>"
                : string.Empty;

            var assignerText = !string.IsNullOrWhiteSpace(evt.AssignerName)
                ? $"<strong>{evt.AssignerName}</strong> te ha asignado"
                : "Se te ha asignado";

            return $@"<!DOCTYPE html>
<html lang='es'>
<head>
  <meta charset='UTF-8'>
  <style>
    body {{ margin:0; padding:0; background:#f4f4f4; font-family:Arial,sans-serif; color:#333; }}
    .wrapper {{ max-width:600px; margin:30px auto; background:#fff; border-radius:8px; overflow:hidden; box-shadow:0 2px 8px rgba(0,0,0,.1); }}
    .header {{ background:#4CAF50; padding:24px 30px; }}
    .header h1 {{ margin:0; color:#fff; font-size:20px; }}
    .header p  {{ margin:4px 0 0; color:#e8f5e9; font-size:13px; }}
    .body   {{ padding:24px 30px; }}
    .task-card {{ background:#f9f9f9; border-left:4px solid #4CAF50; padding:16px 20px; border-radius:0 6px 6px 0; margin:16px 0; }}
    .task-card h2 {{ margin:0 0 8px; font-size:17px; color:#222; }}
    .btn {{ display:inline-block; margin-top:20px; padding:12px 28px; background:#4CAF50; color:#fff; text-decoration:none; border-radius:5px; font-weight:bold; font-size:14px; }}
    .footer {{ background:#f0f0f0; padding:14px 30px; font-size:11px; color:#888; text-align:center; }}
  </style>
</head>
<body>
  <div class='wrapper'>
    <div class='header'>
      <h1>📋 Nueva Tarea Asignada</h1>
      <p>Proyecto: {evt.ProjectName}</p>
    </div>
    <div class='body'>
      <p>Hola <strong>{evt.AssignedUserName}</strong>,</p>
      <p>{assignerText} una nueva tarea en el proyecto <strong>{evt.ProjectName}</strong>.</p>

      <div class='task-card'>
        <h2>{evt.TaskTitle}</h2>
        {descriptionHtml}
        {dueDateHtml}
      </div>

      <p>Accede a la plataforma para ver todos los detalles y comenzar a trabajar.</p>
      <a href='{_appBaseUrl}/tasks/{evt.TaskId}' class='btn'>Ver Tarea →</a>
    </div>
    <div class='footer'>
      Este es un mensaje automático del Sistema de Gestión de Proyectos. No respondas a este email.
    </div>
  </div>
</body>
</html>";
        }

        /// <summary>Email azul: cambio de estado</summary>
        private string BuildStatusChangedHtml(TaskNotificationEvent evt)
        {
            // Traducción amigable de los estados del enum TaskStatus de tu modelo
            static string TranslateStatus(string? s) => s switch
            {
                "Pending" => "Pendiente",
                "InProgress" => "En Progreso",
                "Completed" => "Completada",
                null => "—",
                _ => s
            };

            var changerText = !string.IsNullOrWhiteSpace(evt.AssignerName)
                ? $"<strong>{evt.AssignerName}</strong> actualizó"
                : "Se actualizó";

            return $@"<!DOCTYPE html>
<html lang='es'>
<head>
  <meta charset='UTF-8'>
  <style>
    body {{ margin:0; padding:0; background:#f4f4f4; font-family:Arial,sans-serif; color:#333; }}
    .wrapper {{ max-width:600px; margin:30px auto; background:#fff; border-radius:8px; overflow:hidden; box-shadow:0 2px 8px rgba(0,0,0,.1); }}
    .header {{ background:#1565C0; padding:24px 30px; }}
    .header h1 {{ margin:0; color:#fff; font-size:20px; }}
    .header p  {{ margin:4px 0 0; color:#bbdefb; font-size:13px; }}
    .body   {{ padding:24px 30px; }}
    .status-row {{ display:flex; align-items:center; gap:12px; margin:16px 0; }}
    .chip {{ display:inline-block; padding:6px 14px; border-radius:20px; font-size:13px; font-weight:bold; }}
    .chip-old {{ background:#e0e0e0; color:#555; }}
    .chip-new {{ background:#1565C0; color:#fff; }}
    .arrow {{ font-size:20px; color:#888; }}
    .btn {{ display:inline-block; margin-top:20px; padding:12px 28px; background:#1565C0; color:#fff; text-decoration:none; border-radius:5px; font-weight:bold; font-size:14px; }}
    .footer {{ background:#f0f0f0; padding:14px 30px; font-size:11px; color:#888; text-align:center; }}
  </style>
</head>
<body>
  <div class='wrapper'>
    <div class='header'>
      <h1>🔄 Estado de Tarea Actualizado</h1>
      <p>Proyecto: {evt.ProjectName}</p>
    </div>
    <div class='body'>
      <p>Hola <strong>{evt.AssignedUserName}</strong>,</p>
      <p>{changerText} el estado de la tarea <strong>{evt.TaskTitle}</strong>.</p>

      <div class='status-row'>
        <span class='chip chip-old'>{TranslateStatus(evt.OldStatus)}</span>
        <span class='arrow'>→</span>
        <span class='chip chip-new'>{TranslateStatus(evt.NewStatus)}</span>
      </div>

      <a href='{_appBaseUrl}/tasks/{evt.TaskId}' class='btn'>Ver Tarea →</a>
    </div>
    <div class='footer'>
      Este es un mensaje automático del Sistema de Gestión de Proyectos. No respondas a este email.
    </div>
  </div>
</body>
</html>";
        }

        /// <summary>Email naranja: tarea próxima a vencer</summary>
        private string BuildTaskDueSoonHtml(TaskNotificationEvent evt)
        {
            var daysLeft = evt.DueDate.HasValue
                ? Math.Max(0, (evt.DueDate.Value.Date - DateTime.UtcNow.Date).Days)
                : 0;

            var urgencyMsg = daysLeft == 0
                ? "¡La tarea vence <strong>HOY</strong>!"
                : daysLeft == 1
                    ? "La tarea vence <strong>mañana</strong>."
                    : $"La tarea vence en <strong>{daysLeft} días</strong>.";

            return $@"<!DOCTYPE html>
<html lang='es'>
<head>
  <meta charset='UTF-8'>
  <style>
    body {{ margin:0; padding:0; background:#f4f4f4; font-family:Arial,sans-serif; color:#333; }}
    .wrapper {{ max-width:600px; margin:30px auto; background:#fff; border-radius:8px; overflow:hidden; box-shadow:0 2px 8px rgba(0,0,0,.1); }}
    .header {{ background:#E65100; padding:24px 30px; }}
    .header h1 {{ margin:0; color:#fff; font-size:20px; }}
    .header p  {{ margin:4px 0 0; color:#ffe0b2; font-size:13px; }}
    .body   {{ padding:24px 30px; }}
    .warning-box {{ background:#fff3e0; border-left:4px solid #E65100; padding:16px 20px; border-radius:0 6px 6px 0; margin:16px 0; }}
    .warning-box h2 {{ margin:0 0 8px; font-size:17px; }}
    .btn {{ display:inline-block; margin-top:20px; padding:12px 28px; background:#E65100; color:#fff; text-decoration:none; border-radius:5px; font-weight:bold; font-size:14px; }}
    .footer {{ background:#f0f0f0; padding:14px 30px; font-size:11px; color:#888; text-align:center; }}
  </style>
</head>
<body>
  <div class='wrapper'>
    <div class='header'>
      <h1>⚠️ Tarea Próxima a Vencer</h1>
      <p>Proyecto: {evt.ProjectName}</p>
    </div>
    <div class='body'>
      <p>Hola <strong>{evt.AssignedUserName}</strong>,</p>
      <p>{urgencyMsg}</p>

      <div class='warning-box'>
        <h2>{evt.TaskTitle}</h2>
        <p><strong>📅 Fecha límite:</strong> {evt.DueDate?.ToString("dd/MM/yyyy") ?? "Sin fecha"}</p>
        <p><strong>📁 Proyecto:</strong> {evt.ProjectName}</p>
      </div>

      <p>Revisa el progreso y actualiza el estado si es necesario.</p>
      <a href='{_appBaseUrl}/tasks/{evt.TaskId}' class='btn'>Ver Tarea →</a>
    </div>
    <div class='footer'>
      Este es un mensaje automático del Sistema de Gestión de Proyectos. No respondas a este email.
    </div>
  </div>
</body>
</html>";
        }
    }
}