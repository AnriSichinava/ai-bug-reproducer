using BugReproducer.Api.Auth;
using BugReproducer.Api.Data;
using BugReproducer.Api.Dtos;
using BugReproducer.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BugReproducer.Api.Controllers;

[ApiController]
[Route("api/bugs")]
[Authorize]
public class BugsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<BugReportResponse>>> GetBugs(
        [FromQuery] int? projectId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var userId = User.GetUserId();
        var query = db.BugReports
            .Where(b => b.Project!.OwnerId == userId)
            .Where(b => projectId == null || b.ProjectId == projectId)
            .OrderByDescending(b => b.CreatedAtUtc);

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new BugReportResponse(b.Id, b.ProjectId, b.Title, b.Description, b.CreatedAtUtc))
            .ToListAsync();

        return Ok(new PagedResult<BugReportResponse>(items, page, pageSize, totalCount));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<BugReportResponse>> GetBug(int id)
    {
        var userId = User.GetUserId();
        var bug = await db.BugReports
            .Where(b => b.Id == id && b.Project!.OwnerId == userId)
            .SingleOrDefaultAsync();

        if (bug is null)
        {
            return NotFound();
        }

        return Ok(new BugReportResponse(bug.Id, bug.ProjectId, bug.Title, bug.Description, bug.CreatedAtUtc));
    }

    [HttpPost]
    public async Task<ActionResult<BugReportResponse>> CreateBug(CreateBugReportRequest request)
    {
        var userId = User.GetUserId();
        var projectExists = await db.Projects.AnyAsync(p => p.Id == request.ProjectId && p.OwnerId == userId);
        if (!projectExists)
        {
            return NotFound($"Project {request.ProjectId} was not found.");
        }

        var bug = new BugReport
        {
            ProjectId = request.ProjectId,
            Title = request.Title,
            Description = request.Description,
        };

        db.BugReports.Add(bug);
        await db.SaveChangesAsync();

        var response = new BugReportResponse(bug.Id, bug.ProjectId, bug.Title, bug.Description, bug.CreatedAtUtc);
        return CreatedAtAction(nameof(GetBug), new { id = bug.Id }, response);
    }
}
