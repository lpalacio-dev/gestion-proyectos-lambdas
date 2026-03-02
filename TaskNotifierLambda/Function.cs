using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using TaskNotifierLambda.Models;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TaskNotifierLambda;

public class Function
{
    private readonly IAmazonSimpleEmailService _sesClient;

    // IMPORTANTE: leer de variable de entorno, no hardcodear
    private readonly string _fromEmail = Environment.GetEnvironmentVariable("SES_FROM_EMAIL")
                                         ?? throw new Exception("SES_FROM_EMAIL no configurado");

    public Function()
    {
        _sesClient = new AmazonSimpleEmailServiceClient();
    }

    // Constructor para testing
    public Function(IAmazonSimpleEmailService sesClient)
    {
        _sesClient = sesClient;
    }

    /// <summary>
    /// Handler principal. SQS puede enviar múltiples mensajes a la vez (batch).
    /// Procesamos cada uno individualmente para que los fallos no afecten al batch completo.
    /// </summary>
    public async Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        // SQSBatchResponse permite reportar qué mensajes fallaron individualmente
        // Los mensajes fallidos se reencolan y eventualmente van a la DLQ
        var batchResponse = new SQSBatchResponse
        {
            BatchItemFailures = new List<SQSBatchResponse.BatchItemFailure>()
        };

        foreach (var message in sqsEvent.Records)
        {
            try
            {
                context.Logger.LogInformation(
                    $"[TaskNotifier] Procesando mensaje {message.MessageId}");

                // Los mensajes de SNS vienen envueltos en un sobre JSON
                var snsWrapper = JsonSerializer.Deserialize<SnsMessageWrapper>(message.Body)!;
                var notification = JsonSerializer.Deserialize<TaskNotificationEvent>(snsWrapper.Message)!;

                await SendEmailAsync(notification, context);

                context.Logger.LogInformation(
                    $"[TaskNotifier] Email enviado para tarea {notification.TaskId} → {notification.AssignedUserEmail}");
            }
            catch (Exception ex)
            {
                // Marcar este mensaje como fallido — SQS lo reintentará
                context.Logger.LogError(
                    $"[TaskNotifier] Error procesando mensaje {message.MessageId}: {ex.Message}");

                batchResponse.BatchItemFailures.Add(new SQSBatchResponse.BatchItemFailure
                {
                    ItemIdentifier = message.MessageId
                });
            }
        }

        return batchResponse;
    }

    private async Task SendEmailAsync(TaskNotificationEvent notification, ILambdaContext context)
    {
        var subject = notification.EventType switch
        {
            "TaskAssigned" => $"📋 Nueva tarea asignada: {notification.TaskTitle}",
            "TaskStatusChanged" => $"🔄 Actualización de tarea: {notification.TaskTitle}",
            _ => $"Notificación: {notification.TaskTitle}"
        };

        var htmlBody = BuildEmailBody(notification);

        var request = new SendEmailRequest
        {
            Source = _fromEmail,
            Destination = new Destination
            {
                ToAddresses = new List<string> { notification.AssignedUserEmail }
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

        await _sesClient.SendEmailAsync(request);
    }

    private string BuildEmailBody(TaskNotificationEvent n)
    {
        var dueDateStr = n.DueDate.HasValue
            ? n.DueDate.Value.ToString("dd/MM/yyyy")
            : "Sin fecha límite";

        var statusSection = n.EventType == "TaskStatusChanged"
            ? $"<p><strong>Estado:</strong> {n.OldStatus} → <strong>{n.NewStatus}</strong></p>"
            : $"<p><strong>Estado inicial:</strong> {n.NewStatus}</p>";

        return $"""
            <!DOCTYPE html>
            <html>
            <body style="font-family: Arial, sans-serif; max-width: 600px; margin: auto; padding: 20px;">
              <h2 style="color: #2563EB;">Sistema de Gestión de Proyectos</h2>
              <hr/>
              <h3>{(n.EventType == "TaskAssigned" ? "Se te ha asignado una tarea" : "Una tarea fue actualizada")}</h3>
              <p>Hola <strong>{n.AssignedUserName}</strong>,</p>
              <p>{(n.EventType == "TaskAssigned"
                    ? $"{n.AssignerName ?? "Un administrador"} te asignó la siguiente tarea:"
                    : $"La tarea fue actualizada en el proyecto <strong>{n.ProjectName}</strong>:")}</p>
              <div style="background:#F3F4F6; padding:16px; border-radius:8px; margin:16px 0;">
                <p><strong>Tarea:</strong> {n.TaskTitle}</p>
                <p><strong>Proyecto:</strong> {n.ProjectName}</p>
                {statusSection}
                <p><strong>Fecha límite:</strong> {dueDateStr}</p>
                {(n.TaskDescription != null ? $"<p><strong>Descripción:</strong> {n.TaskDescription}</p>" : "")}
              </div>
              <p style="color:#6B7280; font-size:12px;">Este es un correo automático, no responder.</p>
            </body>
            </html>
            """;
    }
}

/// <summary>
/// SNS envuelve el mensaje original en este sobre JSON.
/// El campo "Message" contiene el payload real que publicó tu backend.
/// </summary>
public class SnsMessageWrapper
{
    public string Type { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string Message { get; set; } = "";  // ← Aquí está tu TaskNotificationEvent serializado
    public string Subject { get; set; } = "";
    public string Timestamp { get; set; } = "";
}