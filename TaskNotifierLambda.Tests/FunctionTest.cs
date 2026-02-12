using Amazon.Lambda.TestUtilities;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Moq;
using TaskNotifierLambda;
using TaskNotifierLambda.Models;
using TaskNotifierLambda.Services;
using Xunit;

namespace TaskNotifierLambda.Tests
{
    // ====================================================================
    // TESTS DE FUNCTION (handler principal)
    // ====================================================================

    public class FunctionTests
    {
        // Helper: evento base con todos los campos válidos
        private static TaskNotificationEvent BuildBaseEvent(string eventType = "TaskAssigned") => new()
        {
            EventType = eventType,
            TaskId = Guid.NewGuid().ToString(),
            TaskTitle = "Implementar login",
            TaskDescription = "Pantalla de login con validaciones",
            ProjectName = "Sistema de Gestión",
            AssignedUserEmail = "juan@example.com",
            AssignedUserName = "Juan Pérez",
            AssignerName = "María López",
            OldStatus = "Pending",
            NewStatus = "InProgress",
            DueDate = DateTime.UtcNow.AddDays(3)
        };

        // Helper: EmailService con SES mockeado que siempre tiene éxito
        private static (EmailService emailService, Mock<IAmazonSimpleEmailService> sesMock) BuildEmailServiceMock()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(s => s.SendEmailAsync(
                    It.IsAny<SendEmailRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SendEmailResponse());

            var emailService = new EmailService(sesMock.Object, "noreply@test.com");
            return (emailService, sesMock);
        }

        [Fact]
        public async Task FunctionHandler_TaskAssigned_EnviaUnEmail()
        {
            var (emailService, sesMock) = BuildEmailServiceMock();
            var function = new Function(emailService);
            var context = new TestLambdaContext();
            var evt = BuildBaseEvent("TaskAssigned");

            await function.FunctionHandler(evt, context);

            sesMock.Verify(
                s => s.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "TaskAssigned debe enviar exactamente 1 email"
            );
        }

        [Fact]
        public async Task FunctionHandler_TaskStatusChanged_EstadoDistinto_EnviaEmail()
        {
            var (emailService, sesMock) = BuildEmailServiceMock();
            var function = new Function(emailService);
            var context = new TestLambdaContext();
            var evt = BuildBaseEvent("TaskStatusChanged");
            evt.OldStatus = "Pending";
            evt.NewStatus = "Completed";

            await function.FunctionHandler(evt, context);

            sesMock.Verify(
                s => s.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Once
            );
        }

        [Fact]
        public async Task FunctionHandler_TaskStatusChanged_MismoEstado_NoEnviaEmail()
        {
            // Si OldStatus == NewStatus, la Lambda debe ignorar silenciosamente
            var (emailService, sesMock) = BuildEmailServiceMock();
            var function = new Function(emailService);
            var context = new TestLambdaContext();
            var evt = BuildBaseEvent("TaskStatusChanged");
            evt.OldStatus = "Pending";
            evt.NewStatus = "Pending"; // Sin cambio real

            await function.FunctionHandler(evt, context);

            sesMock.Verify(
                s => s.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "Si el estado no cambió, no debe enviar email"
            );
        }

        [Fact]
        public async Task FunctionHandler_TaskDueSoon_ConFecha_EnviaEmail()
        {
            var (emailService, sesMock) = BuildEmailServiceMock();
            var function = new Function(emailService);
            var context = new TestLambdaContext();
            var evt = BuildBaseEvent("TaskDueSoon");
            evt.DueDate = DateTime.UtcNow.AddDays(1);

            await function.FunctionHandler(evt, context);

            sesMock.Verify(
                s => s.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Once
            );
        }

        [Fact]
        public async Task FunctionHandler_TaskDueSoon_SinFecha_NoEnviaEmail()
        {
            var (emailService, sesMock) = BuildEmailServiceMock();
            var function = new Function(emailService);
            var context = new TestLambdaContext();
            var evt = BuildBaseEvent("TaskDueSoon");
            evt.DueDate = null; // Falta DueDate

            await function.FunctionHandler(evt, context);

            sesMock.Verify(
                s => s.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "TaskDueSoon sin DueDate no debe enviar email"
            );
        }

        [Fact]
        public async Task FunctionHandler_EmailVacio_OmiteSinExcepcion()
        {
            var (emailService, sesMock) = BuildEmailServiceMock();
            var function = new Function(emailService);
            var context = new TestLambdaContext();
            var evt = BuildBaseEvent("TaskAssigned");
            evt.AssignedUserEmail = ""; // Sin destinatario

            // No debe lanzar excepción
            await function.FunctionHandler(evt, context);

            sesMock.Verify(
                s => s.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Never
            );
        }

        [Fact]
        public async Task FunctionHandler_EventTypeDesconocido_NoLanzaExcepcion()
        {
            var (emailService, sesMock) = BuildEmailServiceMock();
            var function = new Function(emailService);
            var context = new TestLambdaContext();
            var evt = BuildBaseEvent("EventoQueNoExiste");

            // EventType desconocido debe ser ignorado sin romper
            var ex = await Record.ExceptionAsync(() => function.FunctionHandler(evt, context));
            Assert.Null(ex);

            sesMock.Verify(
                s => s.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Never
            );
        }

