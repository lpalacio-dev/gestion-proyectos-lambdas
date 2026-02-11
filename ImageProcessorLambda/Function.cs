using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.S3.Model;
using ImageProcessorLambda.Services;

// Atributo que indica a Lambda qué serializador usar para el evento S3
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ImageProcessorLambda;

/// <summary>
/// Handler principal de la Lambda de procesamiento de imágenes.
/// 
/// CÓMO SE INTEGRA CON TU BACKEND:
///   1. Tu UserController recibe la imagen del frontend vía IFormFile
///   2. Llama a S3Service.UploadFileAsync(stream, fileName, "profile-images", contentType)
///   3. S3Service sube el archivo a: profile-images/{guid}_{fileName}
///   4. S3 detecta el nuevo archivo y dispara esta Lambda automáticamente
///   5. Esta Lambda crea:
///      - profile-images/thumbnails/{guid}_{fileName}  ? Para avatar (150x150)
///      - profile-images/optimized/{guid}_{fileName}   ? Para perfil (500x500)
/// 
/// CÓMO TU BACKEND SIRVE LAS IMÁGENES:
///   - UserService.GetMyProfileAsync() llama a S3Service.GetPresignedUrlAsync(user.ProfileImageKey)
///   - El frontend puede construir la URL del thumbnail reemplazando "profile-images/" 
///     por "profile-images/thumbnails/" en el imageKey guardado en BD
/// </summary>
public class Function
{
    private readonly IAmazonS3 _s3Client;
    private readonly ImageService _imageService;

    // Constructor sin parámetros: Lambda lo usa en producción (usa credenciales del IAM Role)
    public Function()
    {
        _s3Client = new AmazonS3Client();
        _imageService = new ImageService();
    }

    // Constructor con parámetros: usado en tests (permite inyectar mocks)
    public Function(IAmazonS3 s3Client, ImageService imageService)
    {
        _s3Client = s3Client;
        _imageService = imageService;
    }

    /// <summary>
    /// Handler invocado por S3 cuando se sube un archivo a profile-images/
    /// 
    /// El evento S3 contiene una lista de Records (puede ser más de uno por batch).
    /// Cada Record representa un archivo que fue subido.
    /// </summary>
    public async Task FunctionHandler(S3Event s3Event, ILambdaContext context)
    {
        foreach (var record in s3Event.Records)
        {
            try
            {
                var bucket = record.S3.Bucket.Name;
                // La key puede venir URL-encoded desde S3 (ej: "profile-images/mi+foto.jpg")
                var key = Uri.UnescapeDataString(record.S3.Object.Key.Replace("+", " "));

                context.Logger.LogInformation($"[ImageProcessor] Procesando archivo: {bucket}/{key}");

                // Guardia 1: Solo procesar archivos en la carpeta de imágenes de perfil
                // Esto coincide con la carpeta que usa S3Service.UploadFileAsync(..., "profile-images", ...)
                if (!key.StartsWith("profile-images/"))
                {
                    context.Logger.LogInformation($"[ImageProcessor] Ignorando archivo fuera de profile-images/: {key}");
                    continue;
                }

                // Guardia 2: Evitar procesamiento recursivo
                // Cuando esta Lambda sube el thumbnail/optimized, S3 dispararía la Lambda de nuevo.
                // Esta guardia lo evita.
                if (key.Contains("/thumbnails/") || key.Contains("/optimized/"))
                {
                    context.Logger.LogInformation($"[ImageProcessor] Ignorando imagen ya procesada: {key}");
                    continue;
                }

                // ====================================================================
                // PASO 1: Crear thumbnail (150x150, crop cuadrado)
                // ====================================================================

                // Descargar la imagen original de S3
                var originalResponse = await _s3Client.GetObjectAsync(bucket, key);

                // Crear thumbnail cuadrado 150x150
                using var thumbnailStream = await _imageService.CreateThumbnailAsync(
                    originalResponse.ResponseStream,
                    size: 150
                );

                // La key del thumbnail sigue la misma estructura pero en subcarpeta /thumbnails/
                // Ejemplo: "profile-images/abc123_foto.jpg" ? "profile-images/thumbnails/abc123_foto.jpg"
                var thumbnailKey = key.Replace("profile-images/", "profile-images/thumbnails/");
                // Forzar extensión .jpg porque ImageSharp siempre guarda en JPEG
                thumbnailKey = Path.ChangeExtension(thumbnailKey, ".jpg");

                await UploadProcessedImageAsync(bucket, thumbnailKey, thumbnailStream);
                context.Logger.LogInformation($"[ImageProcessor] Thumbnail creado: {thumbnailKey}");

                // ====================================================================
                // PASO 2: Crear versión optimizada (500x500, mantiene proporción)
                // ====================================================================

                // IMPORTANTE: El ResponseStream de S3 solo se puede leer una vez.
                // Hay que volver a descargar para el segundo procesamiento.
                var originalForOptimized = await _s3Client.GetObjectAsync(bucket, key);

                // Crear versión optimizada 500x500 con calidad 85
                using var optimizedStream = await _imageService.ResizeImageAsync(
                    originalForOptimized.ResponseStream,
                    width: 500,
                    height: 500,
                    quality: 85
                );

                // Igual que el thumbnail, pero en subcarpeta /optimized/
                var optimizedKey = key.Replace("profile-images/", "profile-images/optimized/");
                optimizedKey = Path.ChangeExtension(optimizedKey, ".jpg");

                await UploadProcessedImageAsync(bucket, optimizedKey, optimizedStream);
                context.Logger.LogInformation($"[ImageProcessor] Versión optimizada creada: {optimizedKey}");

                context.Logger.LogInformation($"[ImageProcessor] ? Procesamiento completo para: {key}");
            }
            catch (Exception ex)
            {
                // Loggear el error con toda la info disponible para CloudWatch
                context.Logger.LogError(
                    $"[ImageProcessor] ? Error procesando {record.S3.Bucket.Name}/{record.S3.Object.Key}: " +
                    $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
                );

                // Re-lanzar la excepción para que Lambda marque esta invocación como fallida.
                // Esto permite que CloudWatch Alarms detecte el error y aparezca en métricas.
                throw;
            }
        }
    }

    /// <summary>
    /// Sube una imagen procesada (thumbnail u optimizada) a S3 como objeto privado.
    /// 
    /// Se usa CannedACL.Private igual que S3Service.UploadFileAsync() en tu backend,
    /// para que las imágenes solo sean accesibles mediante URLs prefirmadas.
    /// </summary>
    private async Task UploadProcessedImageAsync(string bucket, string key, Stream imageStream)
    {
        var putRequest = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = imageStream,
            ContentType = "image/jpeg",
            CannedACL = S3CannedACL.Private  // Consistente con S3Service del backend
        };

        await _s3Client.PutObjectAsync(putRequest);
    }
}