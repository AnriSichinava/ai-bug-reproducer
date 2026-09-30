using BugReproducer.Api.Auth;
using BugReproducer.Api.Data;
using BugReproducer.Api.Dtos;
using BugReproducer.Api.Models;
using BugReproducer.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BugReproducer.Api.Controllers;

[ApiController]
[Route("api/bugs")]
[Authorize]
public class BugsController(AppDbContext db, IBackgroundTaskQueue taskQueue) : ControllerBase
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

    /// Queues a reproduction attempt. The agent itself runs later and asynchronously,
    /// so this returns 202 with a run the caller can poll, not the finished result.
    [HttpPost("{id:int}/reproduce")]
    public async Task<ActionResult<AgentRunResponse>> Reproduce(int id)
    {
        var userId = User.GetUserId();
        var bugExists = await db.BugReports.AnyAsync(b => b.Id == id && b.Project!.OwnerId == userId);
        if (!bugExists)
        {
            return NotFound();
        }

        var hasActiveRun = await db.AgentRuns.AnyAsync(r =>
            r.BugReportId == id &&
            (r.Status == AgentRunStatus.Pending || r.Status == AgentRunStatus.Running));
        if (hasActiveRun)
        {
            return Conflict("A reproduction run for this bug is already in progress.");
        }

        var run = new AgentRun { BugReportId = id };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();

        // The 202 below is returned before this ever runs -- the caller polls
        // GET /api/agent-runs/{id} for progress, it doesn't wait on this request.
        await taskQueue.QueueAsync(async (services, ct) =>
        {
            var executor = services.GetRequiredService<AgentRunExecutor>();
            await executor.ExecuteAsync(run.Id, ct);
        });

        var response = new AgentRunResponse(
            run.Id, run.BugReportId, run.Status, run.Verdict, run.VerdictReason,
            run.FailureReason, run.StartedAtUtc, run.CompletedAtUtc);

        return AcceptedAtAction(
            nameof(AgentRunsController.GetAgentRun),
            "AgentRuns",
            new { id = run.Id },
            response);
    }
}
