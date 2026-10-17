using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Expenses.Services;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Expenses.Controllers;

/// <summary>Daily office expenses: view / add / edit / delete rights, and approvers (expenses.approve) who review.</summary>
[ApiController]
[Route("api/expenses")]
[HasPermission(Permissions.ExpensesView)]
public class ExpensesController(ExpenseService expenses) : ControllerBase
{
    private ExpenseActor Actor => new(User.GetUserId(), User.HasPermission(Permissions.ExpensesEdit), User.HasPermission(Permissions.ExpensesDelete));

    [HttpGet]
    public async Task<ActionResult<ExpenseSearchResult>> Search([FromQuery] ExpenseQuery query, CancellationToken ct) =>
        Ok(await expenses.SearchAsync(query, User.GetUserId(), ct));

    [HttpGet("summary")]
    public async Task<ActionResult<ExpenseSummaryDto>> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Ok(await expenses.GetSummaryAsync(from, to, ct));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ExpenseDto>> Get(int id, CancellationToken ct) => Ok(await expenses.GetAsync(id, ct));

    [HttpPost]
    [HasPermission(Permissions.ExpensesAdd)]
    public async Task<ActionResult<ExpenseDto>> Create(ExpenseRequest request, CancellationToken ct)
    {
        var created = await expenses.CreateAsync(request, User.GetUserId(), ct);
        return CreatedAtAction(nameof(Get), new { id = created.ExpenseId }, created);
    }

    /// <summary>Own pending expense (expenses.add) or anyone's (expenses.edit).</summary>
    [HttpPut("{id:int}")]
    [HasPermission(Permissions.ExpensesAdd, Permissions.ExpensesEdit)]
    public async Task<ActionResult<ExpenseDto>> Update(int id, ExpenseRequest request, CancellationToken ct) =>
        Ok(await expenses.UpdateAsync(id, request, Actor, ct));

    /// <summary>Own pending expense (expenses.add) or anyone's (expenses.delete).</summary>
    [HttpDelete("{id:int}")]
    [HasPermission(Permissions.ExpensesAdd, Permissions.ExpensesDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await expenses.DeleteAsync(id, Actor, ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- Receipt
    /// <summary>Receipt file (PDF / PNG / JPEG / WebP).</summary>
    [HttpGet("{id:int}/receipt")]
    public async Task<IActionResult> Receipt(int id, CancellationToken ct)
    {
        var (bytes, type, name) = await expenses.GetReceiptAsync(id, ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(bytes, type, name);
    }

    /// <summary>Attaches / replaces the receipt (multipart field "file", max 2 MB).</summary>
    [HttpPut("{id:int}/receipt")]
    [HasPermission(Permissions.ExpensesAdd, Permissions.ExpensesEdit)]
    [RequestSizeLimit(ExpenseService.MaxReceiptBytes + 64 * 1024)]
    public async Task<ActionResult<ExpenseDto>> UploadReceipt(int id, IFormFile file, CancellationToken ct) =>
        Ok(await expenses.SetReceiptAsync(id, file, Actor, ct));

    [HttpDelete("{id:int}/receipt")]
    [HasPermission(Permissions.ExpensesAdd, Permissions.ExpensesEdit)]
    public async Task<ActionResult<ExpenseDto>> RemoveReceipt(int id, CancellationToken ct) =>
        Ok(await expenses.RemoveReceiptAsync(id, Actor, ct));

    // ---------------------------------------------------------------- Review (approver)
    [HttpPost("{id:int}/approve")]
    [HasPermission(Permissions.ExpensesApprove)]
    public async Task<ActionResult<ExpenseDto>> Approve(int id, ExpenseReviewRequest request, CancellationToken ct) =>
        Ok(await expenses.ApproveAsync(id, User.GetUserId(), request.Remarks, ct));

    [HttpPost("{id:int}/reject")]
    [HasPermission(Permissions.ExpensesApprove)]
    public async Task<ActionResult<ExpenseDto>> Reject(int id, ExpenseReviewRequest request, CancellationToken ct) =>
        Ok(await expenses.RejectAsync(id, User.GetUserId(), request.Remarks, ct));

    // ---------------------------------------------------------------- Categories
    [HttpGet("categories")]
    public async Task<ActionResult<List<ExpenseCategoryDto>>> Categories([FromQuery] bool activeOnly = true, CancellationToken ct = default) =>
        Ok(await expenses.GetCategoriesAsync(activeOnly, ct));

    [HttpPost("categories")]
    [HasPermission(Permissions.ExpensesApprove)]
    public async Task<ActionResult<ExpenseCategoryDto>> CreateCategory(ExpenseCategoryRequest request, CancellationToken ct) =>
        Ok(await expenses.SaveCategoryAsync(null, request, ct));

    [HttpPut("categories/{id:int}")]
    [HasPermission(Permissions.ExpensesApprove)]
    public async Task<ActionResult<ExpenseCategoryDto>> UpdateCategory(int id, ExpenseCategoryRequest request, CancellationToken ct) =>
        Ok(await expenses.SaveCategoryAsync(id, request, ct));
}
