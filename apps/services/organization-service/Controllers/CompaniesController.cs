using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrganizationService.Data;
using OrganizationService.Models;

namespace OrganizationService.Controllers;

[ApiController]
[Route("api/companies")]
[Authorize]
public class CompaniesController : ControllerBase
{
    private readonly OrganizationDbContext _db;

    public CompaniesController(OrganizationDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var companies = await _db.Companies.Include(c => c.Departments).ToListAsync();
        return Ok(companies);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var company = await _db.Companies.Include(c => c.Departments).FirstOrDefaultAsync(c => c.Id == id);
        return company is null ? NotFound() : Ok(company);
    }

    [Authorize(Policy = "RequireHrAdmin")]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCompanyRequest request)
    {
        var company = new Company { Name = request.Name, TaxNumber = request.TaxNumber };
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = company.Id }, company);
    }
}

public record CreateCompanyRequest(string Name, string? TaxNumber);
