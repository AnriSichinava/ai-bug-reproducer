using AgentLib = BugReproducer.Agent;
using BugReproducer.Api.Data;
using BugReproducer.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BugReproducer.Api.Services;

/// The actual work behind a queued reproduction. Pulled out of the controller/queue
/// plumbing into its own class specifically so it can be unit-tested directly --
/// constructing this and calling ExecuteAsync doesn't require going through HTTP or the
/// background queue at all.
public class AgentRunExecutor(AppDbContext db, AgentLib.ReproductionAgent agent)
{
    public async Task ExecuteAsync(int agentRunId, CancellationToken cancellationToken = default)
    {
        var run = await db.AgentRuns
            .Include(r => r.BugReport)
            .SingleAsync(r => r.Id == agentRunId, cancellationToken);

        run.Status = AgentRunStatus.Running;
        await db.SaveChangesAsync(cancellationToken);

        var bugContext = new AgentLib.BugReportContext(run.BugReport!.Title, run.BugReport.Description);
        var sink = new EfAgentStepSink(db, agentRunId);

        try
        {
            var outcome = await agent.RunAsync(bugContext, sink, cancellationToken);

            run.Status = AgentRunStatus.Completed;
            run.Verdict = MapVerdict(outcome.Verdict);
            run.VerdictReason = outcome.Reasoning;
        }
        catch (Exception ex)
        {
            // A run that crashed is Failed, not Completed-with-Inconclusive: the two are
            // different facts. Failed means the agent itself broke; Inconclusive means
            // it ran fine but couldn't establish an answer.
            run.Status = AgentRunStatus.Failed;
            run.FailureReason = ex.Message;
        }

        run.CompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static ReproductionVerdict MapVerdict(AgentLib.ReproductionVerdict verdict) => verdict switch
    {
        AgentLib.ReproductionVerdict.Reproduced => ReproductionVerdict.Reproduced,
        AgentLib.ReproductionVerdict.NotReproduced => ReproductionVerdict.NotReproduced,
        _ => ReproductionVerdict.Inconclusive,
    };
}
