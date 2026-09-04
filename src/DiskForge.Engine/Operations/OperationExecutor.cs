using DiskForge.Core.Model;
using DiskForge.Core.Operations;
using Serilog;

namespace DiskForge.Engine.Operations;

public sealed class OperationRunResult
{
    public required IDiskOperation Operation { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public VerifyResult? Verify { get; init; }

    /// <summary>Output the operation wants shown (a chkdsk transcript, for instance), success or not.</summary>
    public string? Report { get; init; }
}

public sealed record ApplyProgress(int OperationIndex, int OperationCount, string Title, OpProgress Step);

/// <summary>
/// Runs a batch of staged operations with a re-validate → execute → verify pass each, aborting the
/// remaining batch on the first failure. This is the only path that executes destructive work.
/// </summary>
public sealed class OperationExecutor
{
    private readonly SystemInspector _inspector;

    public OperationExecutor(SystemInspector? inspector = null) => _inspector = inspector ?? new SystemInspector();

    public async Task<IReadOnlyList<OperationRunResult>> ApplyAsync(
        IReadOnlyList<IDiskOperation> operations,
        IProgress<ApplyProgress> progress,
        CancellationToken ct)
    {
        var results = new List<OperationRunResult>();

        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            var title = op.Describe().Title;
            ct.ThrowIfCancellationRequested();

            // Re-validate against a fresh snapshot right before executing.
            var state = _inspector.Capture();
            var validation = op.Validate(state);
            if (!validation.IsValid)
            {
                results.Add(new OperationRunResult
                {
                    Operation = op, Success = false,
                    Error = "Validation failed: " + string.Join(" ", validation.Errors)
                });
                Log.Warning("Aborting batch at op {Index} ({Title}): validation failed", i, title);
                break;
            }

            var stepProgress = new Progress<OpProgress>(sp =>
                progress.Report(new ApplyProgress(i, operations.Count, title, sp)));

            OpResult opResult;
            try
            {
                opResult = await op.ExecuteAsync(stepProgress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error(ex, "Operation {Title} threw", title);
                opResult = OpResult.Failed(ex.Message);
            }

            if (!opResult.Success)
            {
                results.Add(new OperationRunResult
                {
                    Operation = op, Success = false, Error = opResult.Error, Report = opResult.Report
                });
                Log.Warning("Aborting batch at op {Index} ({Title}): {Error}", i, title, opResult.Error);
                break;
            }

            VerifyResult verify;
            try { verify = await op.VerifyAsync().ConfigureAwait(false); }
            catch (Exception ex) { verify = VerifyResult.Fail(ex.Message); }

            results.Add(new OperationRunResult
            {
                Operation = op, Success = true,
                Verify = verify,
                Report = opResult.Report,
                Error = verify.Verified ? null : "Completed but verification warned: " + string.Join(" ", verify.Findings)
            });
        }

        return results;
    }
}
