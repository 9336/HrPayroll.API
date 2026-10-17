using HrPayroll.BuildingBlocks.Data;
using HrPayroll.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Employees.Data;

/// <summary>
/// HrPayroll_Employees: the source of the company directory (employees, departments, holidays, company master), ID-card
/// photos and the documents employees upload. Directory changes are published for the other services' copies.
/// </summary>
public class EmployeesDbContext(DbContextOptions<EmployeesDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new(OwnsEmployees: true);

    protected override DirectoryModel Directory => Layout;

    public DbSet<EmployeePhoto> EmployeePhotos => Set<EmployeePhoto>();
    public DbSet<EmployeeUpload> EmployeeUploads => Set<EmployeeUpload>();
    public DbSet<EmployeeUploadFile> EmployeeUploadFiles => Set<EmployeeUploadFile>();
    public DbSet<SalarySnapshot> SalarySnapshots => Set<SalarySnapshot>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<EmployeePhoto>(e =>
        {
            e.ToTable("EmployeePhotos");
            e.HasKey(x => x.EmployeeId);
            e.Property(x => x.EmployeeId).ValueGeneratedNever();
        });

        b.Entity<EmployeeUpload>(e =>
        {
            e.ToTable("EmployeeUploads");
            e.HasKey(x => x.UploadId);
            e.Property(x => x.ContentType).IsUnicode(false).HasMaxLength(100);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.ReviewedBy).WithMany().HasForeignKey(x => x.ReviewedByUserId);
            e.HasOne(x => x.File).WithOne().HasForeignKey<EmployeeUploadFile>(x => x.UploadId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EmployeeUploadFile>(e =>
        {
            e.ToTable("EmployeeUploadFiles");
            e.HasKey(x => x.UploadId);
            e.Property(x => x.UploadId).ValueGeneratedNever();
        });

        b.Entity<SalarySnapshot>(e =>
        {
            e.ToTable("SalarySnapshots");
            e.HasKey(x => x.SalaryStructureId);
            e.Property(x => x.SalaryStructureId).ValueGeneratedNever();
            e.Property(x => x.GrossMonthly).HasPrecision(12, 2);
        });
    }

    // ------------------------------------------------------------------ Published to the other services
    protected override bool Publishes(Type t) =>
        t == typeof(Employee) || t == typeof(Department) || t == typeof(Holiday) || t == typeof(CompanyProfile);

    protected override Task<IEnumerable<OutboxEvent>> EventsAfterSaveAsync(
        IReadOnlyList<(object Entity, EntityState State)> changes, CancellationToken ct) =>
        Task.FromResult(changes.Select(c => c.Entity switch
        {
            Employee e => EmployeeEvent(e, isNew: c.State == EntityState.Added),
            Department d => DepartmentEvent(d),
            Holiday h when c.State == EntityState.Deleted => new OutboxEvent(Topics.Holidays, h.HolidayId.ToString(), new HolidayDeleted(h.HolidayId)),
            Holiday h => HolidayEvent(h),
            CompanyProfile p => CompanyEvent(p),
            _ => null,
        }).OfType<OutboxEvent>());

    public static OutboxEvent EmployeeEvent(Employee e, bool isNew) => new(Topics.Employees, e.EmployeeId.ToString(), new EmployeeUpserted(
        e.EmployeeId, e.EmployeeCode, e.FirstName, e.LastName, e.Email, e.Phone, e.Gender, e.DateOfBirth, e.DateOfJoining,
        e.DepartmentId, e.Designation, e.ManagerId, e.PanNumber, e.BankName, e.BankAccountNo, e.IfscCode,
        e.BloodGroup, e.EmergencyContactName, e.EmergencyContactPhone, e.IsActive, e.CreatedAt, e.UpdatedAt, isNew));

    public static OutboxEvent DepartmentEvent(Department d) =>
        new(Topics.Departments, d.DepartmentId.ToString(), new DepartmentUpserted(d.DepartmentId, d.Name, d.IsActive));

    public static OutboxEvent HolidayEvent(Holiday h) =>
        new(Topics.Holidays, h.HolidayId.ToString(), new HolidayUpserted(h.HolidayId, h.HolidayDate, h.Name));

    public static OutboxEvent CompanyEvent(CompanyProfile c) => new(Topics.Company, CompanyProfile.SingletonId.ToString(), new CompanyProfileUpdated(
        c.Name, c.LegalName, c.AddressLine1, c.AddressLine2, c.City, c.State, c.PinCode, c.Phone, c.Email, c.Website,
        c.Pan, c.Tan, c.Gstin, c.SignatoryName, c.SignatoryDesignation, c.UpdatedAt));
}
