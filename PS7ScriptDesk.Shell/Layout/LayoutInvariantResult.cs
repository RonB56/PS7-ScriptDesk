namespace PS7ScriptDesk.Shell.Layout;

public sealed record LayoutInvariantViolation(string Code, string Message, bool IsError = true);

public sealed class LayoutInvariantResult
{
    public LayoutInvariantResult(IReadOnlyList<LayoutInvariantViolation> violations, LayoutBudgetResult budget)
    {
        Violations = violations;
        Budget = budget;
    }

    public IReadOnlyList<LayoutInvariantViolation> Violations { get; }

    public LayoutBudgetResult Budget { get; }

    public bool IsValid => Violations.All(violation => !violation.IsError);

    public bool HasErrors => Violations.Any(violation => violation.IsError);
}
