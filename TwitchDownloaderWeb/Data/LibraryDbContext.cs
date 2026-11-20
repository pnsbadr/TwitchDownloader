using TwitchDownloaderWeb.Services.Projects;
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
        public DbSet<ErrorReport> ErrorReports => Set<ErrorReport>();
        public DbSet<SavedChannel> Channels => Set<SavedChannel>();
        public DbSet<VodAudioRescue> AudioRescues => Set<VodAudioRescue>();
        public DbSet<RescuedAudioSegment> RescuedAudioSegments => Set<RescuedAudioSegment>();
        public DbSet<UserAccount> Users => Set<UserAccount>();
        public DbSet<Team> Teams => Set<Team>();
        public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
        public DbSet<StaffAccount> StaffAccounts => Set<StaffAccount>();
        public DbSet<AccountLink> AccountLinks => Set<AccountLink>();
        public DbSet<TeamRole> TeamRoles => Set<TeamRole>();
        public DbSet<VodMarker> Markers => Set<VodMarker>();
        public DbSet<TimelineRow> TimelineRows => Set<TimelineRow>();
        public DbSet<MemberVod> MemberVods => Set<MemberVod>();
        public DbSet<Video> Videos => Set<Video>();
        public DbSet<VideoSegment> VideoSegments => Set<VideoSegment>();
        public DbSet<SegmentExport> SegmentExports => Set<SegmentExport>();
        public DbSet<SegmentExportItem> SegmentExportItems => Set<SegmentExportItem>();
        public DbSet<ExportBatch> ExportBatches => Set<ExportBatch>();
        public DbSet<ExportItem> ExportItems => Set<ExportItem>();
        public DbSet<RevokedChannel> RevokedChannels => Set<RevokedChannel>();
        public DbSet<HiddenVod> HiddenVods => Set<HiddenVod>();
        public DbSet<ItemEdit> ItemEdits => Set<ItemEdit>();
        public DbSet<ItemApply> ItemApplies => Set<ItemApply>();
        public DbSet<EditProject> Projects => Set<EditProject>();
        public DbSet<ProjectVod> ProjectVods => Set<ProjectVod>();
        public DbSet<TwitchConnection> TwitchConnections => Set<TwitchConnection>();
        public DbSet<AppSettings> AppSettings => Set<AppSettings>();
        public DbSet<VodSyncOffset> VodSyncOffsets => Set<VodSyncOffset>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppSettings>(entity =>
            {
                entity.Property(x => x.Id).ValueGeneratedNever();
                entity.Property(x => x.Name).HasMaxLength(Endpoints.AppSettingsRules.MaxNameLength);
                entity.Property(x => x.XUrl).HasMaxLength(Endpoints.AppSettingsRules.MaxUrlLength);
                entity.Property(x => x.DiscordUrl).HasMaxLength(Endpoints.AppSettingsRules.MaxUrlLength);
            });

            modelBuilder.Entity<TwitchConnection>(entity =>
            {
                entity.Property(x => x.Id).ValueGeneratedNever();
                entity.Property(x => x.ClientId).HasMaxLength(100);
                entity.Property(x => x.TwitchUserId).HasMaxLength(32);
                entity.Property(x => x.TwitchLogin).HasMaxLength(32);
                entity.Property(x => x.Problem).HasMaxLength(500);
            });

            modelBuilder.Entity<LibraryEntry>(entity =>
            {
                entity.ToTable("LibraryEntries");
                entity.Property(x => x.Title).HasMaxLength(500);
                entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
                entity.Property(x => x.GenerationStatus).HasConversion<string>().HasMaxLength(32);
                entity.Property(x => x.TrimMode).HasConversion<string>().HasMaxLength(16);
                entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
                entity.Property(x => x.Color).HasMaxLength(16);
                entity.HasOne(x => x.Row).WithMany().HasForeignKey(x => x.RowId).OnDelete(DeleteBehavior.SetNull);
                entity.Property(x => x.ClipSlug).HasMaxLength(200);
                entity.HasIndex(x => x.StreamDateUtc);
                entity.HasIndex(x => x.AddedAtUtc);
                entity.HasIndex(x => x.Status);
                entity.HasIndex(x => x.TeamId);
                // The timeline, live markers entry and channel checks read a team's entries on one VOD
                entity.HasIndex(x => new { x.TeamId, x.VodId });
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
                // A request sees only its own team's library; requests without a resolved team see nothing. Work on VODs of a channel
                // revoked from the team is hidden (old Twitch clips without a source VOD go by their channel name)
                entity.HasQueryFilter(x => !ScopedToTeam || (x.TeamId == ScopeTeamId
                    && !HiddenVods.Any(h => h.TeamId == ScopeTeamId && h.VodId == x.VodId)
                    && !(x.VodId == 0 && x.ChannelName != null && RevokedChannels.Any(c => c.TeamId == ScopeTeamId
                        && (c.Login == x.ChannelName.ToLower() || c.DisplayName.ToLower() == x.ChannelName.ToLower())))));
            });

            modelBuilder.Entity<ErrorReport>(entity =>
            {
                entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
                entity.Property(x => x.Area).HasMaxLength(200);
                entity.Property(x => x.ErrorType).HasMaxLength(200);
                entity.Property(x => x.Message).HasMaxLength(ErrorReport.MaxMessageLength);
                entity.Property(x => x.Details).HasMaxLength(ErrorReport.MaxDetailsLength);
                entity.Property(x => x.Note).HasMaxLength(ErrorReport.MaxNoteLength);
                entity.Property(x => x.Username).HasMaxLength(64);
                entity.Property(x => x.TeamName).HasMaxLength(100);
                entity.Property(x => x.StaffUsername).HasMaxLength(64);
                entity.Property(x => x.Url).HasMaxLength(1000);
                entity.Property(x => x.Browser).HasMaxLength(60);
                entity.Property(x => x.Os).HasMaxLength(60);
                entity.Property(x => x.UserAgent).HasMaxLength(500);
                entity.Property(x => x.Context).HasMaxLength(ErrorReport.MaxContextLength);
                entity.Property(x => x.AppVersion).HasMaxLength(60);
                entity.Property(x => x.Fingerprint).HasMaxLength(16);
                entity.Property(x => x.RequestId).HasMaxLength(100);
                entity.HasIndex(x => x.CreatedAtUtc);
                entity.HasIndex(x => x.UserId);
                entity.HasIndex(x => x.TeamId);
                entity.HasIndex(x => x.Fingerprint);
            });

            modelBuilder.Entity<AuditEvent>(entity =>
            {
                entity.Property(x => x.Action).HasMaxLength(64);
                entity.HasIndex(x => x.TimestampUtc);
                entity.HasIndex(x => x.EntryId);
                entity.HasIndex(x => x.TeamId);
                // The team log: a team's newest events first
                entity.HasIndex(x => new { x.TeamId, x.TimestampUtc });
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

            modelBuilder.Entity<VodMarker>(entity =>
            {
                entity.ToTable("VodMarkers");
                entity.Property(x => x.Label).HasMaxLength(VodMarker.MaxLabelLength);
                entity.Property(x => x.Color).HasMaxLength(16);
                entity.HasIndex(x => new { x.TeamId, x.VodId });
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                // A row is only deleted after its markers (TimelineEndpoints); SetNull is a safety net
                entity.HasOne(x => x.Row).WithMany().HasForeignKey(x => x.RowId).OnDelete(DeleteBehavior.SetNull);
                entity.HasQueryFilter(x => !ScopedToTeam || (x.TeamId == ScopeTeamId && !HiddenVods.Any(h => h.TeamId == ScopeTeamId && h.VodId == x.VodId)));
            });

            modelBuilder.Entity<TimelineRow>(entity =>
            {
                entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
                entity.Property(x => x.Name).HasMaxLength(TimelineRow.MaxNameLength);
                entity.HasIndex(x => new { x.TeamId, x.VodId, x.UserId });
                entity.HasIndex(x => new { x.TeamId, x.ShownOnVodId });
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasQueryFilter(x => !ScopedToTeam || (x.TeamId == ScopeTeamId && !HiddenVods.Any(h => h.TeamId == ScopeTeamId && h.VodId == x.VodId)));
            });

            modelBuilder.Entity<Video>(entity =>
            {
                entity.Property(x => x.Title).HasMaxLength(Video.MaxTitleLength);
                entity.Property(x => x.ChannelName).HasMaxLength(100);
                entity.HasIndex(x => new { x.TeamId, x.VodId });
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<TimelineRow>().WithMany().HasForeignKey(x => x.RowId).OnDelete(DeleteBehavior.SetNull);
                entity.HasMany(x => x.Segments).WithOne().HasForeignKey(x => x.VideoId).OnDelete(DeleteBehavior.Cascade);
                entity.HasQueryFilter(x => !ScopedToTeam || (x.TeamId == ScopeTeamId && !HiddenVods.Any(h => h.TeamId == ScopeTeamId && h.VodId == x.VodId)));
            });

            modelBuilder.Entity<VideoSegment>(entity =>
            {
                entity.HasIndex(x => new { x.VideoId, x.MarkerId }).IsUnique();
                entity.HasIndex(x => x.MarkerId);
                entity.HasOne(x => x.Marker).WithMany().HasForeignKey(x => x.MarkerId).OnDelete(DeleteBehavior.Cascade);
                // Same team as its segment (matches the markers' filter)
                entity.HasQueryFilter(x => !ScopedToTeam || x.Marker!.TeamId == ScopeTeamId);
            });

            modelBuilder.Entity<VodSyncOffset>(entity =>
            {
                entity.HasIndex(x => new { x.TeamId, x.VodA, x.VodB }).IsUnique();
                entity.Property(x => x.Source).HasMaxLength(16);
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasQueryFilter(x => !ScopedToTeam || x.TeamId == ScopeTeamId);
            });

            modelBuilder.Entity<MemberVod>(entity =>
            {
                entity.HasIndex(x => new { x.TeamId, x.UserId, x.VodId }).IsUnique();
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasQueryFilter(x => !ScopedToTeam || x.TeamId == ScopeTeamId);
            });

            modelBuilder.Entity<SegmentExport>(entity =>
            {
                entity.Property(x => x.Title).HasMaxLength(500);
                entity.Property(x => x.Quality).HasMaxLength(64);
                entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
                entity.HasIndex(x => new { x.TeamId, x.VodId });
                entity.HasIndex(x => x.Status);
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ExportId).OnDelete(DeleteBehavior.Cascade);
                entity.HasQueryFilter(x => !ScopedToTeam || (x.TeamId == ScopeTeamId && !HiddenVods.Any(h => h.TeamId == ScopeTeamId && h.VodId == x.VodId)));
            });

            modelBuilder.Entity<SegmentExportItem>(entity =>
            {
                entity.Property(x => x.Label).HasMaxLength(VodMarker.MaxLabelLength);
                entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
                entity.HasIndex(x => new { x.ExportId, x.Position }).IsUnique();
            });

            modelBuilder.Entity<ExportBatch>(entity =>
            {
                entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
                entity.HasIndex(x => new { x.TeamId, x.CreatedByUserId });
                entity.HasIndex(x => x.Status);
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
                // An export with anything from a hidden VOD is hidden whole
                entity.HasQueryFilter(x => !ScopedToTeam || (x.TeamId == ScopeTeamId
                    && !x.Items.Any(i => HiddenVods.Any(h => h.TeamId == ScopeTeamId && h.VodId == i.VodId))));
            });

            modelBuilder.Entity<ExportItem>(entity =>
            {
                entity.Property(x => x.FileName).HasMaxLength(ExportItem.MaxFileNameLength);
                entity.Property(x => x.Quality).HasMaxLength(64);
                entity.Property(x => x.RenderId).HasMaxLength(32);
                entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
                entity.Property(x => x.TrimMode).HasConversion<string>().HasMaxLength(16);
                entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
                entity.HasIndex(x => new { x.BatchId, x.Position }).IsUnique();
            });

            modelBuilder.Entity<ItemEdit>(entity =>
            {
                entity.Property(x => x.Title).HasMaxLength(ItemEdit.MaxTitleLength);
                entity.Property(x => x.Layout).HasMaxLength(ItemEdit.MaxLayoutLength);
                entity.Ignore(x => x.Kind);
                entity.HasIndex(x => x.EntryId);
                entity.HasIndex(x => x.MarkerId);
                entity.HasIndex(x => x.VideoId);
                entity.HasIndex(x => new { x.TeamId, x.VodId });
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<LibraryEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<VodMarker>().WithMany().HasForeignKey(x => x.MarkerId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<Video>().WithMany().HasForeignKey(x => x.VideoId).OnDelete(DeleteBehavior.Cascade);
                entity.HasQueryFilter(x => !ScopedToTeam || (x.TeamId == ScopeTeamId && !HiddenVods.Any(h => h.TeamId == ScopeTeamId && h.VodId == x.VodId)));
            });

            modelBuilder.Entity<ItemApply>(entity =>
            {
                entity.Property(x => x.EditTitle).HasMaxLength(ItemEdit.MaxTitleLength);
                entity.HasIndex(x => x.EntryId);
                entity.HasIndex(x => x.MarkerId);
                entity.HasIndex(x => x.VideoId);
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<LibraryEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<VodMarker>().WithMany().HasForeignKey(x => x.MarkerId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<Video>().WithMany().HasForeignKey(x => x.VideoId).OnDelete(DeleteBehavior.Cascade);
                entity.HasQueryFilter(x => !ScopedToTeam || (x.TeamId == ScopeTeamId && !HiddenVods.Any(h => h.TeamId == ScopeTeamId && h.VodId == x.VodId)));
            });

            modelBuilder.Entity<EditProject>(entity =>
            {
                entity.ToTable("Projects");
                entity.Property(x => x.Title).HasMaxLength(EditProject.MaxTitleLength);
                entity.Property(x => x.Content).HasMaxLength(EditProject.MaxContentLength);
                // Two saves of one revision at once: the second one's update finds no row and is refused (409 project-changed)
                entity.Property(x => x.Revision).IsConcurrencyToken();
                entity.HasIndex(x => new { x.TeamId, x.UserId });
                entity.HasIndex(x => new { x.TeamId, x.VodId });
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne<LibraryEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.SetNull);
                entity.HasOne<Video>().WithMany().HasForeignKey(x => x.VideoId).OnDelete(DeleteBehavior.SetNull);
                entity.HasMany(x => x.Vods).WithOne().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
                // A project using any VOD hidden from the team is hidden whole (like exports)
                entity.HasQueryFilter(x => !ScopedToTeam || (x.TeamId == ScopeTeamId
                    && !x.Vods.Any(v => HiddenVods.Any(h => h.TeamId == ScopeTeamId && h.VodId == v.VodId))));
            });

            modelBuilder.Entity<ProjectVod>(entity =>
            {
                entity.HasIndex(x => new { x.ProjectId, x.VodId }).IsUnique();
                entity.HasIndex(x => x.VodId);
            });

            modelBuilder.Entity<RevokedChannel>(entity =>
            {
                entity.Property(x => x.Login).HasMaxLength(25);
                entity.Property(x => x.DisplayName).HasMaxLength(100);
                entity.Property(x => x.TwitchUserId).HasMaxLength(32);
                entity.HasIndex(x => x.TeamId);
                entity.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
                entity.HasMany(x => x.HiddenVods).WithOne().HasForeignKey(x => x.RevokedChannelId).OnDelete(DeleteBehavior.Cascade);
                // No query filter: the other filters read it, and only staff code and those filters use it
            });

            modelBuilder.Entity<HiddenVod>(entity =>
            {
                entity.HasIndex(x => new { x.TeamId, x.VodId });
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
                entity.Property(x => x.ChannelsJson).HasMaxLength(8000);
                entity.Property(x => x.TeamName).HasMaxLength(100);
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

        public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
            SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            await FillOwnershipAsync(cancellationToken);
            // Clips and segments whose kept range changes: Player 2 edits made from them follow (ProjectSourceSync)
            var sources = ProjectSourceSync.Collect(this);
            var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (sources.Count > 0 && await ProjectSourceSync.ApplyAsync(this, sources, cancellationToken) is { } change)
                _actor.SourceSyncs.Add(change);
            return saved;
        }

        /// <summary>
        /// New entries/channels go to the request's team and user; new audit events get the request's user and team, or for
        /// background jobs the team of the entry they're about. Done here so no call site can forget it.
        /// </summary>
        private async Task FillOwnershipAsync(CancellationToken cancellationToken)
        {
            var userId = _actor.UserId?.ToString();
            foreach (var added in ChangeTracker.Entries().Where(x => x.State == EntityState.Added).ToList())
            {
                switch (added.Entity)
                {
                    case LibraryEntry entry:
                        if (entry.TeamId == 0 && _actor.TeamId is { } entryTeam) entry.TeamId = entryTeam;
                        entry.AddedByUserId ??= userId;
                        if (entry.RowId is null && entry.Row is null && entry.VodId > 0 && int.TryParse(entry.AddedByUserId, out var entryUser) && entryUser > 0)
                            entry.Row = await DefaultRowAsync(entry.TeamId, entry.VodId, entryUser, TimelineRowKind.Clip, cancellationToken);
                        break;
                    case VodMarker marker:
                        if (marker.TeamId == 0 && _actor.TeamId is { } markerTeam) marker.TeamId = markerTeam;
                        if (marker.CreatedByUserId == 0 && _actor.UserId is { } markerUser) marker.CreatedByUserId = markerUser;
                        if (marker.RowId is null && marker.Row is null && marker.CreatedByUserId > 0)
                            marker.Row = await DefaultRowAsync(marker.TeamId, marker.VodId, marker.CreatedByUserId, TimelineRowKind.Video, cancellationToken);
                        break;
                    case TimelineRow row:
                        if (row.TeamId == 0 && _actor.TeamId is { } rowTeam) row.TeamId = rowTeam;
                        break;
                    case Video video:
                        if (video.TeamId == 0 && _actor.TeamId is { } videoTeam) video.TeamId = videoTeam;
                        break;
                    case VodSyncOffset syncOffset:
                        if (syncOffset.TeamId == 0 && _actor.TeamId is { } syncTeam) syncOffset.TeamId = syncTeam;
                        break;
                    case MemberVod memberVod:
                        if (memberVod.TeamId == 0 && _actor.TeamId is { } memberVodTeam) memberVod.TeamId = memberVodTeam;
                        break;
                    case SegmentExport export:
                        if (export.TeamId == 0 && _actor.TeamId is { } exportTeam) export.TeamId = exportTeam;
                        if (export.CreatedByUserId == 0 && _actor.UserId is { } exportUser) export.CreatedByUserId = exportUser;
                        break;
                    case ExportBatch batch:
                        if (batch.TeamId == 0 && _actor.TeamId is { } batchTeam) batch.TeamId = batchTeam;
                        if (batch.CreatedByUserId == 0 && _actor.UserId is { } batchUser) batch.CreatedByUserId = batchUser;
                        break;
                    case ItemEdit edit:
                        if (edit.TeamId == 0 && _actor.TeamId is { } editTeam) edit.TeamId = editTeam;
                        if (edit.CreatedByUserId == 0 && _actor.UserId is { } editUser) edit.CreatedByUserId = editUser;
                        break;
                    case EditProject project:
                        if (project.TeamId == 0 && _actor.TeamId is { } projectTeam) project.TeamId = projectTeam;
                        if (project.UserId == 0 && _actor.UserId is { } projectUser) project.UserId = projectUser;
                        break;
                    case ItemApply apply:
                        if (apply.TeamId == 0 && _actor.TeamId is { } applyTeam) apply.TeamId = applyTeam;
                        if (apply.UserId == 0 && _actor.UserId is { } applyUser) apply.UserId = applyUser;
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

        /// <summary>
        /// The member's first row of <paramref name="kind"/> on the VOD (lowest position), made if they have none. Rows added in this
        /// same save count too, so several new items share one new row.
        /// </summary>
        private async Task<TimelineRow> DefaultRowAsync(int teamId, long vodId, int userId, TimelineRowKind kind, CancellationToken cancellationToken)
        {
            // Plain rows first: a synced row (shown on another VOD) is used only when it's the member's only row of that kind
            bool Matches(TimelineRow r) => r.TeamId == teamId && r.VodId == vodId && r.UserId == userId && r.Kind == kind;

            var local = TimelineRows.Local.Where(Matches).OrderBy(x => x.ShownOnVodId != null).ThenBy(x => x.Position).ThenBy(x => x.Id).FirstOrDefault();
            if (local is not null)
                return local;

            var existing = await TimelineRows.IgnoreQueryFilters()
                .Where(x => x.TeamId == teamId && x.VodId == vodId && x.UserId == userId && x.Kind == kind)
                .OrderBy(x => x.ShownOnVodId != null).ThenBy(x => x.Position).ThenBy(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
                return existing;

            var positions = await TimelineRows.IgnoreQueryFilters()
                .Where(x => x.TeamId == teamId && x.VodId == vodId && x.UserId == userId)
                .Select(x => (int?)x.Position).MaxAsync(cancellationToken);
            var row = new TimelineRow
            {
                TeamId = teamId, VodId = vodId, UserId = userId, Kind = kind,
                Position = (positions ?? -1) + 1, CreatedAtUtc = DateTime.UtcNow,
            };
            TimelineRows.Add(row);
            return row;
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
