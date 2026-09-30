using AgentLib = BugReproducer.Agent;
using BugReproducer.Api.Auth;
using BugReproducer.Api.Data;
using BugReproducer.Api.Dtos;
using BugReproducer.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BugReproducer.Api.Controllers;

[ApiController]
[Route("api/agent-runs")]
[Authorize]
public class AgentRunsController(AppDbContext db, AgentLib.IPlaywrightTestRunner testRunner) : ControllerBase
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

    /// Generates a Playwright regression test from this run's recorded trace and
    /// immediately executes it. Only valid for a run that concluded REPRODUCED --
    /// generating a test from a run that never confirmed the bug would have nothing
    /// real to encode.
    [HttpPost("{id:int}/generate-test")]
    public async Task<ActionResult<GenerateTestResponse>> GenerateTest(int id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var run = await db.AgentRuns
            .Include(r => r.BugReport)
            .Include(r => r.Steps)
            .Where(r => r.Id == id && r.BugReport!.Project!.OwnerId == userId)
            .SingleOrDefaultAsync(cancellationToken);

        if (run is null)
        {
            return NotFound();
        }

        if (run.Verdict != ReproductionVerdict.Reproduced)
        {
            return Conflict("A regression test can only be generated for a run that concluded REPRODUCED.");
        }

        var bugContext = new AgentLib.BugReportContext(run.BugReport!.Title, run.BugReport.Description);
        var stepRecords = run.Steps
            .OrderBy(s => s.StepNumber)
            .Select(s => new AgentLib.AgentStepRecord(
                s.StepNumber, s.Reasoning, s.ToolName, s.ToolInputJson, s.ToolOutputJson, s.ToolErrorMessage))
            .ToList();

        var source = AgentLib.PlaywrightTestGenerator.Generate(bugContext, stepRecords);
        var execution = await testRunner.RunAsync(source, cancellationToken);

        var generatedTest = new GeneratedTest
        {
            AgentRunId = run.Id,
            FileName = source.FileName,
            SourceCode = source.SourceCode,
        };
        db.GeneratedTests.Add(generatedTest);
        await db.SaveChangesAsync(cancellationToken);

        var testRun = new TestRun
        {
            GeneratedTestId = generatedTest.Id,
            Passed = execution.Passed,
            Output = execution.Output,
        };
        db.TestRuns.Add(testRun);
        await db.SaveChangesAsync(cancellationToken);

        return Ok(new GenerateTestResponse(
            generatedTest.Id, generatedTest.FileName, generatedTest.SourceCode,
            testRun.Id, testRun.Passed, testRun.Output));
    }
}
