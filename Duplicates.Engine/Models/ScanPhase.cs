namespace Duplicates.Engine.Models;

public enum ScanPhase
{
    Enumerating,
    GroupingBySize,
    PartialHashing,
    FullHashing,
    Verifying,
    Done,
}
