using Amazon.Lambda.Core;
using Amazon.SimpleEmail;
using TaskNotifierLambda.Models;
using TaskNotifierLambda.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TaskNotifierLambda;

/// <summary>
/// Handler de la Lambda de notificaciones por email.
/// 
/// FLUJO COMPLETO:
///   1. Tu TaskService (backend .NET) detecta un evento relevante
///      (tarea asignada, estado cambiado, etc.)
///   2. TaskService serializa un TaskNotificationEvent como JSON
///   3. TaskService invoca esta Lambda de forma ASÍNCRONA via IAmazonLambda.InvokeAsync
///      (InvocationType.Event = fire-and-forget, no bloquea el request del usuario)
///   4. Lambda deserializa el evento y llama al EmailService correcto
///   5. EmailService envía el email via Amazon SES
/// 
/// VARIABLES DE ENTORNO requeridas en AWS Lambda Console:
///   SENDER_EMAIL  → email verificado en SES (ej: noreply@tudominio.com)
///   APP_BASE_URL  → URL de tu frontend (ej: https://tuapp.com) — para links en los emails
/// </summary>
public class Function
{
    private readonly EmailService _emailService;

    // Constructor sin parámetros: Lambda lo usa en producción
    public Function()
    {
        var sesClient = new AmazonSimpleEmailServiceClient();

        var senderEmail = Environment.GetEnvironmentVariable("SENDER_EMAIL")
            ?? throw new InvalidOperationException(
                "Variable de entorno SENDER_EMAIL no configurada. " +
                "Configúrala en AWS Lambda Console → Configuration → Environment variables.");

        var appBaseUrl = Environment.GetEnvironmentVariable("APP_BASE_URL")
            ?? "https://tuapp.com";

        _emailService = new EmailService(sesClient, senderEmail, appBaseUrl);
    }

    // Constructor para tests: permite inyectar mocks
    public Function(EmailService emailService)
    {
        _emailService = emailService;
    }

    /// <summary>
    /// Entry point de la Lambda.
    /// Recibe el TaskNotificationEvent que serialize tu TaskService y enruta al método correcto.
    /// </summary>
    public async Task FunctionHandler(TaskNotificationEvent evt, ILambdaContext context)
    {
        context.Logger.LogInformation(
            $"[TaskNotifier] Evento recibido: Type={evt.EventType} | " +
            $"Task={evt.TaskId} | To={evt.AssignedUserEmail}");

        // Validaciones básicas antes de intentar enviar
        if (string.IsNullOrWhiteSpace(evt.AssignedUserEmail))
        {
            context.Logger.LogWarning("[TaskNotifier] AssignedUserEmail vacío — se omite el envío.");
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.EventType))
        {
            context.Logger.LogWarning("[TaskNotifier] EventType vacío — se omite el envío.");
            return;
        }

        try
        {
            switch (evt.EventType)
            {
                case "TaskAssigned":
                    await _emailService.SendTaskAssignedEmailAsync(evt);
                    context.Logger.LogInformation(
                        $"[TaskNotifier] ✅ Email 'TaskAssigned' enviado a {evt.AssignedUserEmail}");
                    break;

                case "TaskStatusChanged":
                    // Solo enviar si realmente cambió el estado (defensa contra datos incorrectos)
                    if (evt.OldStatus == evt.NewStatus)
                    {
                        context.Logger.LogInformation(
                            "[TaskNotifier] Estado sin cambio real — se omite el envío.");
                        return;
                    }
                    await _emailService.SendTaskStatusChangedEmailAsync(evt);
                    context.Logger.LogInformation(
                        $"[TaskNotifier] ✅ Email 'TaskStatusChanged' enviado a {evt.AssignedUserEmail} " +
                        $"({evt.OldStatus} → {evt.NewStatus})");
                    break;

                case "TaskDueSoon":
                    if (!evt.DueDate.HasValue)
                    {
                        context.Logger.LogWarning(
                            "[TaskNotifier] TaskDueSoon sin DueDate — se omite el envío.");
                        return;
                    }
                    await _emailService.SendTaskDueSoonEmailAsync(evt);
                    context.Logger.LogInformation(
                        $"[TaskNotifier] ✅ Email 'TaskDueSoon' enviado a {evt.AssignedUserEmail}");
                    break;

                default:
                    // Loggear sin lanzar excepción: un EventType desconocido no debe hacer fallar la Lambda
                    context.Logger.LogWarning(
                        $"[TaskNotifier] EventType desconocido: '{evt.EventType}'. " +
                        "Valores válidos: TaskAssigned, TaskStatusChanged, TaskDueSoon.");
                    break;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"[TaskNotifier] ❌ Error enviando email para evento '{evt.EventType}' " +
                $"(tarea {evt.TaskId}): {ex.GetType().Name}: {ex.Message}");

            // Re-lanzar para que CloudWatch Alarms detecte el fallo
            throw;
        }
    }
}