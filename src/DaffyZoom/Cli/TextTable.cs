namespace DaffyZoom.Cli;

/// <summary>A plain aligned grid for lists of users and sessions. Wide cells are cut, not wrapped.</summary>
internal static class TextTable
{
    private const int MaxCellWidth = 48;

    public static void Write(TextWriter output, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        int[] widths = headers.Select(h => h.Length).ToArray();
        foreach (var row in rows)
            for (int i = 0; i < widths.Length; i++)
                widths[i] = Math.Min(MaxCellWidth, Math.Max(widths[i], Cell(row, i).Length));

        output.WriteLine();
        output.WriteLine("  " + string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        output.WriteLine("  " + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
            output.WriteLine("  " + string.Join("  ", widths.Select((w, i) => Fit(Cell(row, i), w))).TrimEnd());
        output.WriteLine();
    }

    private static string Cell(string[] row, int index) => index < row.Length ? row[index] : "";

    private static string Fit(string value, int width) =>
        value.Length <= width ? value.PadRight(width) : value[..(width - 1)] + "~";
}
