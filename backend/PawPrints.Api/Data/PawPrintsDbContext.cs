using Microsoft.EntityFrameworkCore;

namespace PawPrints.Api.Data;

public sealed class PawPrintsDbContext(DbContextOptions<PawPrintsDbContext> options) : DbContext(options)
{
    public DbSet<PawPrintsUser> Users => Set<PawPrintsUser>();

    public DbSet<PuppyEvent> Events => Set<PuppyEvent>();

    public DbSet<PawPrintsInvite> Invites => Set<PawPrintsInvite>();

    public DbSet<PuppyPrediction> Predictions => Set<PuppyPrediction>();

    public DbSet<NotificationOutboxItem> NotificationOutbox => Set<NotificationOutboxItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PawPrintsUser>(user =>
        {
            user.HasKey(storedUser => storedUser.Id);
            user.HasIndex(storedUser => storedUser.Email).IsUnique();
            user.Property(storedUser => storedUser.Email).HasMaxLength(320).IsRequired();
            user.Property(storedUser => storedUser.ExternalSubject).HasMaxLength(256).IsRequired();
            user.HasMany(storedUser => storedUser.Events)
                .WithOne(storedEvent => storedEvent.User)
                .HasForeignKey(storedEvent => storedEvent.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            user.HasMany(storedUser => storedUser.Predictions)
                .WithOne(storedPrediction => storedPrediction.User)
                .HasForeignKey(storedPrediction => storedPrediction.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            user.HasMany(storedUser => storedUser.NotificationOutboxItems)
                .WithOne(storedNotification => storedNotification.User)
                .HasForeignKey(storedNotification => storedNotification.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            user.HasOne(storedUser => storedUser.CollaboratesWith)
                .WithMany(owner => owner.Collaborators)
                .HasForeignKey(storedUser => storedUser.CollaboratesWithUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PawPrintsInvite>(invite =>
        {
            invite.HasKey(storedInvite => storedInvite.Id);
            invite.HasIndex(storedInvite => storedInvite.TokenHash).IsUnique();
            invite.Property(storedInvite => storedInvite.TokenHash).HasMaxLength(64).IsRequired();
            invite
                .HasOne(storedInvite => storedInvite.Owner)
                .WithMany()
                .HasForeignKey(storedInvite => storedInvite.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
            invite
                .HasOne(storedInvite => storedInvite.ConsumedBy)
                .WithMany()
                .HasForeignKey(storedInvite => storedInvite.ConsumedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PuppyEvent>(puppyEvent =>
        {
            puppyEvent.HasKey(storedEvent => storedEvent.Id);
            puppyEvent.HasIndex(storedEvent => new { storedEvent.UserId, storedEvent.ClientEventId })
                .IsUnique();
            puppyEvent.Property(storedEvent => storedEvent.ClientEventId).HasMaxLength(160).IsRequired();
            puppyEvent.Property(storedEvent => storedEvent.Type).HasMaxLength(40).IsRequired();
            puppyEvent.Property(storedEvent => storedEvent.DateKey).HasMaxLength(10).IsRequired();
        });

        modelBuilder.Entity<PuppyPrediction>(prediction =>
        {
            prediction.HasKey(storedPrediction => storedPrediction.Id);
            prediction.HasIndex(storedPrediction => new
            {
                storedPrediction.UserId,
                storedPrediction.Type,
                storedPrediction.Status,
            });
            prediction.HasIndex(storedPrediction => storedPrediction.TriggerEventClientId);
            prediction.Property(storedPrediction => storedPrediction.Type).HasMaxLength(40).IsRequired();
            prediction.Property(storedPrediction => storedPrediction.Status).HasMaxLength(24).IsRequired();
            prediction.Property(storedPrediction => storedPrediction.TriggerEventClientId).HasMaxLength(160);
        });

        modelBuilder.Entity<NotificationOutboxItem>(notification =>
        {
            notification.HasKey(storedNotification => storedNotification.Id);
            notification.HasIndex(storedNotification => new
            {
                storedNotification.UserId,
                storedNotification.SendAfterUtc,
            });
            notification.HasIndex(storedNotification => storedNotification.PredictionId);
            notification.Property(storedNotification => storedNotification.Type).HasMaxLength(40).IsRequired();
            notification.Property(storedNotification => storedNotification.Title).HasMaxLength(120).IsRequired();
            notification.Property(storedNotification => storedNotification.Body).HasMaxLength(500).IsRequired();
            notification
                .HasOne(storedNotification => storedNotification.Prediction)
                .WithMany(storedPrediction => storedPrediction.Notifications)
                .HasForeignKey(storedNotification => storedNotification.PredictionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
