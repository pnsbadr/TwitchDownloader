using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TwitchDownloaderWeb.Services.Accounts;

namespace TwitchDownloaderWeb.Data
{
    public class LibraryDbContext : DbContext
    {
        private readonly RequestActor _actor;

        /// <param name="actor">The request's user/team; null (background jobs, tests) sees every team's rows.</param>
        public LibraryDbContext(DbContextOptions<LibraryDbContext> options, RequestActor? actor = null) : base(options)
        {
            _actor = actor ?? new RequestActor();
        }

        public RequestActor Actor => _actor;

        // Read by the query filters; EF evaluates context members per query
        private bool ScopedToTeam => _actor.IsRequest;
        private int? ScopeTeamId => _actor.TeamId;

        public DbSet<LibraryEntry> Entries => Set<LibraryEntry>();
        public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
        public DbSet<SavedChannel> Channels => Set<SavedChannel>();
        public DbSet<VodAudioRescue> AudioRescues => Set<VodAudioRescue>();
        public DbSet<RescuedAudioSegment> RescuedAudioSegments => Set<RescuedAudioSegment>();
        public DbSet<UserAccount> Users => Set<UserAccount>();
        public DbSet<Team> Teams => Set<Team>();
        public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
        public DbSet<StaffAccount> StaffAccounts => Set<StaffAccount>();
        public DbSet<AccountLink> AccountLinks => Set<AccountLink>();
        public DbSet<TeamRole> TeamRoles => Set<TeamRole>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LibraryEntry>(entity =>
            {
                entity.ToTable("LibraryEntries");
                entity.Property(x => x.Title).HasMaxLength(500);
                entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
                entity.Property(x => x.GenerationStatus).HasConversion<string>().HasMaxLength(32);
                entity.Property(x => x.TrimMode).HasConversion<string>().HasMaxLength(16);
                entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
                entity.Property(x => x.ClipSlug).HasMaxLength(200);
                entity.HasIndex(x => x.StreamDateUtc);
                entity.HasIndex(x => x.AddedAtUtc);
                entity.HasIndex(x => x.Status);
                entity.HasIndex(x => x.TeamId);
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
                // A request sees only its own team's library; requests without a resolved team see nothing
                entity.HasQueryFilter(x => !ScopedToTeam || x.TeamId == ScopeTeamId);
            });

            modelBuilder.Entity<AuditEvent>(entity =>
            {
                entity.Property(x => x.Action).HasMaxLength(64);
                entity.HasIndex(x => x.TimestampUtc);
                entity.HasIndex(x => x.EntryId);
                entity.HasIndex(x => x.TeamId);
                entity.HasIndex(x => x.UserId);
            });

            modelBuilder.Entity<SavedChannel>(entity =>
            {
                entity.ToTable("Channels");
                entity.Property(x => x.Login).HasMaxLength(25);
                entity.Property(x => x.DisplayName).HasMaxLength(100);
                entity.Property(x => x.TwitchUserId).HasMaxLength(32);
                entity.HasIndex(x => new { x.TeamId, x.Login }).IsUnique();
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
                entity.HasQueryFilter(x => !ScopedToTeam || x.TeamId == ScopeTeamId);
            });

            modelBuilder.Entity<UserAccount>(entity =>
            {
                entity.ToTable("Users");
                entity.Property(x => x.Username).HasMaxLength(32);
                entity.Property(x => x.NormalizedUsername).HasMaxLength(32);
                entity.Property(x => x.PasswordHash).HasMaxLength(200);
                entity.Property(x => x.SecurityStamp).HasMaxLength(64);
                entity.Property(x => x.TimeZone).HasMaxLength(16);
                entity.HasIndex(x => x.NormalizedUsername).IsUnique();
            });

