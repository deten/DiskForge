using DiskForge.Core.Model;

namespace DiskForge.Core.Operations;

/// <summary>Human-facing summary of an operation for the pending list and confirm dialog.</summary>
public sealed record OperationDescriptor(
    string Title,
    string Details,
    bool IsDestructive,
    int TargetDiskNumber);

/// <summary>Preflight outcome. An operation must not run when <see cref="IsValid"/> is false.</summary>
public sealed class ValidationResult
{
    public bool IsValid { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    /// <summary>Capabilities the target drive was missing, when validation failed on capability gating.</summary>
    public DriveCapability MissingCapabilities { get; private init; } = DriveCapability.None;

    public static ValidationResult Ok(params string[] warnings) =>
        new() { IsValid = true, Warnings = warnings };

    public static ValidationResult Fail(params string[] errors) =>
        new() { IsValid = false, Errors = errors };

    public static ValidationResult MissingCapability(DriveCapability missing, string reason) =>
        new() { IsValid = false, Errors = new[] { reason }, MissingCapabilities = missing };
}

/// <summary>The full plan an operation would execute — produced with zero writes (dry-run, §1).</summary>
public sealed class SimulationResult
{
    public bool Feasible { get; init; }
    public IReadOnlyList<string> PlannedSteps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? BlockingReason { get; init; }
}

public sealed record OpProgress(string Step, double Fraction, string? Detail = null);

public sealed class OpResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Elapsed { get; init; }

    public static OpResult Ok(TimeSpan elapsed) => new() { Success = true, Elapsed = elapsed };
    public static OpResult Failed(string error) => new() { Success = false, Error = error };
}

public sealed class VerifyResult
{
    public bool Verified { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();

    public static VerifyResult Pass() => new() { Verified = true };
    public static VerifyResult Fail(params string[] findings) => new() { Verified = false, Findings = findings };
}
