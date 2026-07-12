using AiSiteFiller.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AiSiteFiller.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<ArticleTask> ArticlesQueue => Set<ArticleTask>();
    public DbSet<PublicationTask> PublicationTasks => Set<PublicationTask>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Загружаем базовую конфигурацию, а затем переопределяем её локальным файлом
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .Build();


        string? connectionString = configuration.GetConnectionString("PostgresConnection");
        optionsBuilder.UseNpgsql(connectionString);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ArticleTask>(entity =>
        {
            entity.ToTable("articles_queue");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Topic).HasColumnName("topic").IsRequired();
            entity.Property(e => e.Category).HasColumnName("category").HasMaxLength(100).IsRequired();
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(50).HasConversion<string>().IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.SiteId).HasColumnName("site_id").HasMaxLength(100).IsRequired(false);
            entity.Property(e => e.ContentHtml).HasColumnName("content_html").IsRequired(false);
            entity.Property(e => e.MongoImageId).HasColumnName("mongo_image_id").IsRequired(false);
        });

        modelBuilder.Entity<PublicationTask>(entity =>
        {
            // Название таблицы в нижнем регистре с подчеркиванием
            entity.ToTable("publication_tasks");

            // Явно задаем snake_case для каждого поля в Postgres
            entity.Property(p => p.Id).HasColumnName("id");
            entity.Property(p => p.ArticleTaskId).HasColumnName("article_task_id");
            entity.Property(p => p.Platform).HasColumnName("platform");
            entity.Property(p => p.Status).HasColumnName("status");
            entity.Property(p => p.ExternalId).HasColumnName("external_id");
            entity.Property(p => p.ProcessedAt).HasColumnName("processed_at");
            entity.Property(p => p.ErrorMessage).HasColumnName("error_message");

            // Настройка связи "Один ко многим" с каскадным удалением
            entity.HasOne(p => p.ArticleTask)
                .WithMany(a => a.PublicationTasks)
                .HasForeignKey(p => p.ArticleTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public async Task<PublicationTask?> GetNextPendingPublicationTaskAsync()
    {
        // Ищем первую подзадачу в очереди, которая еще не опубликована, 
        // и сразу загружаем связанные данные родительской статьи (Include)
        return await PublicationTasks
            .Include(p => p.ArticleTask)
            .FirstOrDefaultAsync(p => p.Status == Domain.Enums.TaskStatus.Pending);
    }

}