            modelBuilder.Entity<Team>(entity =>
            {
                entity.Property(x => x.Name).HasMaxLength(100);
                entity.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<TeamMember>(entity =>
            {
                entity.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
                entity.HasIndex(x => x.UserId);
                entity.HasOne(x => x.Team).WithMany(x => x.Members).HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(x => x.User).WithMany(x => x.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<TeamRole>(entity =>
            {
                entity.Property(x => x.Name).HasMaxLength(50);
                entity.HasIndex(x => new { x.TeamId, x.Name }).IsUnique();
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<StaffAccount>(entity =>
            {
                entity.Property(x => x.Username).HasMaxLength(32);
                entity.Property(x => x.NormalizedUsername).HasMaxLength(32);
                entity.Property(x => x.PasswordHash).HasMaxLength(200);
                entity.Property(x => x.SecurityStamp).HasMaxLength(64);
                entity.HasIndex(x => x.NormalizedUsername).IsUnique();
            });

            modelBuilder.Entity<AccountLink>(entity =>
            {
                entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
                entity.Property(x => x.TokenHash).HasMaxLength(64);
                entity.Property(x => x.Note).HasMaxLength(200);
                entity.HasIndex(x => x.TokenHash).IsUnique();
                entity.HasIndex(x => x.TeamId);
                entity.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<VodAudioRescue>(entity =>
            {
                entity.ToTable("AudioRescues");
                entity.HasKey(x => x.VodId);
                entity.Property(x => x.VodId).ValueGeneratedNever();
                entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
                entity.Property(x => x.ChannelLogin).HasMaxLength(25);
                entity.Property(x => x.Title).HasMaxLength(500);
                entity.Property(x => x.SourceQuality).HasMaxLength(64);
                entity.HasIndex(x => new { x.Status, x.NextCheckAtUtc });
            });

            modelBuilder.Entity<RescuedAudioSegment>(entity =>
            {
                entity.Property(x => x.Key).HasMaxLength(64);
                // SQLite stores decimals as TEXT; these are small segment times, so a double column is fine and sortable
                entity.Property(x => x.OffsetSeconds).HasConversion<double>();
                entity.Property(x => x.DurationSeconds).HasConversion<double>();
                entity.HasIndex(x => new { x.VodId, x.Key }).IsUnique();
            });
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            FillOwnershipAsync(CancellationToken.None).GetAwaiter().GetResult();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            await FillOwnershipAsync(cancellationToken);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        /// <summary>
        /// New entries/channels go to the request's team and user; new audit events get the request's user and team, or for
        /// background jobs the team of the entry they're about. Done here so no call site can forget it.
        /// </summary>
        private async Task FillOwnershipAsync(CancellationToken cancellationToken)
        {
            var userId = _actor.UserId?.ToString();
            foreach (var added in ChangeTracker.Entries().Where(x => x.State == EntityState.Added))
            {
                switch (added.Entity)
                {
                    case LibraryEntry entry:
                        if (entry.TeamId == 0 && _actor.TeamId is { } entryTeam) entry.TeamId = entryTeam;
                        entry.AddedByUserId ??= userId;
                        break;
                    case SavedChannel channel:
                        if (channel.TeamId == 0 && _actor.TeamId is { } channelTeam) channel.TeamId = channelTeam;
                        channel.AddedByUserId ??= userId;
                        break;
                }
            }

            var audits = ChangeTracker.Entries<AuditEvent>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToList();
            if (audits.Count == 0)
                return;

            foreach (var audit in audits)
            {
                audit.UserId ??= userId;
                audit.StaffId ??= _actor.StaffId;
                audit.TeamId ??= _actor.TeamId;
            }

            var entryIds = audits.Where(x => x.TeamId is null && x.EntryId is not null).Select(x => x.EntryId!.Value).Distinct().ToList();
            if (entryIds.Count == 0)
                return;

            var teams = Entries.Local.Where(x => entryIds.Contains(x.Id)).ToDictionary(x => x.Id, x => x.TeamId);
            var missing = entryIds.Where(x => !teams.ContainsKey(x)).ToList();
            if (missing.Count > 0)
            {
                foreach (var row in await Entries.IgnoreQueryFilters().AsNoTracking().Where(x => missing.Contains(x.Id))
                             .Select(x => new { x.Id, x.TeamId }).ToListAsync(cancellationToken))
                    teams[row.Id] = row.TeamId;
            }

            foreach (var audit in audits)
            {
                if (audit.TeamId is null && audit.EntryId is { } id && teams.TryGetValue(id, out var team))
                    audit.TeamId = team;
            }
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
