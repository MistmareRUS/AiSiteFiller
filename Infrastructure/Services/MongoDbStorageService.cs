using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace AiSiteFiller.Infrastructure.Services;

public class MongoDbStorageService : IFileStorageService
{
    private readonly ILogger<MongoDbStorageService> _logger;
    private readonly GridFSBucket _gridFsBucket;

    public MongoDbStorageService(IConfiguration configuration, ILogger<MongoDbStorageService> logger)
    {
        _logger = logger;

        string connectionString = configuration.GetConnectionString("MongoConnection") ?? "mongodb://localhost:27017";

        // Подключаемся к MongoDB и создаем/выбираем базу данных 'ai_media_store'
        var client = new MongoClient(connectionString);
        var database = client.GetDatabase("ai_media_store");

        // Инициализируем стандартный контейнер GridFS
        _gridFsBucket = new GridFSBucket(database, new GridFSBucketOptions
        {
            BucketName = "article_covers"
        });
    }

    public async Task<string> SaveFileAsync(byte[] fileBytes, string fileName, string contentType)
    {
        _logger.LogInformation("📦 [MongoDB] Записываю файл '" + fileName + "' в GridFS...");

        try
        {
            var options = new GridFSUploadOptions
            {
                Metadata = new MongoDB.Bson.BsonDocument
                {
                    { "contentType", contentType },
                    { "uploadedAt", DateTime.UtcNow }
                }
            };

            // Загружаем поток байт в MongoDB
            using var stream = new MemoryStream(fileBytes);
            var fileId = await _gridFsBucket.UploadFromStreamAsync(fileName, stream, options);

            string stringId = fileId.ToString();
            _logger.LogInformation("✅ [MongoDB] Файл успешно сохранен. Уникальный ObjectId: " + stringId);

            return stringId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [MongoDB] Не удалось сохранить медиафайл в GridFS.");
            throw;
        }
    }
}
