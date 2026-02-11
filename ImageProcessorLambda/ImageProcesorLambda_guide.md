# 🖼️ ImageProcessor Lambda

Lambda de .NET 8 que procesa automáticamente imágenes de perfil cuando se suben a S3.

## Qué hace

Cuando tu backend sube una imagen a `profile-images/`, esta Lambda genera:
- **Thumbnail** (`profile-images/thumbnails/`) → 150×150 px, crop cuadrado, JPEG quality 90
- **Versión optimizada** (`profile-images/optimized/`) → máx 500×500 px, JPEG quality 85

## Cómo se integra con tu backend existente

```
Frontend → UserController → S3Service.UploadFileAsync("profile-images/")
                                           ↓
                                   S3 dispara esta Lambda
                                           ↓
                         profile-images/thumbnails/{key}   ← para avatar en navbar
                         profile-images/optimized/{key}    ← para página de perfil
```

Tu `UserService.GetMyProfileAsync()` ya devuelve `ProfileImageUrl` (URL prefirmada).  
El frontend puede construir la URL del thumbnail así:
```
thumbnailKey = profileImageKey.replace("profile-images/", "profile-images/thumbnails/")
```

---

## Setup

### Prerrequisitos

```bash
# .NET 8 SDK
dotnet --version  # debe ser 8.x

# Lambda tools
dotnet tool install -g Amazon.Lambda.Tools

# AWS CLI configurado
aws configure
```

### 1. Crear el IAM Role para Lambda

```bash
# Trust policy
cat > lambda-trust-policy.json << 'EOF'
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Service": "lambda.amazonaws.com" },
    "Action": "sts:AssumeRole"
  }]
}
EOF

aws iam create-role \
  --role-name ImageProcessorLambdaRole \
  --assume-role-policy-document file://lambda-trust-policy.json

# Permisos básicos de Lambda (CloudWatch Logs)
aws iam attach-role-policy \
  --role-name ImageProcessorLambdaRole \
  --policy-arn arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole

# Permisos de S3 (leer y escribir en tu bucket)
cat > s3-policy.json << 'EOF'
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": ["s3:GetObject", "s3:PutObject"],
    "Resource": "arn:aws:s3:::TU-BUCKET-NAME/*"
  }]
}
EOF

aws iam create-policy \
  --policy-name ImageProcessorS3Policy \
  --policy-document file://s3-policy.json

# Reemplaza ACCOUNT_ID con tu ID de cuenta de AWS
aws iam attach-role-policy \
  --role-name ImageProcessorLambdaRole \
  --policy-arn arn:aws:iam::ACCOUNT_ID:policy/ImageProcessorS3Policy

# Guarda el ARN del role para el siguiente paso
aws iam get-role --role-name ImageProcessorLambdaRole --query 'Role.Arn' --output text
```

### 2. Deploy de la Lambda

```bash
cd src/ImageProcessorLambda

dotnet lambda deploy-function ImageProcessorLambda \
  --function-role <ROLE_ARN_DEL_PASO_ANTERIOR> \
  --region us-east-1
```

### 3. Configurar el trigger de S3

```bash
# Dar permiso a S3 para invocar la Lambda
aws lambda add-permission \
  --function-name ImageProcessorLambda \
  --statement-id S3InvokePermission \
  --action lambda:InvokeFunction \
  --principal s3.amazonaws.com \
  --source-arn arn:aws:s3:::TU-BUCKET-NAME

# Configurar notificación en S3
cat > s3-notification.json << 'EOF'
{
  "LambdaFunctionConfigurations": [{
    "Id": "ProcessProfileImages",
    "LambdaFunctionArn": "arn:aws:lambda:us-east-1:ACCOUNT_ID:function:ImageProcessorLambda",
    "Events": ["s3:ObjectCreated:Put"],
    "Filter": {
      "Key": {
        "FilterRules": [{"Name": "prefix", "Value": "profile-images/"}]
      }
    }
  }]
}
EOF

aws s3api put-bucket-notification-configuration \
  --bucket TU-BUCKET-NAME \
  --notification-configuration file://s3-notification.json
```

---

## Correr los tests localmente

```bash
cd test/ImageProcessorLambda.Tests
dotnet test
```

---

## Testing en AWS

### Test con evento simulado

```bash
cat > test-event.json << 'EOF'
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
  --payload file://test-event.json \
  --cli-binary-format raw-in-base64-out \
  response.json

cat response.json
```

### Ver logs en CloudWatch

```bash
aws logs tail /aws/lambda/ImageProcessorLambda --follow
```

---

## Troubleshooting

| Problema | Causa | Solución |
|---------|-------|----------|
| Timeout | Imagen muy grande | `aws lambda update-function-configuration --function-name ImageProcessorLambda --timeout 120` |
| Out of memory | Imagen en alta resolución | `aws lambda update-function-configuration --function-name ImageProcessorLambda --memory-size 1024` |
| AccessDenied en S3 | IAM Role sin permisos | Verificar que el role tenga `s3:GetObject` y `s3:PutObject` |
| Bucle infinito | Sin guardia de thumbnails | La Function.cs ya lo maneja verificando `/thumbnails/` y `/optimized/` en la key |

---

## Estructura de carpetas en S3

```
tu-bucket/
├── profile-images/
│   ├── abc123_foto.jpg          ← Original (subida por tu backend)
│   ├── thumbnails/
│   │   └── abc123_foto.jpg      ← 150×150, generada por esta Lambda
│   └── optimized/
│       └── abc123_foto.jpg      ← máx 500×500, generada por esta Lambda
```