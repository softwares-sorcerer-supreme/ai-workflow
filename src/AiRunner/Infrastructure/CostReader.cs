using AiRunner.Core;

namespace AiRunner.Infrastructure;

/// <summary>What the dashboard may read of the cost log. No write method, on purpose.</summary>
public interface ICostReader
{
    /// <summary>Every cost row, oldest first.</summary>
    IReadOnlyList<CostRecord> ListCost();
}
