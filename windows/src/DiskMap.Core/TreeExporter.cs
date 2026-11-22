using System.Text;
using System.Text.Json;

namespace DiskMap.Core;

/// <summary>
/// Tree export formats (TASK-058 port): nested JSON, NDJSON, RFC 4180
/// CSV, and ncdu's `-o` layout. Pure string building over a FileTree —
/// the caller supplies paths (Model.PathOf) so this stays UI-free.
/// </summary>
public static class TreeExporter
{
    public enum Format { Json, Ndjson, Csv, Ncdu }

    public static string FileExtension(Format format) => format switch
    {
        Format.Json => ".json",
        Format.Ndjson => ".ndjson",
        Format.Csv => ".csv",
        Format.Ncdu => ".ncdu",
        _ => ".txt",
    };

    public static string Export(
        FileTree tree, long[] totals, string rootPath, Format format)
    {
        return format switch
        {
            Format.Json => ToJson(tree, totals, rootPath),
            Format.Ndjson => ToNdjson(tree, totals, rootPath),
            Format.Csv => ToCsv(tree, totals, rootPath),
            Format.Ncdu => ToNcdu(tree, totals, rootPath),
            _ => ToJson(tree, totals, rootPath),
        };
    }

    private static string PathOf(FileTree tree, int id, string rootPath)
    {
        var parts = new List<string>();
        for (int p = id; p > 0; p = tree.Parent[p]) parts.Add(tree.NameOf(p));
        parts.Reverse();
        return rootPath.TrimEnd('\\') + '\\' + string.Join('\\', parts);
    }

    private static string ToJson(FileTree tree, long[] totals, string rootPath)
    {
        // Iterative: paths can nest thousands deep (NTFS allows ~32k
        // chars), which overflows a recursive writer's stack — and each
        // level is two JSON levels, past Utf8JsonWriter's default 1000.
        var buffer = new StringBuilder(totals.Length * 48);
        var w = new Utf8JsonWriter(new JsonBufferWriterAdapter(buffer),
            new JsonWriterOptions { MaxDepth = int.MaxValue });
        // Next sibling to write in each open children array; -1 closes the array and its parent.
        var pending = new Stack<int>();
        void Open(int id)
        {
            w.WriteStartObject();
            w.WriteString("name", id == 0 ? rootPath : tree.NameOf(id));
            w.WriteNumber("logicalBytes", tree.LogicalSize[id]);
            w.WriteNumber("allocatedBytes", totals[id]);
            w.WriteBoolean("directory", tree.IsDirectory[id]);
            if (tree.ModifiedDay[id] > 0)
                w.WriteNumber("modifiedDay", tree.ModifiedDay[id]);
            if (tree.CreatedDay[id] > 0)
                w.WriteNumber("createdDay", tree.CreatedDay[id]);
            if (tree.Flags[id] != 0)
                w.WriteString("flags", Convert.ToHexString([tree.Flags[id]]));
            int child = tree.FirstChild[id];
            if (child == -1)
            {
                w.WriteEndObject();
                return;
            }
            w.WriteStartArray("children");
            pending.Push(child);
        }
        Open(0);
        while (pending.TryPop(out int id))
        {
            if (id == -1)
            {
                w.WriteEndArray();
                w.WriteEndObject();
                continue;
            }
            pending.Push(tree.NextSibling[id]);
            Open(id);
        }
        w.Flush();
        return buffer.ToString();
    }

    private static string ToNdjson(FileTree tree, long[] totals, string rootPath)
    {
        var sb = new StringBuilder();
        for (int id = 0; id < tree.Count; id++)
        {
            var row = new Dictionary<string, object?>
            {
                ["path"] = PathOf(tree, id, rootPath),
                ["logicalBytes"] = tree.LogicalSize[id],
                ["allocatedBytes"] = totals[id],
                ["directory"] = tree.IsDirectory[id],
                ["modifiedDay"] = tree.ModifiedDay[id] > 0 ? tree.ModifiedDay[id] : null,
            };
            sb.AppendLine(JsonSerializer.Serialize(row));
        }
        return sb.ToString();
    }

