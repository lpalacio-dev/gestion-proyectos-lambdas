using Amazon.Lambda.S3Events;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using ImageProcessorLambda.Services;
using Moq;
using SixLabors.ImageSharp;
using Xunit;

namespace ImageProcessorLambda.Tests
{
    /// <summary>
    /// Tests unitarios para la Lambda de procesamiento de imágenes.
    /// 
    /// Se usa Moq para simular IAmazonS3 sin necesitar conexión real a AWS.
    /// Esto permite correr los tests localmente y en CI/CD sin costos.
    /// </summary>
    public class FunctionTests
    {
        // ====================================================================
        // HELPERS: Crear objetos de test reutilizables
        // ====================================================================

        /// <summary>
        /// Crea un evento S3 simulado como el que recibiría la Lambda en producción.
        /// </summary>
        private static S3Event CreateS3Event(string bucketName, string objectKey)
        {
            return new S3Event
            {
                Records = new List<S3Event.S3EventNotificationRecord>
                {
                    new S3Event.S3EventNotificationRecord
                    {
                        S3 = new S3Event.S3Entity
                        {
                            Bucket = new S3Event.S3BucketEntity { Name = bucketName },
                            Object = new S3Event.S3ObjectEntity { Key = objectKey }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Crea un mock de S3 que devuelve una imagen JPEG mínima válida.
        /// La imagen es 1x1 píxeles codificada en base64.
        /// </summary>
        private static Mock<IAmazonS3> CreateS3MockWithImage()
        {
            var mock = new Mock<IAmazonS3>();

            // Imagen JPEG mínima válida (1x1 píxel, ~631 bytes)
            // Esto evita que ImageSharp falle al intentar decodificarla
            var minimalJpeg = Convert.FromBase64String(
                "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8U" +
                "HRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgN" +
                "DRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIy" +
                "MjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAFgABAQEAAAAAAAAAAAAAAAAABgUE/8QAIhAA" +
                "AgIBBQEBAAAAAAAAAAAAAQIDBAUREiExQf/EABQBAQAAAAAAAAAAAAAAAAAAAAD/xAAUEQEA" +
                "AAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCwABpjsAAAAAAAAAAAAAAAAAAAAAAAAAAAB/9k="
            );

            mock.Setup(s => s.GetObjectAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new GetObjectResponse
                {
                    ResponseStream = new MemoryStream(minimalJpeg)
                });

            mock.Setup(s => s.PutObjectAsync(
                    It.IsAny<PutObjectRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutObjectResponse());

            return mock;
        }

        // ====================================================================
        // TESTS: Function.FunctionHandler
        // ====================================================================

        [Fact]
        public async Task FunctionHandler_ArchivoPerfil_CreaMiniaturaYVersionOptimizada()
        {
            // ARRANGE
            var s3Mock = CreateS3MockWithImage();
            var imageService = new ImageService();
            var function = new Function(s3Mock.Object, imageService);
            var context = new TestLambdaContext();

            var evento = CreateS3Event("mi-bucket", "profile-images/abc123_foto.jpg");

            // ACT
            await function.FunctionHandler(evento, context);

            // ASSERT: S3 debió recibir 2 llamadas PutObject (thumbnail + optimized)
            s3Mock.Verify(
                s => s.PutObjectAsync(
                    It.Is<PutObjectRequest>(r => r.Key.Contains("/thumbnails/")),
                    It.IsAny<CancellationToken>()
                ),
                Times.Once,
                "Debió crear el thumbnail en profile-images/thumbnails/"
            );

            s3Mock.Verify(
                s => s.PutObjectAsync(
                    It.Is<PutObjectRequest>(r => r.Key.Contains("/optimized/")),
                    It.IsAny<CancellationToken>()
                ),
                Times.Once,
                "Debió crear la versión optimizada en profile-images/optimized/"
            );
        }

        [Fact]
        public async Task FunctionHandler_ArchivoFueraDeCarpeta_NoProcesamientoEjecutado()
        {
            // ARRANGE: Archivo en una carpeta diferente (ej: uploads generales)
            var s3Mock = new Mock<IAmazonS3>();
            var imageService = new ImageService();
            var function = new Function(s3Mock.Object, imageService);
            var context = new TestLambdaContext();

            var evento = CreateS3Event("mi-bucket", "documents/contrato.pdf");

            // ACT
            await function.FunctionHandler(evento, context);

            // ASSERT: No debe llamar a GetObject ni a PutObject
            s3Mock.Verify(
                s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "No debe descargar archivos fuera de profile-images/"
            );

            s3Mock.Verify(
                s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "No debe subir archivos procesados si no es una imagen de perfil"
            );
        }

        [Fact]
        public async Task FunctionHandler_ThumbnailExistente_NoProcesaDeNuevo()
        {
            // ARRANGE: Simula que S3 notifica de un thumbnail que esta Lambda ya creó
            // (sin esta guardia, entraría en bucle infinito)
            var s3Mock = new Mock<IAmazonS3>();
            var imageService = new ImageService();
            var function = new Function(s3Mock.Object, imageService);
            var context = new TestLambdaContext();

            var evento = CreateS3Event("mi-bucket", "profile-images/thumbnails/abc123_foto.jpg");

            // ACT
            await function.FunctionHandler(evento, context);

            // ASSERT: No debe procesar thumbnails ya existentes
            s3Mock.Verify(
                s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "No debe procesar imágenes que ya están en /thumbnails/ o /optimized/"
            );
        }

        [Fact]
        public async Task FunctionHandler_VersionOptimizadaExistente_NoProcesaDeNuevo()
        {
            // ARRANGE: Similar al test anterior, pero para la carpeta /optimized/
            var s3Mock = new Mock<IAmazonS3>();
            var imageService = new ImageService();
            var function = new Function(s3Mock.Object, imageService);
            var context = new TestLambdaContext();

            var evento = CreateS3Event("mi-bucket", "profile-images/optimized/abc123_foto.jpg");

            // ACT
            await function.FunctionHandler(evento, context);

            // ASSERT
            s3Mock.Verify(
                s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Never
            );
        }

        [Fact]
        public async Task FunctionHandler_MultiplesArchivos_ProcesaTodos()
        {
            // ARRANGE: Evento S3 con múltiples archivos (batch upload)
            var s3Mock = CreateS3MockWithImage();
            var imageService = new ImageService();
            var function = new Function(s3Mock.Object, imageService);
            var context = new TestLambdaContext();

            var evento = new S3Event
            {
                Records = new List<S3Event.S3EventNotificationRecord>
                {
                    new()
                    {
                        S3 = new S3Event.S3Entity
                        {
                            Bucket = new S3Event.S3BucketEntity { Name = "mi-bucket" },
                            Object = new S3Event.S3ObjectEntity { Key = "profile-images/user1_foto.jpg" }
                        }
                    },
                    new()
                    {
                        S3 = new S3Event.S3Entity
                        {
                            Bucket = new S3Event.S3BucketEntity { Name = "mi-bucket" },
                            Object = new S3Event.S3ObjectEntity { Key = "profile-images/user2_foto.jpg" }
                        }
                    }
                }
            };

            // ACT
            await function.FunctionHandler(evento, context);

            // ASSERT: 2 archivos × 2 versiones = 4 llamadas PutObject
            s3Mock.Verify(
                s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Exactly(4),
                "Debe crear thumbnail + optimized para cada uno de los 2 archivos"
            );
        }

        [Fact]
        public async Task FunctionHandler_KeyConEspacios_DecodificaCorrectamente()
        {
            // ARRANGE: S3 URL-encodea los nombres de archivo (espacios → "+")
            // Tu S3Service usa Guid como prefijo, pero el nombre original puede tener espacios
            var s3Mock = CreateS3MockWithImage();
            var imageService = new ImageService();
            var function = new Function(s3Mock.Object, imageService);
            var context = new TestLambdaContext();

            // S3 envía la key con "+" en lugar de espacios
            var evento = CreateS3Event("mi-bucket", "profile-images/abc123_mi+foto+de+perfil.jpg");

            // ACT - No debe lanzar excepción
            await function.FunctionHandler(evento, context);

            // ASSERT: Debe procesar el archivo correctamente
            s3Mock.Verify(
                s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2)
            );
        }

        // ====================================================================
        // TESTS: ImageService (tests de transformación de imágenes)
        // ====================================================================

        [Fact]
        public async Task ImageService_CreateThumbnailAsync_DevuelveStreamNoVacio()
        {
            // ARRANGE: Crear una imagen de prueba en memoria (10x10 píxeles blancos)
            var imageService = new ImageService();
            using var inputStream = CreateTestImageStream(10, 10);

            // ACT
            using var result = await imageService.CreateThumbnailAsync(inputStream, size: 5);

            // ASSERT
            Assert.NotNull(result);
            Assert.True(result.Length > 0, "El stream del thumbnail no debe estar vacío");
            Assert.Equal(0, result.Position, "La posición debe estar en 0 para permitir lectura");
        }

        [Fact]
        public async Task ImageService_ResizeImageAsync_DevuelveStreamNoVacio()
        {
            // ARRANGE
            var imageService = new ImageService();
            using var inputStream = CreateTestImageStream(100, 100);

            // ACT
            using var result = await imageService.ResizeImageAsync(inputStream, 50, 50, quality: 80);

            // ASSERT
            Assert.NotNull(result);
            Assert.True(result.Length > 0, "El stream de la versión optimizada no debe estar vacío");
            Assert.Equal(0, result.Position);
        }

        /// <summary>
        /// Crea un stream con una imagen JPEG simple para tests de ImageService.
        /// Usa SixLabors.ImageSharp para generar una imagen de color sólido.
        /// </summary>
        private static MemoryStream CreateTestImageStream(int width, int height)
        {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(width, height);
            var stream = new MemoryStream();
            image.SaveAsJpeg(stream);
            stream.Position = 0;
            return stream;
        }
    }
}