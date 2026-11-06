using Microsoft.EntityFrameworkCore;

namespace WebApi.Infrastructure;

public class BatchDbContext(DbContextOptions<BatchDbContext> options) : DbContext(options)
{
    public DbSet<BatchFileRecord> Batches => Set<BatchFileRecord>();
    public DbSet<BatchLineRecord> BatchLines => Set<BatchLineRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BatchFileRecord>(entity =>
        {
            entity.HasKey(batch => batch.BatchId);
            entity.HasIndex(batch => batch.IdempotencyKey).IsUnique();
            entity.Property(batch => batch.FileName).HasMaxLength(255).IsRequired();
            entity.Property(batch => batch.FileContent).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(batch => batch.Status).HasMaxLength(20).IsRequired();
            entity.Property(batch => batch.ErrorMessage).HasMaxLength(2000);
            entity.HasMany(batch => batch.Lines)
                .WithOne(line => line.Batch)
                .HasForeignKey(line => line.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BatchLineRecord>(entity =>
        {
            entity.HasKey(line => line.Id);
            entity.HasIndex(line => new { line.BatchId, line.LineNumber }).IsUnique();
            entity.Property(line => line.Content).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(line => line.ErrorMessage).HasMaxLength(2000);
        });
    }
}

public class BatchFileRecord
{
    public string BatchId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileContent { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public ICollection<BatchLineRecord> Lines { get; set; } = new List<BatchLineRecord>();
}

public class BatchLineRecord
{
    public long Id { get; set; }
    public string BatchId { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public BatchFileRecord Batch { get; set; } = null!;
}