    private static string ToCsv(FileTree tree, long[] totals, string rootPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("path,name,logicalBytes,allocatedBytes,directory,modifiedDay,createdDay,flags");
        for (int id = 0; id < tree.Count; id++)
        {
            sb.Append(Csv(PathOf(tree, id, rootPath)));
            sb.Append(',');
            sb.Append(Csv(tree.NameOf(id)));
            sb.Append(',');
            sb.Append(tree.LogicalSize[id]); sb.Append(',');
            sb.Append(totals[id]); sb.Append(',');
            sb.Append(tree.IsDirectory[id] ? "true" : "false"); sb.Append(',');
            sb.Append(tree.ModifiedDay[id]); sb.Append(',');
            sb.Append(tree.CreatedDay[id]); sb.Append(',');
            sb.Append(tree.Flags[id]);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// RFC 4180: quote when the field holds a comma, quote, or newline —
    /// after neutralising a leading formula character (OWASP CSV
    /// injection), so a file named "=cmd|…" opens as text in a spreadsheet.
    /// </summary>
    private static string Csv(string field)
    {
        if (field.Length > 0 && field[0] is '=' or '+' or '-' or '@' or '\t' or '\r') field = "'" + field;
        return field.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? field
            : "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// ncdu `-o` JSON: [[1,0,{progver}],[rootEntry]] where an entry is
    /// {name,asize,dsize} followed by its children array for dirs.
    /// </summary>
    private static string ToNcdu(FileTree tree, long[] totals, string rootPath)
    {
        var sb = new StringBuilder(totals.Length * 32);
        sb.Append("[1,0,{\"progname\":\"diskmap\"},");
        // Iterative for the same reason as ToJson. Each frame is the next
        // entry to write in an open children array; -1 closes that array.
        var pending = new Stack<(int Id, bool First)>();
        void Open(int id)
        {
            sb.Append("{\"name\":");
            sb.Append(JsonSerializer.Serialize(id == 0 ? rootPath : tree.NameOf(id)));
            sb.Append(",\"asize\":").Append(tree.LogicalSize[id]);
            sb.Append(",\"dsize\":").Append(totals[id]);
            sb.Append('}');
            int child = tree.FirstChild[id];
            if (child == -1) return;
            sb.Append(",[");
            pending.Push((child, true));
        }
        Open(0);
        while (pending.TryPop(out var next))
        {
            if (next.Id == -1)
            {
                sb.Append(']');
                continue;
            }
            if (!next.First) sb.Append(',');
            pending.Push((tree.NextSibling[next.Id], false));
            Open(next.Id);
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Copy-paths quoting for Windows: only when the string needs it —
    /// space or a cmd metachar, wrapped in double quotes (Windows has no
    /// single-quote escape; internal quotes double up is not valid cmd —
    /// so just refuse to quote those and pass through verbatim).
    /// </summary>
    public static string QuotePathIfNeeded(string path) =>
        path.IndexOfAny([' ', '\t', '&', '(', ')', '^']) >= 0 && !path.Contains('"')
            ? "\"" + path + "\""
            : path;

    /// <summary>
    /// Utf8JsonWriter needs an IBufferWriter; the StringBuilder version
    /// keeps the export a single allocation-friendly string.
    /// </summary>
    private sealed class JsonBufferWriterAdapter : System.Buffers.IBufferWriter<byte>
    {
        private readonly StringBuilder _sb;
        private byte[] _buffer = new byte[1 << 14];

        public JsonBufferWriterAdapter(StringBuilder sb) => _sb = sb;

        public void Advance(int count)
        {
            _sb.Append(Encoding.UTF8.GetString(_buffer, 0, count));
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length) _buffer = new byte[sizeHint];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length) _buffer = new byte[sizeHint];
            return _buffer;
        }
    }
}
