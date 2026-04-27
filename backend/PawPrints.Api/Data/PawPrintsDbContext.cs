using Microsoft.EntityFrameworkCore;

namespace PawPrints.Api.Data;

public sealed class PawPrintsDbContext(DbContextOptions<PawPrintsDbContext> options) : DbContext(options)
{
    public DbSet<PawPrintsUser> Users => Set<PawPrintsUser>();

    public DbSet<PuppyEvent> Events => Set<PuppyEvent>();

    public DbSet<PawPrintsInvite> Invites => Set<PawPrintsInvite>();

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
    }
}
