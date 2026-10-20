using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TwitchDownloaderWeb.Data
{
    public class LibraryDbContext : DbContext
    {
        public LibraryDbContext(DbContextOptions<LibraryDbContext> options) : base(options) { }

        public DbSet<LibraryEntry> Entries => Set<LibraryEntry>();
        public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
        public DbSet<SavedChannel> Channels => Set<SavedChannel>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LibraryEntry>(entity =>
            {
                entity.ToTable("LibraryEntries");
                entity.Property(x => x.Title).HasMaxLength(500);
                entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
                entity.Property(x => x.GenerationStatus).HasConversion<string>().HasMaxLength(32);
                entity.Property(x => x.TrimMode).HasConversion<string>().HasMaxLength(16);
                entity.HasIndex(x => x.StreamDateUtc);
                entity.HasIndex(x => x.AddedAtUtc);
                entity.HasIndex(x => x.Status);
            });

            modelBuilder.Entity<AuditEvent>(entity =>
            {
                entity.Property(x => x.Action).HasMaxLength(64);
                entity.HasIndex(x => x.TimestampUtc);
                entity.HasIndex(x => x.EntryId);
            });

            modelBuilder.Entity<SavedChannel>(entity =>
            {
                entity.ToTable("Channels");
                entity.Property(x => x.Login).HasMaxLength(25);
                entity.Property(x => x.DisplayName).HasMaxLength(100);
                entity.Property(x => x.TwitchUserId).HasMaxLength(32);
                entity.HasIndex(x => x.Login).IsUnique();
            });
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // SQLite has no date type and EF reads DateTimes back as Unspecified. Everything we store is UTC,
            // so normalize on the way in and mark it UTC on the way out.
            configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
            configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
        }

        private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
        {
            public UtcDateTimeConverter() : base(
                v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
            { }
        }
    }
}
