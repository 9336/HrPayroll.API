using HrPayroll.BuildingBlocks.Data;
using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HrPayroll.BuildingBlocks.Directory;

/// <summary>
/// Keeps this service's copy of the company directory (and, where needed, of leave requests) in step with the owning
/// services. Every handler is an upsert of the record's full current state, so replaying a topic is harmless.
/// </summary>
public sealed class DirectoryReplicaHandlers(ServiceDbContext db) :
    IIntegrationEventHandler<EmployeeUpserted>,
    IIntegrationEventHandler<DepartmentUpserted>,
    IIntegrationEventHandler<HolidayUpserted>,
    IIntegrationEventHandler<HolidayDeleted>,
    IIntegrationEventHandler<CompanyProfileUpdated>,
    IIntegrationEventHandler<UserUpserted>,
    IIntegrationEventHandler<RoleUpserted>,
    IIntegrationEventHandler<RoleDeleted>,
    IIntegrationEventHandler<LeaveRequestChanged>
{
    public async Task HandleAsync(EmployeeUpserted m, EventContext context, CancellationToken ct)
    {
        var e = await db.Employees.FindAsync([m.EmployeeId], ct) ?? db.Employees.Add(new Employee { EmployeeId = m.EmployeeId }).Entity;
        e.EmployeeCode = m.EmployeeCode;
        e.FirstName = m.FirstName;
        e.LastName = m.LastName;
        e.Email = m.Email;
        e.Phone = m.Phone;
        e.Gender = m.Gender;
        e.DateOfBirth = m.DateOfBirth;
        e.DateOfJoining = m.DateOfJoining;
        e.DepartmentId = m.DepartmentId;
        e.Designation = m.Designation;
        e.ManagerId = m.ManagerId;
        e.PanNumber = m.PanNumber;          // not stored unless the copy keeps financial details
        e.BankName = m.BankName;
        e.BankAccountNo = m.BankAccountNo;
        e.IfscCode = m.IfscCode;
        e.BloodGroup = m.BloodGroup;
        e.EmergencyContactName = m.EmergencyContactName;
        e.EmergencyContactPhone = m.EmergencyContactPhone;
        e.IsActive = m.IsActive;
        e.CreatedAt = m.CreatedAt;
        e.UpdatedAt = m.UpdatedAt;
    }

    public async Task HandleAsync(DepartmentUpserted m, EventContext context, CancellationToken ct)
    {
        var d = await db.Departments.FindAsync([m.DepartmentId], ct) ?? db.Departments.Add(new Department { DepartmentId = m.DepartmentId }).Entity;
        d.Name = m.Name;
        d.IsActive = m.IsActive;
    }

    public async Task HandleAsync(HolidayUpserted m, EventContext context, CancellationToken ct)
    {
        var h = await db.Holidays.FindAsync([m.HolidayId], ct) ?? db.Holidays.Add(new Holiday { HolidayId = m.HolidayId }).Entity;
        h.HolidayDate = m.HolidayDate;
        h.Name = m.Name;
    }

    public async Task HandleAsync(HolidayDeleted m, EventContext context, CancellationToken ct) =>
        await db.Holidays.Where(h => h.HolidayId == m.HolidayId).ExecuteDeleteAsync(ct);

    public async Task HandleAsync(CompanyProfileUpdated m, EventContext context, CancellationToken ct)
    {
        var c = await db.CompanyProfiles.FindAsync([CompanyProfile.SingletonId], ct) ?? db.CompanyProfiles.Add(new CompanyProfile()).Entity;
        c.Name = m.Name;
        c.LegalName = m.LegalName;
        c.AddressLine1 = m.AddressLine1;
        c.AddressLine2 = m.AddressLine2;
        c.City = m.City;
        c.State = m.State;
        c.PinCode = m.PinCode;
        c.Phone = m.Phone;
        c.Email = m.Email;
        c.Website = m.Website;
        c.Pan = m.Pan;
        c.Tan = m.Tan;
        c.Gstin = m.Gstin;
        c.SignatoryName = m.SignatoryName;
        c.SignatoryDesignation = m.SignatoryDesignation;
        c.UpdatedAt = m.UpdatedAt;
    }

    public async Task HandleAsync(UserUpserted m, EventContext context, CancellationToken ct)
    {
        var u = await db.Users.FindAsync([m.UserId], ct) ?? db.Users.Add(new User { UserId = m.UserId }).Entity;
        u.Username = m.Username;
        u.RoleId = m.RoleId;
        u.EmployeeId = m.EmployeeId;
        u.IsActive = m.IsActive;
        u.LastLoginAt = m.LastLoginAt;
        u.CreatedAt = m.CreatedAt;
        // LastSeenAt belongs to the Chat service and is left as it is.
    }

    public async Task HandleAsync(RoleUpserted m, EventContext context, CancellationToken ct)
    {
        var r = await db.Roles.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.RoleId == m.RoleId, ct)
                ?? db.Roles.Add(new Role { RoleId = m.RoleId }).Entity;
        r.Name = m.Name;
        r.Description = m.Description;
        r.IsSystem = m.IsSystem;
        var wanted = m.Permissions.ToHashSet();
        r.Permissions.RemoveAll(p => !wanted.Contains(p.Permission));
        foreach (var p in wanted.Where(w => r.Permissions.All(x => x.Permission != w)))
            r.Permissions.Add(new RolePermission { RoleId = m.RoleId, Permission = p });
    }

    public async Task HandleAsync(RoleDeleted m, EventContext context, CancellationToken ct)
    {
        await db.RolePermissions.Where(p => p.RoleId == m.RoleId).ExecuteDeleteAsync(ct);
        await db.Roles.Where(r => r.RoleId == m.RoleId).ExecuteDeleteAsync(ct);
    }

    public async Task HandleAsync(LeaveRequestChanged m, EventContext context, CancellationToken ct)
    {
        var set = db.Set<LeaveRecord>();
        var l = await set.FindAsync([m.LeaveRequestId], ct) ?? set.Add(new LeaveRecord { LeaveRequestId = m.LeaveRequestId }).Entity;
        l.EmployeeId = m.EmployeeId;
        l.LeaveTypeId = m.LeaveTypeId;
        l.LeaveCode = m.LeaveCode;
        l.LeaveName = m.LeaveName;
        l.IsPaid = m.IsPaid;
        l.FromDate = m.FromDate;
        l.ToDate = m.ToDate;
        l.IsHalfDay = m.IsHalfDay;
        l.TotalDays = m.TotalDays;
        l.Status = m.Status;
    }
}

