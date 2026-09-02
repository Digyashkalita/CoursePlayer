using Microsoft.EntityFrameworkCore;

namespace CoursePlayer.Data;

public class CoursePlayerDbContext : DbContext
{
    public CoursePlayerDbContext(DbContextOptions<CoursePlayerDbContext> options)
        : base(options)
    {
    }

    public DbSet<Course> Courses => Set<Course>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<Progress> Progresses => Set<Progress>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var dateTimeOffset = new DateTimeOffsetToUtcTicksConverter();
        var timeSpan = new TimeSpanToTicksConverter();

        modelBuilder.Entity<Course>(course =>
        {
            course.HasKey(c => c.Id);
            course.Property(c => c.Title).IsRequired().HasMaxLength(500);
            course.Property(c => c.FolderPath).IsRequired();
            course.Property(c => c.ImportedAt).HasConversion(dateTimeOffset);
            course.Property(c => c.LastOpenedAt).HasConversion(dateTimeOffset);
            course.HasIndex(c => c.FolderPath);
            course.HasIndex(c => c.IsFavorite);
            course.HasIndex(c => c.LastOpenedAt);

            course.HasMany(c => c.Assets)
                  .WithOne(a => a.Course)
                  .HasForeignKey(a => a.CourseId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Asset>(asset =>
        {
            asset.HasKey(a => a.Id);
            asset.Property(a => a.Title).IsRequired().HasMaxLength(500);
            asset.Property(a => a.FilePath).IsRequired();
            asset.Property(a => a.Type).HasConversion<int>();
            asset.Property(a => a.Duration).HasConversion(timeSpan);
            asset.Property(a => a.Codec).HasMaxLength(64);
            asset.Property(a => a.Resolution).HasMaxLength(32);

            // Imports dedupe on this: the same file may appear in two different courses,
            // but never twice inside one course.
            asset.HasIndex(a => new { a.CourseId, a.FilePath }).IsUnique();
            asset.HasIndex(a => a.FilePath);
            asset.HasIndex(a => new { a.CourseId, a.OrderIndex });

            asset.HasOne(a => a.Progress)
                 .WithOne(p => p.Asset)
                 .HasForeignKey<Progress>(p => p.AssetId)
                 .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Progress>(progress =>
        {
            progress.HasKey(p => p.Id);
            progress.Property(p => p.LastAccessedAt).HasConversion(dateTimeOffset);
            progress.HasIndex(p => p.AssetId).IsUnique();
            progress.HasIndex(p => p.LastAccessedAt);
        });
    }
}
