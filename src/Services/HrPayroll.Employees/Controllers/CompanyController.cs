using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Employees.Entities;
using HrPayroll.Employees.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Employees.Controllers;

[ApiController]
[Route("api/company")]
[Authorize]
public class CompanyController(CompanyService company) : ControllerBase
{
    /// <summary>Company master (name, address, logo...) - used for branding and on letters / payslips.</summary>
    [HttpGet]
    public async Task<ActionResult<CompanyDto>> Get(CancellationToken ct) =>
        Ok(await company.GetAsync(ct));

    [HttpPut]
    [HasPermission(Permissions.CompanyEdit)]
    public async Task<ActionResult<CompanyDto>> Update(UpdateCompanyRequest request, CancellationToken ct) =>
        Ok(await company.UpdateAsync(request, ct));

    /// <summary>Uploads the company logo (PNG, JPEG or WebP, max 512 KB) as multipart field "file".</summary>
    [HttpPut("logo")]
    [HasPermission(Permissions.CompanyEdit)]
    [RequestSizeLimit(CompanyService.MaxLogoBytes + 64 * 1024)]
    public async Task<ActionResult<CompanyDto>> UploadLogo(IFormFile file, CancellationToken ct) =>
        Ok(await company.SetLogoAsync(file, ct));

    [HttpDelete("logo")]
    [HasPermission(Permissions.CompanyEdit)]
    public async Task<ActionResult<CompanyDto>> RemoveLogo(CancellationToken ct) =>
        Ok(await company.RemoveLogoAsync(ct));
}

[ApiController]
[Route("api/org")]
[Authorize]
public class OrgController(EmployeeService employees) : ControllerBase
{
    /// <summary>Company hierarchy - every active employee with their reporting manager. Visible to all staff.</summary>
    [HttpGet("chart")]
    public async Task<ActionResult<List<OrgNodeDto>>> Chart(CancellationToken ct) =>
        Ok(await employees.GetChartAsync(ct));
}
