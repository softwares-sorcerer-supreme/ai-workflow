using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>What the dashboard may read of the cost log. No write method, on purpose.</summary>
public interface ICostReader
{
    /// <summary>
    /// Every cost row, oldest first. <paramref name="withParts"/> also reads what each agent spent on each model
    /// (<see cref="CostRecord.Parts"/>); only the model grouping needs it, and the dashboard polls this.
    /// </summary>
    IReadOnlyList<CostRecord> ListCost(bool withParts = false);
}
