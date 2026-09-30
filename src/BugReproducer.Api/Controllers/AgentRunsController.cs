using BugReproducer.Api.Auth;
using BugReproducer.Api.Data;
using BugReproducer.Api.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BugReproducer.Api.Controllers;

[ApiController]
[Route("api/agent-runs")]
[Authorize]
public class AgentRunsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<AgentRunResponse>>> GetAgentRuns(
        [FromQuery] int? bugId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var userId = User.GetUserId();
        var query = db.AgentRuns
            .Where(r => r.BugReport!.Project!.OwnerId == userId)
            .Where(r => bugId == null || r.BugReportId == bugId)
            .OrderByDescending(r => r.StartedAtUtc);

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new AgentRunResponse(
                r.Id, r.BugReportId, r.Status, r.Verdict, r.VerdictReason,
                r.FailureReason, r.StartedAtUtc, r.CompletedAtUtc))
            .ToListAsync();

        return Ok(new PagedResult<AgentRunResponse>(items, page, pageSize, totalCount));
    }

    /// Returns the run with its full step history, which is what makes a verdict auditable.
    [HttpGet("{id:int}")]
    public async Task<ActionResult<AgentRunDetailResponse>> GetAgentRun(int id)
    {
        var userId = User.GetUserId();
        var run = await db.AgentRuns
            .Where(r => r.Id == id && r.BugReport!.Project!.OwnerId == userId)
            .Select(r => new AgentRunDetailResponse(
                r.Id, r.BugReportId, r.Status, r.Verdict, r.VerdictReason,
                r.FailureReason, r.StartedAtUtc, r.CompletedAtUtc,
                r.Steps
                    .OrderBy(s => s.StepNumber)
                    .Select(s => new AgentStepResponse(
                        s.Id, s.StepNumber, s.Reasoning, s.ToolName, s.ToolInputJson,
                        s.ToolOutputJson, s.ToolErrorMessage, s.CreatedAtUtc))
                    .ToList()))
            .SingleOrDefaultAsync();

        if (run is null)
        {
            return NotFound();
        }

        return Ok(run);
    }
}
