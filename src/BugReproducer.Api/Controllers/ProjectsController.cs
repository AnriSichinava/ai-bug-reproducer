using BugReproducer.Api.Auth;
using BugReproducer.Api.Data;
using BugReproducer.Api.Dtos;
using BugReproducer.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BugReproducer.Api.Controllers;

[ApiController]
[Route("api/projects")]
[Authorize]
public class ProjectsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<ProjectResponse>>> GetProjects(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var userId = User.GetUserId();
        var query = db.Projects.Where(p => p.OwnerId == userId).OrderByDescending(p => p.CreatedAtUtc);

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProjectResponse(p.Id, p.Name, p.CreatedAtUtc))
            .ToListAsync();

        return Ok(new PagedResult<ProjectResponse>(items, page, pageSize, totalCount));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProjectResponse>> GetProject(int id)
    {
        var userId = User.GetUserId();
        var project = await db.Projects.SingleOrDefaultAsync(p => p.Id == id && p.OwnerId == userId);
        if (project is null)
        {
            return NotFound();
        }

        return Ok(new ProjectResponse(project.Id, project.Name, project.CreatedAtUtc));
    }

    [HttpPost]
    public async Task<ActionResult<ProjectResponse>> CreateProject(CreateProjectRequest request)
    {
        var project = new Project
        {
            Name = request.Name,
            OwnerId = User.GetUserId(),
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var response = new ProjectResponse(project.Id, project.Name, project.CreatedAtUtc);
        return CreatedAtAction(nameof(GetProject), new { id = project.Id }, response);
    }
}
