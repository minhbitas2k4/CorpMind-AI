using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using CorpMindAI.Domain.Entities;
using CorpMindAI.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CorpMindAI.Infrastructure.Data
{
    public class CorpMindDbContext : DbContext
    {
        public CorpMindDbContext(DbContextOptions options) : base(options)
        {
        }

        public DbSet<Department> Departments { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<UserRole> UserRoles { get; set; }
        public DbSet<Curriculum> Curriculums { get; set; }
        public DbSet<Document> Documents { get; set; }
        public DbSet<OcrResult> OcrResults { get; set; }
        public DbSet<ChatSession> ChatSessions { get; set; }
        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<AiKnowledgeConflict> AiKnowledgeConflicts { get; set; }
        public DbSet<ActivityLog> ActivityLogs { get; set; }
        public DbSet<ChunkingRun> ChunkingRuns { get; set; }
        public DbSet<ParentChunk> ParentChunks { get; set; }
        public DbSet<ChildChunk> ChildChunks { get; set; }
        public DbSet<ChunkingOutboxMessage> ChunkingOutboxMessages { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<UserRole>()
                .HasKey(ur => new { ur.UserId, ur.RoleId, ur.DepartmentId });

            modelBuilder.Entity<Department>().HasIndex(d => d.Name).IsUnique();
            modelBuilder.Entity<Role>().HasIndex(r => r.RoleName).IsUnique();
            modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
            modelBuilder.Entity<User>().Property(u => u.TokenVersion).HasDefaultValue(0);

            modelBuilder.Entity<AiKnowledgeConflict>()
                .HasOne(c => c.SourceDocument)
                .WithMany(d => d.SourceConflicts)
                .HasForeignKey(c => c.SourceDocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AiKnowledgeConflict>()
                .HasOne(c => c.ConflictingDocument)
                .WithMany(d => d.ConflictingConflicts)
                .HasForeignKey(c => c.ConflictingDocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Document>()
                .HasOne(d => d.UploadedBy)
                .WithMany(u => u.UploadedDocuments)
                .HasForeignKey(d => d.UploadedById)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Document>()
                .HasOne(d => d.ApprovedBy)
                .WithMany(u => u.ApprovedDocuments)
                .HasForeignKey(d => d.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<OcrResult>()
                .HasOne(o => o.Document)
                .WithOne(d => d.OcrResult)
                .HasForeignKey<OcrResult>(o => o.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<OcrResult>()
                .HasIndex(o => o.DocumentId)
                .IsUnique();

            ConfigureChunking(modelBuilder);
            ConfigureChunkingOutbox(modelBuilder);
        }

        private static void ConfigureChunkingOutbox(ModelBuilder modelBuilder)
        {
            var message = modelBuilder.Entity<ChunkingOutboxMessage>();
            message.HasIndex(item => new { item.DocumentId, item.OcrPayloadHash }).IsUnique();
            message.HasIndex(item => new { item.DispatchedAt, item.NextAttemptAt });
            message.HasOne(item => item.Document)
                .WithMany()
                .HasForeignKey(item => item.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        }

        private static void ConfigureChunking(ModelBuilder modelBuilder)
        {
            var listConverter = new ValueConverter<List<string>, string>(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new());
            var listComparer = new ValueComparer<List<string>>(
                (left, right) => left != null && right != null && left.SequenceEqual(right),
                value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode(StringComparison.Ordinal))),
                value => value.ToList());

            var run = modelBuilder.Entity<ChunkingRun>();
            run.HasKey(item => item.Id);
            run.HasAlternateKey(item => new { item.Id, item.DocumentId });
            run.HasIndex(item => item.DocumentId);
            run.HasIndex(item => item.DocumentId)
                .IsUnique()
                .HasFilter("\"is_active\" = TRUE");
            run.Property(item => item.ConfigurationJson).HasColumnType("jsonb");
            run.Property(item => item.ValidationWarningsJson).HasColumnType("jsonb");
            run.Property(item => item.NormalizationNoticesJson).HasColumnType("jsonb");
            run.HasOne(item => item.Document)
                .WithMany(document => document.ChunkingRuns)
                .HasForeignKey(item => item.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            var parent = modelBuilder.Entity<ParentChunk>();
            parent.HasKey(item => item.Id);
            parent.HasAlternateKey(item => new { item.Id, item.ChunkingRunId, item.DocumentId });
            parent.HasIndex(item => item.DocumentId);
            parent.HasIndex(item => item.ChunkingRunId);
            parent.HasIndex(item => new { item.ChunkingRunId, item.Ordinal }).IsUnique();
            parent.Property(item => item.SectionPath)
                .HasConversion(listConverter)
                .Metadata.SetValueComparer(listComparer);
            parent.Property(item => item.ComponentIds)
                .HasConversion(listConverter)
                .Metadata.SetValueComparer(listComparer);
            parent.HasOne(item => item.ChunkingRun)
                .WithMany(runEntity => runEntity.ParentChunks)
                .HasForeignKey(item => new { item.ChunkingRunId, item.DocumentId })
                .HasPrincipalKey(runEntity => new { runEntity.Id, runEntity.DocumentId })
                .OnDelete(DeleteBehavior.Cascade);

            var child = modelBuilder.Entity<ChildChunk>();
            child.HasKey(item => item.Id);
            child.HasIndex(item => item.DocumentId);
            child.HasIndex(item => item.ChunkingRunId);
            child.HasIndex(item => item.ParentChunkId);
            child.HasIndex(item => new { item.ParentChunkId, item.Ordinal }).IsUnique();
            child.Property(item => item.SectionPath)
                .HasConversion(listConverter)
                .Metadata.SetValueComparer(listComparer);
            child.Property(item => item.ComponentIds)
                .HasConversion(listConverter)
                .Metadata.SetValueComparer(listComparer);
            child.HasOne(item => item.ParentChunk)
                .WithMany(parentEntity => parentEntity.ChildChunks)
                .HasForeignKey(item => new { item.ParentChunkId, item.ChunkingRunId, item.DocumentId })
                .HasPrincipalKey(parentEntity => new { parentEntity.Id, parentEntity.ChunkingRunId, parentEntity.DocumentId })
                .OnDelete(DeleteBehavior.Cascade);
            child.HasOne(item => item.ChunkingRun)
                .WithMany()
                .HasForeignKey(item => new { item.ChunkingRunId, item.DocumentId })
                .HasPrincipalKey(runEntity => new { runEntity.Id, runEntity.DocumentId })
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
