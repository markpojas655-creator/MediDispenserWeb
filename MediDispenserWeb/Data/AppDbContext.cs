using Microsoft.EntityFrameworkCore;

namespace MediDispenserWeb.Data;

public class AppDbContext : DbContext
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<ScheduleItem> ScheduleItems => Set<ScheduleItem>();
    public DbSet<EventLog> EventLogs => Set<EventLog>();
    public DbSet<TestRun> TestRuns => Set<TestRun>();
    public DbSet<CalibrationHistory> CalibrationHistories => Set<CalibrationHistory>();
    public DbSet<Setting> Settings => Set<Setting>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Device>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.DeviceId).IsUnique();
            e.Property(d => d.DeviceId).HasMaxLength(64);
            e.Property(d => d.Name).HasMaxLength(128);
            e.Property(d => d.Location).HasMaxLength(256);
            e.Property(d => d.PatientName).HasMaxLength(128);
        });

        m.Entity<ScheduleItem>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.DeviceId, s.Compartment, s.Time });
        });

        m.Entity<EventLog>(e =>
        {
            e.HasKey(ev => ev.Id);
            e.HasIndex(ev => new { ev.DeviceId, ev.Time });
        });

        m.Entity<TestRun>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.DeviceId, t.Time });
        });

        m.Entity<CalibrationHistory>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.DeviceId, c.Time });
        });

        m.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(128);
        });
    }
}

public class Device
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public string? PatientName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ScheduleItem
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public int Compartment { get; set; }       // 1-8
    public string Time { get; set; } = "08:00"; // HH:mm
    public int DaysMask { get; set; } = 127;    // bit flags Mon=1..Sun=64, 127=every day
    public int Amount { get; set; } = 1;        // 1-3
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Version { get; set; }            // set by the service when publishing
}

public class EventLog
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "";      // dispensed, taken, returned, reminder, missed, empty, jam, command, info
    public int? Compartment { get; set; }
    public string Message { get; set; } = "";
    public string? Raw { get; set; }
    public bool Acknowledged { get; set; }
}

public class TestRun
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string ResultsJson { get; set; } = "[]"; // serialized list of test results
}

public class CalibrationHistory
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string ValuesJson { get; set; } = "{}";
    public string? ChangedBy { get; set; }
}

public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
