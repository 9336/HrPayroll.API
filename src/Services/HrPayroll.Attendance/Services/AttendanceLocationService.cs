using System.Security.Cryptography;
using System.Text;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Attendance.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Attendance.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace HrPayroll.Attendance.Services;

/// <summary>Where a check-in / check-out happened, once it has been accepted.</summary>
public record PunchPlace(decimal? Latitude, decimal? Longitude, int? Accuracy, string? Place, int? Distance, string Source, bool Biometric = false,
    decimal? FaceScore = null, byte[]? Selfie = null);

/// <summary>
/// Attendance areas (geofences): admins set a centre and radius; when any active area applies to an employee they
/// can only check in / out from inside one of their areas. With no area for them, attendance works from anywhere.
/// </summary>
public class AttendanceLocationService(AttendanceDbContext db, IMemoryCache cache, AttendanceFaceService faces, TimeProvider time)
{
    /// <summary>How long a check-in challenge stays valid (the employee scans their face in between).</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);

    // ------------------------------------------------------------------ Rules
    public async Task<AttendanceSettings> GetRulesAsync(CancellationToken ct) =>
        await db.AttendanceSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new AttendanceSettings();

    public async Task<AttendanceSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var s = await GetRulesAsync(ct);
        var active = db.Employees.Where(e => e.IsActive);
        var anyAllArea = await db.AttendanceLocations.AnyAsync(l => l.IsActive && l.AllEmployees, ct);
        var withoutArea = anyAllArea ? 0 : await active.CountAsync(e =>
            !db.AttendanceLocationEmployees.Any(x => x.EmployeeId == e.EmployeeId && db.AttendanceLocations.Any(l => l.LocationId == x.LocationId && l.IsActive)), ct);
        var withoutPhone = await active.CountAsync(e => e.User != null && e.User.IsActive &&
            !db.AttendanceDevices.Any(d => d.EmployeeId == e.EmployeeId && d.IsActive), ct);
        var withoutFace = await active.CountAsync(e => e.User != null && e.User.IsActive &&
            !db.EmployeeFaces.Any(f => f.EmployeeId == e.EmployeeId && f.Status == EmployeeFaceStatus.Approved), ct);
        return new AttendanceSettingsDto(s.RequireLocation, s.RequireBiometric, withoutArea, withoutPhone, s.UpdatedAt, s.RequireFace, withoutFace);
    }

    public async Task<AttendanceSettingsDto> SaveSettingsAsync(AttendanceSettingsRequest req, int userId, CancellationToken ct)
    {
        var s = await db.AttendanceSettings.FirstOrDefaultAsync(ct) ?? db.AttendanceSettings.Add(new AttendanceSettings()).Entity;
        s.RequireLocation = req.RequireLocation;
        s.RequireBiometric = req.RequireBiometric;
        s.RequireFace = req.RequireFace;
        s.UpdatedAt = time.GetUtcNow().UtcDateTime;
        s.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
        return await GetSettingsAsync(ct);
    }

    // ------------------------------------------------------------------ Registered phones (Face ID / fingerprint)
    public Task<List<AttendanceDeviceDto>> GetDevicesAsync(CancellationToken ct) =>
        db.AttendanceDevices.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Employee.FirstName).ThenBy(d => d.Employee.LastName)
            .Select(d => new AttendanceDeviceDto(d.DeviceId, d.EmployeeId, d.Employee.FirstName + " " + d.Employee.LastName, d.Employee.EmployeeCode,
                d.Platform, d.DeviceName, d.RegisteredAt, d.LastUsedAt))
            .ToListAsync(ct);

    /// <summary>HR resets a phone (lost / new phone): the employee can then register another one.</summary>
    public async Task RevokeDeviceAsync(int deviceId, int userId, CancellationToken ct)
    {
        var d = await db.AttendanceDevices.FirstOrDefaultAsync(x => x.DeviceId == deviceId && x.IsActive, ct)
                ?? throw new NotFoundException("Registered phone not found.");
        d.IsActive = false;
        d.RevokedAt = time.GetUtcNow().UtcDateTime;
        d.RevokedByUserId = userId;
        await db.SaveChangesAsync(ct);
    }

    public Task<MyDeviceDto?> MyDeviceAsync(int employeeId, CancellationToken ct) =>
        db.AttendanceDevices.AsNoTracking().Where(d => d.EmployeeId == employeeId && d.IsActive)
            .Select(d => new MyDeviceDto(d.InstallId, d.Platform, d.DeviceName, d.RegisteredAt)).FirstOrDefaultAsync(ct);

    /// <summary>Links the employee's phone (its biometric-protected public key). One active phone per employee.</summary>
    public async Task<MyDeviceDto> RegisterDeviceAsync(int employeeId, RegisterDeviceRequest req, CancellationToken ct)
    {
        var existing = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.EmployeeId == employeeId && d.IsActive, ct);
        if (existing is not null)
            throw new ConflictException(existing.InstallId == req.InstallId
                ? "This phone is already set up for Face ID / fingerprint check-in."
                : $"Your account is already linked to another phone ({existing.DeviceName}). Ask HR to reset it, then set up this phone.");
        if (req.Platform is not ("Android" or "iOS")) throw new AppException("Unknown phone platform.");
        byte[] spki;
        try { spki = Convert.FromBase64String(req.PublicKey); }
        catch (FormatException) { throw new AppException("Invalid security key from the phone."); }
        using (var ecdsa = ECDsa.Create())
        {
            try { ecdsa.ImportSubjectPublicKeyInfo(spki, out _); }
            catch (CryptographicException) { throw new AppException("Invalid security key from the phone."); }
            if (ecdsa.KeySize != 256) throw new AppException("The phone's security key is not supported (P-256 expected).");
        }
        var device = new AttendanceDevice
        {
            EmployeeId = employeeId, InstallId = req.InstallId, Platform = req.Platform, DeviceName = req.DeviceName.Trim(),
            PublicKey = req.PublicKey, RegisteredAt = time.GetUtcNow().UtcDateTime,
        };
        db.AttendanceDevices.Add(device);
        await db.SaveChangesAsync(ct);
        return new MyDeviceDto(device.InstallId, device.Platform, device.DeviceName, device.RegisteredAt);
    }

    /// <summary>A one-time random value the phone signs (after Face ID) to prove a check-in is fresh.</summary>
    public AttendanceChallengeDto IssueChallenge(int employeeId)
    {
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = time.GetUtcNow().Add(ChallengeLifetime);
        cache.Set(ChallengeKey(challenge), employeeId, expires);
        return new AttendanceChallengeDto(challenge, expires.UtcDateTime);
    }

    private static string ChallengeKey(string c) => "attendance-challenge:" + c;

    /// <summary>Checks the phone's Face ID / fingerprint signature; the challenge can be used once.</summary>
    private async Task VerifyBiometricAsync(int employeeId, AttendancePunchRequest? req, string action, CancellationToken ct)
    {
        if (req?.Source != "Mobile" || req.InstallId is null || string.IsNullOrEmpty(req.Challenge) || string.IsNullOrEmpty(req.Signature))
            throw new AppException($"Attendance must be marked in the mobile app with Face ID / fingerprint. Open the HR Attendance app to {action}.");
        var device = await db.AttendanceDevices.FirstOrDefaultAsync(d => d.EmployeeId == employeeId && d.IsActive, ct)
                     ?? throw new AppException("Set up Face ID / fingerprint check-in in the mobile app first.");
        if (device.InstallId != req.InstallId)
            throw new AppException($"Your account is linked to another phone ({device.DeviceName}). Use that phone, or ask HR to reset it.");
        var key = ChallengeKey(req.Challenge);
        if (!cache.TryGetValue(key, out int owner) || owner != employeeId)
            throw new AppException("The security check expired. Please try again.");
        cache.Remove(key);   // single use

        byte[] signature;
        try { signature = Convert.FromBase64String(req.Signature); }
        catch (FormatException) { throw new AppException("Face ID / fingerprint verification failed. Please try again."); }
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(device.PublicKey), out _);
        var payload = Encoding.UTF8.GetBytes(PunchPayload.Build(req.Challenge, employeeId, action.Replace(' ', '-'), req.Latitude, req.Longitude));
        if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new AppException("Face ID / fingerprint verification failed. Please try again.");
        device.LastUsedAt = time.GetUtcNow().UtcDateTime;
    }
    // ------------------------------------------------------------------ Admin
    public async Task<List<AttendanceLocationDto>> GetAllAsync(CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(time.GetLocalNow().DateTime).AddDays(-30);
        var locations = await db.AttendanceLocations.AsNoTracking().OrderByDescending(l => l.IsActive).ThenBy(l => l.Name)
            .Select(l => new
            {
                l.LocationId, l.Name, l.Address, l.Latitude, l.Longitude, l.RadiusMeters, l.AllEmployees, l.IsActive, l.CreatedAt,
                Employees = l.Employees.OrderBy(x => x.Employee.FirstName)
                    .Select(x => new CalendarPersonDto(x.EmployeeId, x.Employee.FirstName + " " + x.Employee.LastName, x.Employee.Designation, x.Employee.Department.Name))
                    .ToList(),
            })
            .ToListAsync(ct);
        var counts = await db.Attendance.AsNoTracking().Where(a => a.AttendanceDate >= since && a.CheckInPlace != null)
            .GroupBy(a => a.CheckInPlace!).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return locations.Select(l => new AttendanceLocationDto(l.LocationId, l.Name, l.Address, l.Latitude, l.Longitude, l.RadiusMeters,
            l.AllEmployees, l.IsActive, l.Employees, counts.GetValueOrDefault(l.Name), l.CreatedAt)).ToList();
    }

    public async Task<AttendanceLocationDto> SaveAsync(int? id, AttendanceLocationRequest req, int userId, CancellationToken ct)
    {
        var name = req.Name.Trim();
        if (await db.AttendanceLocations.AnyAsync(l => l.Name == name && l.LocationId != id, ct))
            throw new ConflictException($"An attendance area named '{name}' already exists.");
        var employeeIds = req.AllEmployees ? [] : req.EmployeeIds.Distinct().ToList();
        if (!req.AllEmployees && employeeIds.Count == 0) throw new AppException("Choose the employees this area applies to, or apply it to everyone.");
        if (employeeIds.Count > 0 && await db.Employees.CountAsync(e => employeeIds.Contains(e.EmployeeId), ct) != employeeIds.Count)
            throw new AppException("One of the selected employees does not exist.");

        var location = id is int existing
            ? await db.AttendanceLocations.Include(l => l.Employees).FirstOrDefaultAsync(l => l.LocationId == existing, ct)
              ?? throw new NotFoundException("Attendance area not found.")
            : db.AttendanceLocations.Add(new AttendanceLocation { CreatedByUserId = userId, CreatedAt = time.GetUtcNow().UtcDateTime }).Entity;
        location.Name = name;
        location.Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim();
        location.Latitude = Math.Round(req.Latitude, 6);
        location.Longitude = Math.Round(req.Longitude, 6);
        location.RadiusMeters = req.RadiusMeters;
        location.AllEmployees = req.AllEmployees;
        location.IsActive = req.IsActive;
        if (id is not null) location.UpdatedAt = time.GetUtcNow().UtcDateTime;
        location.Employees.RemoveAll(e => !employeeIds.Contains(e.EmployeeId));
        foreach (var e in employeeIds.Where(e => location.Employees.All(x => x.EmployeeId != e)))
            location.Employees.Add(new AttendanceLocationEmployee { EmployeeId = e });
        await db.SaveChangesAsync(ct);
        return (await GetAllAsync(ct)).First(l => l.LocationId == location.LocationId);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        // Past attendance keeps the area's name and the coordinates, so deleting loses no history.
        if (await db.AttendanceLocations.Where(l => l.LocationId == id).ExecuteDeleteAsync(ct) == 0)
            throw new NotFoundException("Attendance area not found.");
    }

    // ------------------------------------------------------------------ Employee
    /// <summary>Active areas that apply to the employee (empty = they may check in from anywhere).</summary>
    public Task<List<AttendancePlaceDto>> PlacesForAsync(int employeeId, CancellationToken ct) =>
        db.AttendanceLocations.AsNoTracking()
            .Where(l => l.IsActive && (l.AllEmployees || l.Employees.Any(e => e.EmployeeId == employeeId)))
            .OrderBy(l => l.Name)
            .Select(l => new AttendancePlaceDto(l.LocationId, l.Name, l.Address, l.Latitude, l.Longitude, l.RadiusMeters))
            .ToListAsync(ct);

    /// <summary>Checks a check-in / check-out position against the employee's areas; throws with a clear reason when refused.</summary>
    /// <summary>All attendance checks: office location, Face ID / fingerprint signature, and the selfie face check.</summary>
    public async Task<PunchPlace> ValidateAsync(int employeeId, AttendancePunchRequest? req, string action, CancellationToken ct)
    {
        var rules = await GetRulesAsync(ct);
        var place = await ValidatePlaceAsync(employeeId, req, action, rules, ct);
        if (!rules.RequireFace) return place;
        var face = await faces.VerifyAsync(employeeId, req?.Selfie, action, ct);   // last: the most expensive check
        return place with { FaceScore = face.Score, Selfie = face.Jpeg };
    }

    private async Task<PunchPlace> ValidatePlaceAsync(int employeeId, AttendancePunchRequest? req, string action, AttendanceSettings rules, CancellationToken ct)
    {
        var source = req?.Source is "Mobile" ? "Mobile" : "Web";
        var places = await PlacesForAsync(employeeId, ct);
        var hasPosition = req?.Latitude is not null && req.Longitude is not null;
        int? accuracy = req?.AccuracyMeters is double a ? (int)Math.Round(a) : null;

        if (places.Count == 0 && rules.RequireLocation)
            throw new AppException("Attendance can only be marked at your office, and no office location is set up for you yet. Please ask HR.");
        // The signature is checked first: the challenge is used up even when the location check fails afterwards.
        var biometric = false;
        if (rules.RequireBiometric) { await VerifyBiometricAsync(employeeId, req, action, ct); biometric = true; }

        if (places.Count == 0)   // no area for this employee: allowed anywhere, position kept if sent
            return new PunchPlace(req?.Latitude, req?.Longitude, accuracy, null, null, source, biometric);

        if (!hasPosition)
            throw new AppException($"Your location is needed to {action}. Allow location access and try again.");
        if (req!.IsMock)
            throw new AppException("A fake / mock location was detected. Turn off any location-spoofing app to mark attendance.");
        if (accuracy > AttendanceRules.MaxAccuracyMeters)
            throw new AppException($"Your location is not accurate enough (±{accuracy} m). Turn on GPS / precise location, step outside or near a window, and try again.");

        var (place, distance) = places
            .Select(p => (Place: p, Distance: DistanceMeters(req.Latitude!.Value, req.Longitude!.Value, p.Latitude, p.Longitude)))
            .OrderBy(x => x.Distance - x.Place.RadiusMeters).First();
        var allowed = place.RadiusMeters + Math.Min(accuracy ?? 0, AttendanceRules.EdgeToleranceMeters);
        if (distance > allowed)
        {
            var away = FormatDistance(distance - place.RadiusMeters);
            throw new AppException(places.Count == 1
                ? $"You are {away} outside {place.Name}. Move inside the attendance area to {action}."
                : $"You are not inside any of your attendance areas - the nearest, {place.Name}, is {away} away. Move inside it to {action}.");
        }
        return new PunchPlace(Math.Round(req.Latitude!.Value, 6), Math.Round(req.Longitude!.Value, 6), accuracy, place.Name, (int)Math.Round(distance), source, biometric);
    }

    /// <summary>Great-circle distance in metres (haversine).</summary>
    public static double DistanceMeters(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
    {
        const double R = 6371000;
        static double Rad(decimal d) => (double)d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static string FormatDistance(double m) => m >= 1000 ? $"{m / 1000:0.#} km" : $"{Math.Ceiling(m):0} m";
}
