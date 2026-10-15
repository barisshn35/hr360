using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ExpenseService.Data;
using ExpenseService.Models;
using ExpenseService.Services;

namespace ExpenseService.Controllers;

/// <summary>
/// Ozluk dokumani metadata'si. Dosyanin kendisi MinIO'da tutulur;
/// bu servis yalnizca kaydi yonetir (StorageKey ile baglanti).
/// </summary>
[ApiController]
[Route("api/documents")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly ExpenseDbContext _db;
    private readonly ApprovalWorkflowClient _employees;
    public DocumentsController(ExpenseDbContext db, ApprovalWorkflowClient employees)
    {
        _db = db;
        _employees = employees;
    }

    [HttpGet]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> GetAll([FromQuery] Guid? employeeId, [FromQuery] DocumentType? type)
    {
        var q = _db.Documents.AsQueryable();
        if (employeeId.HasValue) q = q.Where(d => d.EmployeeId == employeeId.Value);
        if (type.HasValue) q = q.Where(d => d.Type == type.Value);
        return Ok(await q.OrderByDescending(d => d.UploadedAt).ToListAsync());
    }

    [HttpPost]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> Create([FromBody] CreateDocumentRequest request, CancellationToken ct)
    {
        // NOT: Arayuz {type: serbest metin, name, storageKey?} gonderiyordu; bu uc ise
        // enum Type + zorunlu FileName/StorageKey bekliyordu - her kayit 400 aliyordu
        // (Dokumanlar ekrani hic calismiyordu). Sozlesme arayuzde duzeltildi; burada
        // StorageKey istege bagli (dosya yukleme henuz yok) ve yukleyen sunucuda
        // belirlenir (onceden istemciden geliyordu).
        if (request.EmployeeId == Guid.Empty)
            return BadRequest(new { message = "Çalışan zorunlu" });
        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 255)
            return BadRequest(new { message = "Doküman adı zorunlu ve en fazla 255 karakter olabilir" });
        var me = await _employees.FindMyEmployeeIdAsync(ct);
        var doc = new Document
        {
            EmployeeId = request.EmployeeId,
            Type = request.Type,
            FileName = request.FileName.Trim(),
            StorageKey = request.StorageKey?.Trim() ?? "",
            SizeBytes = Math.Max(0, request.SizeBytes ?? 0),
            ContentType = request.ContentType,
            UploadedByEmployeeId = me
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync();
        return Created($"/api/documents/{doc.Id}", doc);
    }

    /// <summary>
    /// Kaydi siler. Y28: imzali dokumanlar degistirilemez; silme yalnizca saklama suresi dolan
    /// dokumanin imhasi icin ve <c>confirmSigned=true</c> ile yapilir - imza talepleri ve kanit
    /// kayitlari dokumanla birlikte imha edilir (kanit dokuman saklandigi surece tutulur).
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireDocumentManage")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool confirmSigned = false)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id);
        if (doc is null) return NotFound();
        var evidence = await _db.SignatureEvidence.Where(e => e.DocumentId == id).ToListAsync();
        if ((doc.SignedAt is not null || evidence.Count > 0) && !confirmSigned)
            return Conflict(new
            {
                message = "Bu doküman basit elektronik imzayla imzalanmış. Silme, imza kanıtlarını da imha eder; yalnızca saklama süresi dolduysa onaylayarak silin.",
                code = "signed_document",
            });
        // Eski kanitlar burada; yeni (governance_signatures, DocumentType 'HrDocument') kanitlar ve kodlar
        // expense_documents silme tetikleyicisiyle birlikte silinir (scripts/sql/2026-10-11_signature_unify.sql).
        _db.SignatureEvidence.RemoveRange(evidence);
        _db.DocumentSignatures.RemoveRange(await _db.DocumentSignatures.Where(s => s.DocumentId == id).ToListAsync());
        _db.Documents.Remove(doc);
        await _db.SaveChangesAsync();
        // Not: MinIO'daki dosyanin silinmesi ayri bir temizlik isi olarak yapilmali.
        return NoContent();
    }
}

public record CreateDocumentRequest(
    Guid EmployeeId, DocumentType Type, string FileName, string? StorageKey,
    long? SizeBytes, string? ContentType);
