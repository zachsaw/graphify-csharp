namespace Graphify.CSharp.Domain;

public sealed record SourceLocation
{
    public SourceLocation(string filePath, int line, int column)
    {
        FilePath = CanonicalText.NormalizePath(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(line, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);

        Line = line;
        Column = column;
    }

    public string FilePath { get; }

    public int Line { get; }

    public int Column { get; }
}
