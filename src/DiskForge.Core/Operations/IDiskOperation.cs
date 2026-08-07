using DiskForge.Core.Model;
using DiskForge.Core.Planning;

namespace DiskForge.Core.Operations;

/// <summary>
/// The single contract every disk operation implements (§3). The same Validate → Simulate →
/// Execute → Verify pipeline backs both dry-run preview and real Apply, so there is exactly one
/// code path and no way to execute something that was never validated/simulated.
/// </summary>
public interface IDiskOperation
{
    /// <summary>Human summary + DESTRUCTIVE flag for the pending list and confirm dialog.</summary>
    OperationDescriptor Describe();

    /// <summary>Capabilities the target drive must support; gated in <see cref="Validate"/> (§1A.7).</summary>
    DriveCapability RequiredCapabilities();

    /// <summary>Preflight: capability, encryption, alignment, space, system-disk checks. Never writes.</summary>
    ValidationResult Validate(SystemState state);

    /// <summary>Produce the full execution plan without touching the disk (dry-run).</summary>
    SimulationResult Simulate(SystemState state);

    /// <summary>Execute for real. Only reachable after a valid Validate + explicit user Apply.</summary>
    Task<OpResult> ExecuteAsync(IProgress<OpProgress> progress, CancellationToken ct);

    /// <summary>Re-read state and confirm the operation's post-conditions hold.</summary>
    Task<VerifyResult> VerifyAsync();

    /// <summary>
    /// How this operation would change the disk map, so the dashboard can draw the staged batch before
    /// it runs (queued deletes shown as free space, queued creates shown in it). Preview only: it is
    /// never consulted by Validate or Execute, and an operation that declares nothing simply doesn't
    /// appear in the preview.
    /// </summary>
    IReadOnlyList<LayoutChange> PlanLayoutChanges() => Array.Empty<LayoutChange>();
}
