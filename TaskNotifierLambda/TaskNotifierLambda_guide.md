# 📧 TaskNotifier Lambda

Lambda de .NET 8 que envía emails automáticos via Amazon SES cuando ocurren eventos en las tareas de tu sistema.

## Qué hace

| EventType | Cuándo se dispara | Email enviado |
|-----------|------------------|---------------|
| `TaskAssigned` | Tarea creada con asignado, o reasignada | Email verde al asignado |
| `TaskStatusChanged` | Estado cambia en tarea con asignado | Email azul al asignado |
| `TaskDueSoon` | (Futuro: Lambda 3 lo dispara) | Email naranja al asignado |

## Flujo completo

```
Usuario → TaskController → TaskService.CreateTaskAsync()
                                  ↓ tarea guardada en BD
                         IAmazonLambda.InvokeAsync (fire-and-forget, 202)
                                  ↓
                         TaskNotifierLambda
                                  ↓
                         Amazon SES → Email al asignado
```

---

## Setup paso a paso

### 1. Configurar Amazon SES

**IMPORTANTE**: En modo Sandbox, solo puedes enviar a emails verificados.

```bash
# Verificar email remitente
aws ses verify-email-identity --email-address noreply@tudominio.com

# Verificar email de prueba (solo en Sandbox)
aws ses verify-email-identity --email-address tu-email@ejemplo.com

# Ver estado de verificación
aws ses get-identity-verification-attributes \
  --identities noreply@tudominio.com
```

Para producción: AWS Console → SES → Account dashboard → **Request production access**.

### 2. Agregar permisos SES al IAM Role de Lambda

Si ya tienes el `LambdaExecutionRole` de la Lambda 1, agrega esta política:

```bash
cat > ses-policy.json << 'EOF'
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": ["ses:SendEmail", "ses:SendRawEmail"],
    "Resource": "*"
  }]
}
EOF

aws iam create-policy \
  --policy-name LambdaSESPolicy \
  --policy-document file://ses-policy.json

aws iam attach-role-policy \
  --role-name LambdaExecutionRole \
  --policy-arn arn:aws:iam::ACCOUNT_ID:policy/LambdaSESPolicy
```

### 3. Agregar permiso Lambda al IAM Role del ECS Task (backend)

Tu backend necesita permiso para invocar esta Lambda:

```bash
cat > invoke-lambda-policy.json << 'EOF'
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": "lambda:InvokeFunction",
    "Resource": "arn:aws:lambda:us-east-1:ACCOUNT_ID:function:TaskNotifierLambda"
  }]
}
EOF

aws iam create-policy \
  --policy-name ECSInvokeLambdaPolicy \
  --policy-document file://invoke-lambda-policy.json

# Adjuntar al rol del ECS Task (el que usa tu contenedor de backend)
aws iam attach-role-policy \
  --role-name TuECSTaskRole \
  --policy-arn arn:aws:iam::ACCOUNT_ID:policy/ECSInvokeLambdaPolicy
```

### 4. Deploy de la Lambda

```bash
cd src/TaskNotifierLambda

dotnet lambda deploy-function TaskNotifierLambda \
  --function-role <LAMBDA_EXECUTION_ROLE_ARN> \
  --region us-east-1 \
  --environment-variables "SENDER_EMAIL=noreply@tudominio.com;APP_BASE_URL=https://tuapp.com"
```

### 5. Cambios en tu backend

**En `Program.cs`** — agregar una línea:
```csharp
builder.Services.AddAWSService<IAmazonS3>();
builder.Services.AddAWSService<IAmazonLambda>(); // ← AGREGAR
```

**También instalar el paquete** en tu proyecto .NET:
```bash
dotnet add package AWSSDK.Lambda
```

**Reemplazar `TaskService.cs`** con la versión en `backend-changes/TaskService.cs`.
El constructor ahora recibe `IAmazonLambda`, `UserManager<ApplicationUser>` y `ApplicationDbContext`.

---

## Testing

### Correr tests localmente

```bash
cd test/TaskNotifierLambda.Tests
dotnet test
```

### Test directo a Lambda en AWS

```bash
cat > test-assigned.json << 'EOF'
{
  "EventType": "TaskAssigned",
  "TaskId": "123e4567-e89b-12d3-a456-426614174000",
  "TaskTitle": "Implementar Login",
  "TaskDescription": "Crear pantalla de login con validaciones",
  "ProjectName": "Sistema de Gestión",
  "AssignedUserEmail": "tu-email@ejemplo.com",
  "AssignedUserName": "Juan Pérez",
  "AssignerName": "María López",
  "DueDate": "2026-03-15T00:00:00Z"
}
EOF

aws lambda invoke \
  --function-name TaskNotifierLambda \
  --payload file://test-assigned.json \
  --cli-binary-format raw-in-base64-out \
  response.json && cat response.json
```

```bash
cat > test-status.json << 'EOF'
{
  "EventType": "TaskStatusChanged",
  "TaskId": "123e4567-e89b-12d3-a456-426614174000",
  "TaskTitle": "Implementar Login",
  "ProjectName": "Sistema de Gestión",
  "AssignedUserEmail": "tu-email@ejemplo.com",
  "AssignedUserName": "Juan Pérez",
  "AssignerName": "María López",
  "OldStatus": "Pending",
  "NewStatus": "InProgress"
}
EOF

aws lambda invoke \
  --function-name TaskNotifierLambda \
  --payload file://test-status.json \
  --cli-binary-format raw-in-base64-out \
  response.json && cat response.json
```

### Ver logs en tiempo real

```bash
aws logs tail /aws/lambda/TaskNotifierLambda --follow
```

---

## Variables de entorno requeridas

| Variable | Descripción | Ejemplo |
|----------|-------------|---------|
| `SENDER_EMAIL` | Email verificado en SES | `noreply@tudominio.com` |
| `APP_BASE_URL` | URL del frontend (para links en emails) | `https://tuapp.com` |

Configurar en: AWS Lambda Console → Functions → TaskNotifierLambda → **Configuration → Environment variables**.

---

## Troubleshooting

| Error | Causa | Solución |
|-------|-------|----------|
| `MessageRejected: Email address is not verified` | SES en Sandbox | Verificar el email destinatario con `aws ses verify-email-identity` |
| `AccessDenied: lambda:InvokeFunction` | ECS Role sin permisos | Agregar política `ECSInvokeLambdaPolicy` al ECS Task Role |
| `AccessDenied: ses:SendEmail` | Lambda Role sin permisos | Adjuntar `LambdaSESPolicy` al `LambdaExecutionRole` |
| Emails no llegan pero Lambda no falla | Filtro de spam | Revisar carpeta spam; en producción salir del Sandbox |