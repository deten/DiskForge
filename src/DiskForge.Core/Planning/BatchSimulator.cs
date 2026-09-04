using DiskForge.Core.Model;
using DiskForge.Core.Operations;

namespace DiskForge.Core.Planning;

/// <summary>One operation's place in a simulated batch.</summary>
public sealed record SimulatedOperation(
    int Index,
    IDiskOperation Operation,
    OperationDescriptor Descriptor,
    SimulationResult Result);

/// <summary>
/// Runs a staged batch through Validate and Simulate, in order, with zero writes.
///
/// Each operation is simulated against the layout the operations <i>before</i> it would leave behind,
/// which is the same order <c>OperationExecutor</c> uses and the same projection the dashboard draws.
/// That is what lets a create that depends on an earlier delete report as feasible, and what makes a
/// create whose delete was removed report as blocked instead of appearing to work.
///
/// This is the dry-run half of the Validate → Simulate → Execute → Verify contract. It is pure: no
/// disk access, no side effects, and nothing here is consulted when the batch really runs. The real
/// Apply still re-validates every operation against a fresh capture immediately before writing.
/// </summary>
public static class BatchSimulator
{
    public static IReadOnlyList<SimulatedOperation> Simulate(SystemState actual, IReadOnlyList<IDiskOperation> staged)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(staged);

        var results = new List<SimulatedOperation>(staged.Count);

        for (var i = 0; i < staged.Count; i++)
        {
            var op = staged[i];
            var before = LayoutProjector.Project(actual, staged.Take(i).ToList()).Projected;

            SimulationResult result;
            try
            {
                result = op.Simulate(before);
            }
            catch (Exception ex)
            {
                // A simulation that throws is still information: the plan cannot be trusted.
                result = new SimulationResult { Feasible = false, BlockingReason = ex.Message };
            }

            results.Add(new SimulatedOperation(i, op, op.Describe(), result));
        }

        return results;
    }

    /// <summary>True when every operation in the batch simulated as feasible.</summary>
    public static bool AllFeasible(IReadOnlyList<SimulatedOperation> simulated)
        => simulated.All(s => s.Result.Feasible);
}
