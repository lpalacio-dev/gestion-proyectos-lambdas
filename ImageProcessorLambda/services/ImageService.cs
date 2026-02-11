using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using static System.Net.Mime.MediaTypeNames;

namespace ImageProcessorLambda.Services
{
    /// <summary>
    /// Servicio de procesamiento de imágenes usando SixLabors.ImageSharp.
    /// Se encarga de redimensionar y comprimir imágenes de perfil que se suben a S3.
    /// 
    /// FLUJO EN TU SISTEMA:
    ///   UserController → S3Service.UploadFileAsync() → S3 (carpeta profile-images/)
    ///                                                       ↓
    ///                                             S3 trigger (PUT event)
    ///                                                       ↓
    ///                                         Esta Lambda → ImageService
    ///                                                       ↓
    ///                         profile-images/thumbnails/   (150x150, crop cuadrado)
    ///                         profile-images/optimized/    (500x500, max fit)
    /// </summary>
    public class ImageService
    {
        /// <summary>
        /// Redimensiona una imagen manteniendo la proporción original (no recorta).
        /// Útil para la versión "optimizada" (500x500) que sirve en el perfil del usuario.
        /// 
        /// Ejemplo: imagen 1200x800 → 500x333 (no se distorsiona)
        /// </summary>
        /// <param name="inputStream">Stream de la imagen original descargada de S3</param>
        /// <param name="width">Ancho máximo en píxeles</param>
        /// <param name="height">Alto máximo en píxeles</param>
        /// <param name="quality">Calidad JPEG 0-100 (85 = buena calidad, menor tamaño)</param>
        /// <returns>MemoryStream con la imagen redimensionada lista para subir a S3</returns>
        public async Task<MemoryStream> ResizeImageAsync(
            Stream inputStream,
            int width,
            int height,
            int quality = 85)
        {
            // Cargar imagen desde el stream de S3
            using var image = await SixLabors.ImageSharp.Image.LoadAsync(inputStream);

            // ResizeMode.Max: reduce la imagen para que quepa dentro de width x height
            // SIN recortar y SIN distorsionar (respeta aspect ratio)
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Max
            }));

            var outputStream = new MemoryStream();

            // Guardar como JPEG con compresión especificada
            var encoder = new JpegEncoder { Quality = quality };
            await image.SaveAsJpegAsync(outputStream, encoder);

            // Importante: resetear posición para que S3 pueda leer desde el inicio
            outputStream.Position = 0;
            return outputStream;
        }

        /// <summary>
        /// Crea un thumbnail cuadrado recortando al centro de la imagen.
        /// Útil para avatares/thumbnails (150x150) donde se necesita un cuadrado exacto.
        /// 
        /// Ejemplo: imagen 1200x800 → recorta el centro → 150x150
        /// Tu frontend puede usar esta URL para el avatar pequeño en el navbar.
        /// </summary>
        /// <param name="inputStream">Stream de la imagen original descargada de S3</param>
        /// <param name="size">Tamaño del cuadrado en píxeles (default: 150)</param>
        /// <returns>MemoryStream con el thumbnail cuadrado listo para subir a S3</returns>
        public async Task<MemoryStream> CreateThumbnailAsync(
            Stream inputStream,
            int size = 150)
        {
            using var image = await SixLabors.ImageSharp.Image.LoadAsync(inputStream);

            // ResizeMode.Crop: recorta la imagen desde el centro para hacer un cuadrado exacto
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Crop
            }));

            var outputStream = new MemoryStream();

            // Quality 90 para thumbnails (un poco más alta porque son pequeños)
            var encoder = new JpegEncoder { Quality = 90 };
            await image.SaveAsJpegAsync(outputStream, encoder);

            outputStream.Position = 0;
            return outputStream;
        }
    }
}
