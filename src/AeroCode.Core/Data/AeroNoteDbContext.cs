using AeroCode.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AeroCode.Core.Data;

/// <summary>
/// EF Core DbContext。配置 Note/Notebook/Tag/NoteTag 实体映射，
/// 启用 FTS5 全文索引虚表（搜索服务用）。
/// </summary>
public class AeroCodeDbContext : DbContext
{
    public AeroCodeDbContext(DbContextOptions<AeroCodeDbContext> options) : base(options)
    {
    }

    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Notebook> Notebooks => Set<Notebook>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<NoteTag> NoteTags => Set<NoteTag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Note>(e =>
        {
            e.ToTable("notes");
            e.HasKey(n => n.Id);
            e.Property(n => n.Title).IsRequired().HasMaxLength(500);
            e.Property(n => n.Content).HasColumnType("TEXT");
            e.HasIndex(n => n.UpdatedAt);
            e.HasIndex(n => n.IsDeleted);
            e.HasIndex(n => n.IsPinned);
            e.HasOne(n => n.Notebook)
                .WithMany(nb => nb.Notes)
                .HasForeignKey(n => n.NotebookId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Notebook>(e =>
        {
            e.ToTable("notebooks");
            e.HasKey(nb => nb.Id);
            e.Property(nb => nb.Name).IsRequired().HasMaxLength(200);
            e.HasOne(nb => nb.Parent)
                .WithMany(nb => nb.Children)
                .HasForeignKey(nb => nb.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(nb => nb.SortOrder);
        });

        modelBuilder.Entity<Tag>(e =>
        {
            e.ToTable("tags");
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).IsRequired().HasMaxLength(50);
            e.Property(t => t.Color).HasMaxLength(9);
            e.HasIndex(t => t.Name).IsUnique();
        });

        modelBuilder.Entity<NoteTag>(e =>
        {
            e.ToTable("note_tags");
            e.HasKey(nt => new { nt.NoteId, nt.TagId });
            e.HasOne(nt => nt.Note)
                .WithMany(n => n.NoteTags)
                .HasForeignKey(nt => nt.NoteId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(nt => nt.Tag)
                .WithMany(t => t.NoteTags)
                .HasForeignKey(nt => nt.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
