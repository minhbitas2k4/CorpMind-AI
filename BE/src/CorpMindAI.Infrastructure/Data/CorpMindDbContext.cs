using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<UserRole>()
                .HasKey(ur => new { ur.UserId, ur.RoleId, ur.DepartmentId });

            modelBuilder.Entity<Department>().HasIndex(d => d.Name).IsUnique();
            modelBuilder.Entity<Role>().HasIndex(r => r.RoleName).IsUnique();
            modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();

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
        }
    }
}
