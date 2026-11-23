using System.Text;

namespace DaffyZoom.Tds;

internal static class HexDump
{
    /// <summary>Classic 16-byte-per-row dump with offset column and ASCII gutter.</summary>
    public static void Write(StringBuilder sb, ReadOnlySpan<byte> data, string indent = "    ", int maxBytes = 4096)
    {
        int limit = Math.Min(data.Length, maxBytes);

        for (int offset = 0; offset < limit; offset += 16)
        {
            int rowLength = Math.Min(16, limit - offset);
            sb.Append(indent).Append(offset.ToString("X4")).Append("  ");

            for (int i = 0; i < 16; i++)
            {
                sb.Append(i < rowLength ? data[offset + i].ToString("X2") : "  ");
                sb.Append(i == 7 ? "  " : " ");
            }

            sb.Append(" |");
            for (int i = 0; i < rowLength; i++)
            {
                byte b = data[offset + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.Append('|').Append('\n');
        }

        if (data.Length > limit)
        {
            sb.Append(indent).Append("... ").Append(data.Length - limit).Append(" more bytes truncated\n");
        }
    }

    public static string Inline(ReadOnlySpan<byte> data, int maxBytes = 32)
    {
        var sb = new StringBuilder();
        int limit = Math.Min(data.Length, maxBytes);
        for (int i = 0; i < limit; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        if (data.Length > limit) sb.Append(" ...");
        return sb.ToString();
    }
}