public static class DirectoryReplicaRegistration
{
    /// <summary>Subscribes this service to the directory topics it does not own (and to leave requests when asked).</summary>
    public static IServiceCollection AddDirectoryCopy(this IServiceCollection services, DirectoryModel model, bool leaveRecords = false)
    {
        if (!model.OwnsEmployees)
        {
            services.AddEventHandler<EmployeeUpserted, DirectoryReplicaHandlers>(Topics.Employees);
            services.AddEventHandler<DepartmentUpserted, DirectoryReplicaHandlers>(Topics.Departments);
            services.AddEventHandler<HolidayUpserted, DirectoryReplicaHandlers>(Topics.Holidays);
            services.AddEventHandler<HolidayDeleted, DirectoryReplicaHandlers>(Topics.Holidays);
            services.AddEventHandler<CompanyProfileUpdated, DirectoryReplicaHandlers>(Topics.Company);
        }
        if (!model.OwnsUsers)
        {
            services.AddEventHandler<UserUpserted, DirectoryReplicaHandlers>(Topics.Users);
            services.AddEventHandler<RoleUpserted, DirectoryReplicaHandlers>(Topics.Roles);
            services.AddEventHandler<RoleDeleted, DirectoryReplicaHandlers>(Topics.Roles);
        }
        if (leaveRecords) services.AddEventHandler<LeaveRequestChanged, DirectoryReplicaHandlers>(Topics.LeaveRequests);
        return services;
    }
}
