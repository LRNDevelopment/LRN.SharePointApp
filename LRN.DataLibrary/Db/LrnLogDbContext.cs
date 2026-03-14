using LRN.DataLibrary.Entities;
using Microsoft.EntityFrameworkCore;

namespace LRN.DataLibrary.Db;

public sealed class LrnLogDbContext : DbContext
{
    public LrnLogDbContext(DbContextOptions<LrnLogDbContext> options) : base(options) { }

    public DbSet<LrnRunLog> RunLogs => Set<LrnRunLog>();
    public DbSet<LrnStepLog> StepLogs => Set<LrnStepLog>();
    public DbSet<LrnErrorLog> ErrorLogs => Set<LrnErrorLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // These table names match your naming in other chats.
        modelBuilder.Entity<LrnRunLog>(b =>
        {
            b.ToTable("LRN_Run_Log");
            b.HasKey(x => x.RunID);
            b.Property(x => x.RunID).ValueGeneratedOnAdd();
            b.Property(x => x.LabName).HasMaxLength(200);
            b.Property(x => x.SourceSystem).HasMaxLength(100);
            b.Property(x => x.OverallStatus).HasMaxLength(50);
            b.Property(x => x.MandatoryColumnCheck).HasMaxLength(50);
            b.Property(x => x.SplitOutputWrittenToSharePoint).HasMaxLength(50);
            b.Property(x => x.PayerPolicyValidationStatus).HasMaxLength(50);
            b.Property(x => x.CodingValidationStatus).HasMaxLength(50);
            b.Property(x => x.AveragesProcessStatus).HasMaxLength(50);
            b.Property(x => x.OutputsCopiedToSharePoint).HasMaxLength(50);
        });

        modelBuilder.Entity<LrnStepLog>(b =>
        {
            b.ToTable("LRN_STEP_LOG");
            b.HasKey(x => x.StepLogId);
            b.Property(x => x.StepLogId).ValueGeneratedOnAdd();
            b.Property(x => x.LabName).HasMaxLength(200);
            b.Property(x => x.StepName).HasMaxLength(200);
            b.Property(x => x.StepCategory).HasMaxLength(200);
            b.Property(x => x.SourceSystem).HasMaxLength(100);
            b.Property(x => x.Status).HasMaxLength(50);
            b.Property(x => x.FileNameIn).HasMaxLength(260);
            b.Property(x => x.FileNameOut).HasMaxLength(260);
            b.Property(x => x.PathIn).HasMaxLength(1000);
            b.Property(x => x.PathOut).HasMaxLength(1000);
            b.Property(x => x.ErrorCode).HasMaxLength(50);
            b.Property(x => x.Host).HasMaxLength(128);
            b.Property(x => x.ExecutedBy).HasMaxLength(128);
            b.Property(x => x.ModuleVersion).HasMaxLength(50);
        });

        modelBuilder.Entity<LrnErrorLog>(b =>
        {
            b.ToTable("LRN_Error_Log");
            b.HasKey(x => x.ErrorLogId);
            b.Property(x => x.ErrorLogId).ValueGeneratedOnAdd();
            b.Property(x => x.LabName).HasMaxLength(200);
            b.Property(x => x.SourceSystem).HasMaxLength(100);
            b.Property(x => x.ErrorCode).HasMaxLength(50);
            b.Property(x => x.ErrorMessage).HasMaxLength(4000);
            b.Property(x => x.ErrorDetail).HasMaxLength(4000);
            b.Property(x => x.Host).HasMaxLength(128);
            b.Property(x => x.ExecutedBy).HasMaxLength(128);
        });

        base.OnModelCreating(modelBuilder);
    }
}
