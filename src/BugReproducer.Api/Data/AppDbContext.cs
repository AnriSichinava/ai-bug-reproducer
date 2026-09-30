using BugReproducer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BugReproducer.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<BugReport> BugReports => Set<BugReport>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentStep> AgentSteps => Set<AgentStep>();
    public DbSet<GeneratedTest> GeneratedTests => Set<GeneratedTest>();
    public DbSet<TestRun> TestRuns => Set<TestRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<Project>()
            .HasOne(p => p.Owner)
            .WithMany()
            .HasForeignKey(p => p.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<BugReport>()
            .HasOne(b => b.Project)
            .WithMany(p => p.BugReports)
            .HasForeignKey(b => b.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AgentRun>(run =>
        {
            run.HasOne(r => r.BugReport)
                .WithMany()
                .HasForeignKey(r => r.BugReportId)
                .OnDelete(DeleteBehavior.Cascade);

            // Stored as strings so the database stays readable and reordering the enum
            // can't silently change the meaning of existing rows.
            run.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            run.Property(r => r.Verdict).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<AgentStep>(step =>
        {
            step.HasOne(s => s.AgentRun)
                .WithMany(r => r.Steps)
                .HasForeignKey(s => s.AgentRunId)
                .OnDelete(DeleteBehavior.Cascade);

            step.HasIndex(s => new { s.AgentRunId, s.StepNumber }).IsUnique();
        });

        modelBuilder.Entity<GeneratedTest>()
            .HasOne(g => g.AgentRun)
            .WithMany()
            .HasForeignKey(g => g.AgentRunId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TestRun>()
            .HasOne(t => t.GeneratedTest)
            .WithMany(g => g.TestRuns)
            .HasForeignKey(t => t.GeneratedTestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
