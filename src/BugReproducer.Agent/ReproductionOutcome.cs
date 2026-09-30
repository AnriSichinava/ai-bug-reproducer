namespace BugReproducer.Agent;

public enum ReproductionVerdict
{
    Reproduced,
    NotReproduced,
    Inconclusive,
}

/// A single claim the model makes about what it saw, tied to the step that saw it.
/// The observation is the model's words; StepNumber is what makes it checkable against
/// the recorded tool output for that step.
public record EvidenceItem(int StepNumber, string Observation);

/// The result of a run. Reasoning is inference -- the model's interpretation. Evidence
/// points at steps whose recorded tool output is fact. Keeping them apart is the whole
/// point: the UI can show a verdict without presenting the model's story as truth.
public record ReproductionOutcome(
    ReproductionVerdict Verdict,
    string Reasoning,
    IReadOnlyList<EvidenceItem> Evidence,
    int StepsUsed,
    StopReason StopReason,
    /// Evidence citing a step number that was never recorded. Empty in a healthy run;
    /// non-empty means the model referenced something that does not exist, which is
    /// worth surfacing rather than hiding.
    IReadOnlyList<EvidenceItem> UnverifiableEvidence);

public enum StopReason
{
    /// The model called the conclude tool -- the only clean ending.
    ModelConcluded,

    /// The step budget ran out before the model reached a conclusion.
    StepBudgetExhausted,

    /// The model stopped requesting tools without concluding.
    ModelStoppedWithoutConcluding,

    /// The model declined the request.
    Refused,
}
