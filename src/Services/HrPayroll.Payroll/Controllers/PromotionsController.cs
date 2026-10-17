using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Payroll.Services;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Payroll.Controllers;

/// <summary>Employee promotions (promotions.view / add / delete; an increment also needs salary.add).</summary>
[ApiController]
[Route("api/promotions")]
[HasPermission(Permissions.PromotionsView)]
public class PromotionsController(PromotionService promotions) : ControllerBase
{
    private PromotionActor Actor => new(User.GetUserId(), User.HasPermission(Permissions.SalaryView), User.HasPermission(Permissions.SalaryAdd));

    [HttpGet]
    public async Task<ActionResult<List<PromotionDto>>> Search([FromQuery] string? status, [FromQuery] int? employeeId, [FromQuery] int? year,
        CancellationToken ct) => Ok(await promotions.SearchAsync(status, employeeId, year, ct));

    [HttpGet("context/{employeeId:int}")]
    public async Task<ActionResult<PromotionContextDto>> Context(int employeeId, CancellationToken ct) =>
        Ok(await promotions.GetContextAsync(employeeId, Actor, ct));

    [HttpPost]
    [HasPermission(Permissions.PromotionsAdd)]
    public async Task<ActionResult<PromotionDto>> Create(CreatePromotionRequest request, CancellationToken ct) =>
        Ok(await promotions.CreateAsync(request, Actor, ct));

    [HttpPost("{id:int}/cancel")]
    [HasPermission(Permissions.PromotionsDelete)]
    public async Task<ActionResult<PromotionDto>> Cancel(int id, CancellationToken ct) => Ok(await promotions.CancelAsync(id, ct));
}
