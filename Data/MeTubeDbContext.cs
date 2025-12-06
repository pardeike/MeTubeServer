using Microsoft.EntityFrameworkCore;
using MeTubeServer.Models;

namespace MeTubeServer.Data;

public class MeTubeDbContext : DbContext
{
    public MeTubeDbContext(DbContextOptions<MeTubeDbContext> options) : base(options)
    {
    }

    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserChannel> UserChannels => Set<UserChannel>();
    public DbSet<Video> Videos => Set<Video>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Channel configuration
        modelBuilder.Entity<Channel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ChannelId).IsUnique();
            entity.HasIndex(e => e.TopicUrl).IsUnique();
            entity.HasIndex(e => e.LeaseExpiresAt).HasDatabaseName("IX_Channels_LeaseExpiresAt");
            entity.Property(e => e.ChannelId).IsRequired();
            entity.Property(e => e.TopicUrl).IsRequired();
        });

        // User configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AppUserId).IsUnique();
            entity.Property(e => e.AppUserId).IsRequired();
        });

        // UserChannel configuration (many-to-many)
        modelBuilder.Entity<UserChannel>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.ChannelId });
            entity.HasIndex(e => new { e.UserId, e.ChannelId })
                .HasDatabaseName("IX_UserChannels_User_Channel");
            entity.HasOne(e => e.User)
                .WithMany(u => u.UserChannels)
                .HasForeignKey(e => e.UserId);
            entity.HasOne(e => e.Channel)
                .WithMany(c => c.UserChannels)
                .HasForeignKey(e => e.ChannelId);
        });

        // Video configuration
        modelBuilder.Entity<Video>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.VideoId).IsUnique();
            entity.HasIndex(e => new { e.ChannelId, e.PublishedAt })
                .HasDatabaseName("IX_Videos_Channel_Published");
            entity.Property(e => e.VideoId).IsRequired();
            entity.Property(e => e.PublishedAt).IsRequired();
            entity.HasOne(e => e.Channel)
                .WithMany(c => c.Videos)
                .HasForeignKey(e => e.ChannelId);
        });
    }
}
