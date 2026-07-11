using AiSiteFiller.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AiSiteFiller.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<ArticleTask> ArticlesQueue => Set<ArticleTask>();

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
    }
}