        [Fact]
        public async Task FunctionHandler_SESFalla_RelanzaExcepcion()
        {
            // Si SES falla, la Lambda debe relanzar la excepción para que
            // CloudWatch Alarms lo detecte y aparezca en métricas de errores
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(s => s.SendEmailAsync(
                    It.IsAny<SendEmailRequest>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("SES: cuenta no verificada"));

            var emailService = new EmailService(sesMock.Object, "noreply@test.com");
            var function = new Function(emailService);
            var context = new TestLambdaContext();
            var evt = BuildBaseEvent("TaskAssigned");

            await Assert.ThrowsAsync<Exception>(() => function.FunctionHandler(evt, context));
        }
    }

    // ====================================================================
    // TESTS DE EMAIL SERVICE (contenido de los emails)
    // ====================================================================

    public class EmailServiceTests
    {
        private static (EmailService service, Mock<IAmazonSimpleEmailService> mock) Build()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(s => s.SendEmailAsync(
                    It.IsAny<SendEmailRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SendEmailResponse());

            return (new EmailService(sesMock.Object, "noreply@test.com", "https://mi-app.com"), sesMock);
        }

        [Fact]
        public async Task SendTaskAssignedEmail_UsaDestinatarioCorrecto()
        {
            var (service, mock) = Build();
            var evt = new TaskNotificationEvent
            {
                EventType = "TaskAssigned",
                TaskId = "abc-123",
                TaskTitle = "Revisar PRs",
                ProjectName = "Proyecto Alpha",
                AssignedUserEmail = "carlos@empresa.com",
                AssignedUserName = "Carlos García",
                AssignerName = "Laura Martínez"
            };

            await service.SendTaskAssignedEmailAsync(evt);

            mock.Verify(s => s.SendEmailAsync(
                It.Is<SendEmailRequest>(r =>
                    r.Destination.ToAddresses.Contains("carlos@empresa.com") &&
                    r.Source == "noreply@test.com"),
                It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task SendTaskAssignedEmail_AsuntoContieneTituloTarea()
        {
            var (service, mock) = Build();
            var evt = new TaskNotificationEvent
            {
                EventType = "TaskAssigned",
                TaskId = "abc-123",
                TaskTitle = "Diseñar mockups",
                ProjectName = "App Móvil",
                AssignedUserEmail = "dev@empresa.com",
                AssignedUserName = "Dev"
            };

            await service.SendTaskAssignedEmailAsync(evt);

            mock.Verify(s => s.SendEmailAsync(
                It.Is<SendEmailRequest>(r =>
                    r.Message.Subject.Data.Contains("Diseñar mockups")),
                It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task SendStatusChangedEmail_CuerpoContieneEstadosTraducidos()
        {
            SendEmailRequest? capturedRequest = null;
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(s => s.SendEmailAsync(
                    It.IsAny<SendEmailRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new SendEmailResponse());

            var service = new EmailService(sesMock.Object, "noreply@test.com");
            var evt = new TaskNotificationEvent
            {
                EventType = "TaskStatusChanged",
                TaskId = "xyz-456",
                TaskTitle = "Configurar CI/CD",
                ProjectName = "DevOps",
                AssignedUserEmail = "ops@empresa.com",
                AssignedUserName = "Ops Team",
                OldStatus = "Pending",
                NewStatus = "InProgress"
            };

            await service.SendTaskStatusChangedEmailAsync(evt);

            Assert.NotNull(capturedRequest);
            var html = capturedRequest!.Message.Body.Html.Data;

            // El HTML debe contener las traducciones amigables de los estados
            Assert.Contains("Pendiente", html);
            Assert.Contains("En Progreso", html);
        }

        [Fact]
        public async Task SendTaskDueSoonEmail_CuerpoContieneNombreUsuario()
        {
            SendEmailRequest? capturedRequest = null;
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(s => s.SendEmailAsync(
                    It.IsAny<SendEmailRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new SendEmailResponse());

            var service = new EmailService(sesMock.Object, "noreply@test.com");
            var evt = new TaskNotificationEvent
            {
                EventType = "TaskDueSoon",
                TaskId = "due-789",
                TaskTitle = "Entregar informe",
                ProjectName = "Auditoría",
                AssignedUserEmail = "auditor@empresa.com",
                AssignedUserName = "Sofía Ramírez",
                DueDate = DateTime.UtcNow.AddDays(1)
            };

            await service.SendTaskDueSoonEmailAsync(evt);

            Assert.NotNull(capturedRequest);
            var html = capturedRequest!.Message.Body.Html.Data;
            Assert.Contains("Sofía Ramírez", html);
            Assert.Contains("mañana", html); // 1 día → "vence mañana"
        }

        [Fact]
        public async Task SendTaskAssignedEmail_ContieneEnlaceATarea()
        {
            SendEmailRequest? capturedRequest = null;
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(s => s.SendEmailAsync(
                    It.IsAny<SendEmailRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new SendEmailResponse());

            var service = new EmailService(sesMock.Object, "noreply@test.com", "https://mi-app.com");
            var taskId = Guid.NewGuid().ToString();
            var evt = new TaskNotificationEvent
            {
                EventType = "TaskAssigned",
                TaskId = taskId,
                TaskTitle = "Tarea con link",
                ProjectName = "Proyecto",
                AssignedUserEmail = "user@empresa.com",
                AssignedUserName = "Usuario"
            };

            await service.SendTaskAssignedEmailAsync(evt);

            Assert.NotNull(capturedRequest);
            var html = capturedRequest!.Message.Body.Html.Data;
            Assert.Contains($"https://mi-app.com/tasks/{taskId}", html);
        }
    }
}