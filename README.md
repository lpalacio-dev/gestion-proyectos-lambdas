# ⚡ Gestión de Proyectos — Lambdas

Funciones AWS Lambda que extienden el [backend](../gestion-de-proyectos-backend) con procesamiento asíncrono. Este repositorio contiene dos funciones independientes escritas en .NET 8.

---

## 📋 Tabla de Contenidos

- [Descripción General](#-descripción-general)
- [Funciones Lambda](#-funciones-lambda)
  - [ImageProcessorLambda](#️-imageprocessorlambda)
  - [TaskNotifierLambda](#-tasknotifierlambda)
- [Estructura del Repositorio](#-estructura-del-repositorio)
- [Pre-requisitos](#-pre-requisitos)
- [Despliegue](#-despliegue)
- [IAM Role y Permisos](#-iam-role-y-permisos)
- [Testing](#-testing)
- [Monitoreo](#-monitoreo)

---

## 🎯 Descripción General

Estas Lambdas se integran con el sistema de gestión de proyectos para ejecutar tareas asíncronas que no deben bloquear la API principal:

```
Backend (ECS Fargate)
        │
        ├──► S3 ObjectCreated ──► ImageProcessorLambda
        │                         Procesa imágenes de perfil
        │
        └──► Invocación directa ──► TaskNotifierLambda
                                    Envía emails vía SES
```

| Lambda | Trigger | Runtime | Memoria | Timeout |
|---|---|---|---|---|
| `ImageProcessorLambda` | S3 Event (`ObjectCreated`) | .NET 8 | 512 MB | 60 s |
| `TaskNotifierLambda` | Invocación directa desde el backend | .NET 8 | 128 MB | 30 s |

---

## 🔧 Funciones Lambda

### 🖼️ ImageProcessorLambda

Procesa automáticamente las imágenes de perfil que los usuarios suben al bucket S3. Se dispara sin intervención del backend.

**Trigger:** `s3:ObjectCreated:Put` con prefijo `profile-images/`

**Flujo:**

```
Usuario sube imagen
        │
        ▼
S3: profile-images/{userId}/{filename}.jpg
        │
        ▼
ImageProcessorLambda
        │
        ├──► profile-images/thumbnails/{filename}.jpg   (150×150, crop centrado, calidad 90%)
        └──► profile-images/optimized/{filename}.jpg    (500×500, aspect ratio, calidad 85%)
```

**Lógica de seguridad:** la función ignora automáticamente archivos ya ubicados en `/thumbnails/` o `/optimized/` para evitar bucles de procesamiento recursivo.

**Dependencias NuGet:**

| Paquete | Versión |
|---|---|
| `Amazon.Lambda.Core` | 2.2.0 |
| `Amazon.Lambda.S3Events` | 3.1.0 |
| `Amazon.Lambda.Serialization.SystemTextJson` | 2.4.0 |
| `AWSSDK.S3` | 3.7.307 |
| `SixLabors.ImageSharp` | 3.1.0 |

---

### 📧 TaskNotifierLambda

Envía emails HTML transaccionales a los usuarios mediante **Amazon SES**. Es invocada de forma asíncrona por el backend cuando ocurre un evento relevante en una tarea.

**Trigger:** Invocación directa (`InvocationType.Event`) desde `TaskService.cs` del backend.

**Eventos soportados:**

| `EventType` | Descripción | Destinatario |
|---|---|---|
| `TaskAssigned` | Se asigna una tarea a un usuario | Usuario asignado |
| `TaskStatusChanged` | Cambia el estado de una tarea | Usuario asignado |
| `TaskDueSoon` | Una tarea está próxima a vencer | Usuario asignado |

**Payload esperado:**

```json
{
  "EventType": "TaskAssigned",
  "TaskId": "123e4567-e89b-12d3-a456-426614174000",
  "TaskTitle": "Implementar Login",
  "TaskDescription": "Crear pantalla de login con validaciones",
  "ProjectName": "Sistema de Gestión",
  "AssignedUserEmail": "usuario@ejemplo.com",
  "AssignedUserName": "Juan Pérez",
  "AssignerName": "María López",
  "DueDate": "2024-03-15T00:00:00Z"
}
```

**Variable de entorno requerida:**

| Variable | Descripción | Ejemplo |
|---|---|---|
| `SENDER_EMAIL` | Email remitente verificado en SES | `noreply@tudominio.com` |

**Dependencias NuGet:**

| Paquete | Versión |
|---|---|
| `Amazon.Lambda.Core` | 2.2.0 |
| `Amazon.Lambda.Serialization.SystemTextJson` | 2.4.0 |
| `AWSSDK.SimpleEmail` | 3.7.400 |

---

## 📁 Estructura del Repositorio

```
gestion-proyectos-lambdas/
├── ImageProcessorLambda/
│   └── src/
│       └── ImageProcessorLambda/
│           ├── Function.cs                      # Handler principal S3
│           ├── ImageProcessorLambda.csproj
│           ├── aws-lambda-tools-defaults.json   # Config de deploy
│           ├── Models/
│           │   └── S3EventModels.cs
│           └── Services/
│               └── ImageService.cs              # Lógica de resize y thumbnail
│
├── TaskNotifierLambda/
│   └── src/
│       └── TaskNotifierLambda/
│           ├── Function.cs                      # Handler principal
│           ├── TaskNotifierLambda.csproj
│           ├── aws-lambda-tools-defaults.json
│           ├── Models/
│           │   └── TaskNotificationEvent.cs     # Modelo del evento entrante
│           └── Services/
│               └── EmailService.cs              # Envío de emails vía SES
│
└── README.md
```

---

## 🔧 Pre-requisitos

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [AWS CLI](https://aws.amazon.com/cli/) configurado con `aws configure`
- [Amazon.Lambda.Tools](https://github.com/aws/aws-extensions-for-dotnet-cli)
- IAM Role con los permisos necesarios (ver sección [IAM](#-iam-role-y-permisos))
- Bucket S3 con la carpeta `profile-images/` configurada
- Email verificado en Amazon SES

```bash
# Instalar la herramienta de deploy Lambda para .NET
dotnet tool install -g Amazon.Lambda.Tools
```

---

## 🚀 Despliegue

### ImageProcessorLambda

```bash
cd ImageProcessorLambda/src/ImageProcessorLambda

dotnet lambda deploy-function ImageProcessorLambda \
    --function-role <LAMBDA_EXECUTION_ROLE_ARN> \
    --region us-east-2
```

**Configurar el trigger S3** (una sola vez tras el primer deploy):

```bash
# Autorizar a S3 para invocar la Lambda
aws lambda add-permission \
    --function-name ImageProcessorLambda \
    --statement-id S3InvokePermission \
    --action lambda:InvokeFunction \
    --principal s3.amazonaws.com \
    --source-arn arn:aws:s3:::TU-BUCKET-NAME

# Registrar la notificación en el bucket
aws s3api put-bucket-notification-configuration \
    --bucket TU-BUCKET-NAME \
    --notification-configuration '{
      "LambdaFunctionConfigurations": [{
        "Id": "ProcessProfileImages",
        "LambdaFunctionArn": "arn:aws:lambda:us-east-2:ACCOUNT_ID:function:ImageProcessorLambda",
        "Events": ["s3:ObjectCreated:Put"],
        "Filter": {
          "Key": {
            "FilterRules": [{ "Name": "prefix", "Value": "profile-images/" }]
          }
        }
      }]
    }'
```

### TaskNotifierLambda

**Verificar el email remitente en SES** (una sola vez):

```bash
aws ses verify-email-identity --email-address noreply@tudominio.com

# Verificar el estado de verificación
aws ses get-identity-verification-attributes \
    --identities noreply@tudominio.com
```

> ⚠️ SES opera en **modo sandbox** por defecto: solo permite enviar a emails verificados. Para producción, solicitar acceso en AWS Console → SES → Account dashboard → *Request production access*.

```bash
cd TaskNotifierLambda/src/TaskNotifierLambda

dotnet lambda deploy-function TaskNotifierLambda \
    --function-role <LAMBDA_EXECUTION_ROLE_ARN> \
    --region us-east-2 \
    --environment-variables SENDER_EMAIL=noreply@tudominio.com
```

### Actualizar una función existente

```bash
# Redespliega solo el código sin modificar la configuración
dotnet lambda deploy-function <NombreFuncion> --region us-east-2
```

---

## 🔐 IAM Role y Permisos

Ambas Lambdas comparten el mismo execution role. Crearlo una sola vez:

```bash
# 1. Crear el role
aws iam create-role \
    --role-name LambdaExecutionRole \
    --assume-role-policy-document '{
      "Version": "2012-10-17",
      "Statement": [{
        "Effect": "Allow",
        "Principal": { "Service": "lambda.amazonaws.com" },
        "Action": "sts:AssumeRole"
      }]
    }'

# 2. Adjuntar política de logs básica (CloudWatch)
aws iam attach-role-policy \
    --role-name LambdaExecutionRole \
    --policy-arn arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole

# 3. Crear política personalizada
aws iam create-policy \
    --policy-name LambdaProjectsPolicy \
    --policy-document '{
      "Version": "2012-10-17",
      "Statement": [
        {
          "Effect": "Allow",
          "Action": ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
          "Resource": "arn:aws:s3:::TU-BUCKET-NAME/*"
        },
        {
          "Effect": "Allow",
          "Action": ["ses:SendEmail", "ses:SendRawEmail"],
          "Resource": "*"
        }
      ]
    }'

# 4. Adjuntar la política al role
aws iam attach-role-policy \
    --role-name LambdaExecutionRole \
    --policy-arn arn:aws:iam::ACCOUNT_ID:policy/LambdaProjectsPolicy

# 5. Obtener el ARN (necesario para el deploy)
aws iam get-role --role-name LambdaExecutionRole --query 'Role.Arn' --output text
```

---

## 🧪 Testing

### ImageProcessorLambda

```bash
cat > test-s3-event.json << EOF
{
  "Records": [{
    "s3": {
      "bucket": { "name": "TU-BUCKET-NAME" },
      "object": { "key": "profile-images/test-image.jpg" }
    }
  }]
}
EOF

aws lambda invoke \
    --function-name ImageProcessorLambda \
    --payload file://test-s3-event.json \
    --cli-binary-format raw-in-base64-out \
    response.json && cat response.json
```

Verificar en el bucket que se crearon:
- `profile-images/thumbnails/test-image.jpg`
- `profile-images/optimized/test-image.jpg`

### TaskNotifierLambda

```bash
cat > test-notification.json << EOF
{
  "EventType": "TaskAssigned",
  "TaskId": "123e4567-e89b-12d3-a456-426614174000",
  "TaskTitle": "Implementar Login",
  "TaskDescription": "Crear pantalla de login con validaciones",
  "ProjectName": "Sistema de Gestión",
  "AssignedUserEmail": "tu-email-verificado@ejemplo.com",
  "AssignedUserName": "Juan Pérez",
  "AssignerName": "María López",
  "DueDate": "2025-03-15T00:00:00Z"
}
EOF

aws lambda invoke \
    --function-name TaskNotifierLambda \
    --payload file://test-notification.json \
    --cli-binary-format raw-in-base64-out \
    response.json && cat response.json
```

---

## 📊 Monitoreo

**Ver logs en tiempo real:**

```bash
aws logs tail /aws/lambda/ImageProcessorLambda --follow
aws logs tail /aws/lambda/TaskNotifierLambda --follow
```

**Troubleshooting frecuente:**

| Problema | Causa probable | Solución |
|---|---|---|
| Lambda timeout | Imagen muy grande | `aws lambda update-function-configuration --function-name ImageProcessorLambda --timeout 120` |
| Out of memory | Imagen de alta resolución | Aumentar memoria a 1024 MB |
| Email no enviado | SES en sandbox o email no verificado | Verificar destinatario en SES o solicitar producción |
| Permisos insuficientes | IAM Role incompleto | Revisar logs en CloudWatch y verificar políticas del role |
| Procesamiento recursivo | Trigger sin prefijo correcto | Asegurar que el trigger solo escucha `profile-images/` (sin subcarpetas) |

---

## 🔗 Repositorios relacionados

- **Backend:** [`gestion-de-proyectos-backend`](../gestion-de-proyectos-backend) — ASP.NET Core 8, ECS Fargate
- **Frontend:** [`project-management-front`](../project-management-front) — Angular 20, S3

---

*Desarrollado con .NET 8 · AWS Lambda · Amazon S3 · Amazon SES*
