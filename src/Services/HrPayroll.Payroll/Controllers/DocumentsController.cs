using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Payroll.Entities;
using HrPayroll.Payroll.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Payroll.Controllers;

[ApiController]
[Route("api/documents")]
[Authorize]
public class DocumentsController(DocumentService documents) : ControllerBase
{
    // ---------------------------------------------------------------- Employee
    /// <summary>Documents released to the signed-in employee.</summary>
    [HttpGet("my")]
    public async Task<ActionResult<List<DocumentSummaryDto>>> Mine(CancellationToken ct) =>
        Ok(await documents.SearchAsync(User.RequireEmployeeId(), null, null, ct));

    /// <summary>Single document - employees can open their own; HR / admin the types they manage.</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<DocumentDto>> Get(int id, CancellationToken ct)
    {
        var doc = await documents.GetAsync(id, ct);
        if (doc.EmployeeId != User.GetEmployeeId() && !TypesFor(Permissions.View).Contains(doc.DocumentType)) return Forbid();
        return Ok(doc);
    }

    // ---------------------------------------------------------------- HR / admin
    /// <summary>Released documents of the types the caller may manage (letters and / or Form 16).</summary>
    [HttpGet]
    [HasPermission(Permissions.LettersView, Permissions.Form16View)]
    public async Task<ActionResult<List<DocumentSummaryDto>>> Search(
        [FromQuery] int? employeeId, [FromQuery] string? type, CancellationToken ct) =>
        Ok(await documents.SearchAsync(employeeId, type, TypesFor(Permissions.View), ct));

    [HttpPost("offer-letter")]
    [HasPermission(Permissions.LettersAdd)]
    public async Task<ActionResult<DocumentDto>> OfferLetter(OfferLetterRequest request, CancellationToken ct) =>
        Released(await documents.ReleaseOfferLetterAsync(request, User.GetUserId(), ct));

    [HttpPost("relieving-letter")]
    [HasPermission(Permissions.LettersAdd)]
    public async Task<ActionResult<DocumentDto>> RelievingLetter(RelievingLetterRequest request, CancellationToken ct) =>
        Released(await documents.ReleaseRelievingLetterAsync(request, User.GetUserId(), ct));

    [HttpPost("form16")]
    [HasPermission(Permissions.Form16Add)]
    public async Task<ActionResult<DocumentDto>> Form16(Form16Request request, CancellationToken ct) =>
        Released(await documents.ReleaseForm16Async(request, User.GetUserId(), ct));

    [HttpDelete("{id:int}")]
    [HasPermission(Permissions.LettersDelete, Permissions.Form16Delete)]
    public async Task<IActionResult> Revoke(int id, CancellationToken ct)
    {
        await documents.RevokeAsync(id, TypesFor(Permissions.Delete), ct);
        return NoContent();
    }

    private CreatedAtActionResult Released(DocumentDto doc) => CreatedAtAction(nameof(Get), new { id = doc.DocumentId }, doc);

    /// <summary>Document types the caller has the given right (view / add / delete) for.</summary>
    private string[] TypesFor(string action) =>
    [
        .. User.HasPermission($"letters.{action}") ? new[] { DocumentTypes.OfferLetter, DocumentTypes.RelievingLetter, DocumentTypes.PromotionLetter } : [],
        .. User.HasPermission($"form16.{action}") ? new[] { DocumentTypes.Form16 } : [],
    ];
}
