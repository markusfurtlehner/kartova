using System.Globalization;
using System.Text;
using Kartova.Core.Model;

namespace Kartova.App.Services;

/// <summary>Writes a scanned tree to disk for use in other tools.</summary>
public static class ExportService
{
    /// <summary>
    /// Writes the tree as CSV, one row per node, depth first.
    /// </summary>
    /// <remarks>
    /// Streamed rather than built in memory: a full-volume scan holds millions of nodes and
    /// materialising that as a single string would cost more than the scan itself.
    /// </remarks>
    public static void ExportCsv(FileNode root, string path)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);

        writer.WriteLine("Path,Name,Type,SizeBytes,SizeOnDiskBytes,Files,Directories,PercentOfTotal,LastModifiedUtc");

        var total = root.Size;

        foreach (var node in root.DescendantsAndSelf())
        {
            var percent = total > 0 ? (double)node.Size / total * 100 : 0;

            writer.Write(Escape(node.GetFullPath()));
            writer.Write(',');
            writer.Write(Escape(node.Name));
            writer.Write(',');
            writer.Write(Escape(DescribeType(node)));
            writer.Write(',');
            writer.Write(node.Size.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(node.SizeOnDisk.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(node.FileCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(node.DirCount.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(percent.ToString("F4", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(node.LastWriteUtcTicks == 0
                ? string.Empty
                : node.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture));
            writer.WriteLine();
        }
    }

    /// <summary>
    /// Writes the tree as nested JSON, streamed by an explicit stack so that a deep
    /// hierarchy cannot overflow the call stack the way recursion would.
    /// </summary>
    public static void ExportJson(FileNode root, string path)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);

        // Either descend into a node, or emit the closing punctuation left pending for it.
        var stack = new Stack<(FileNode Node, int ChildIndex, bool Written)>();
        stack.Push((root, 0, false));

        while (stack.Count > 0)
        {
            var (node, childIndex, written) = stack.Pop();

            if (!written)
            {
                WriteNodeHeader(writer, node);
                written = true;
            }

            var children = node.Children;
            if (children is not null && childIndex < children.Length)
            {
                if (childIndex == 0) writer.Write(",\"children\":[");
                else writer.Write(',');

                stack.Push((node, childIndex + 1, written));
                stack.Push((children[childIndex], 0, false));
                continue;
            }

            if (children is { Length: > 0 }) writer.Write(']');
            writer.Write('}');
        }

        writer.WriteLine();
    }

    private static void WriteNodeHeader(TextWriter writer, FileNode node)
    {
        writer.Write("{\"name\":");
        WriteJsonString(writer, node.Name);
        writer.Write(",\"type\":\"");
        writer.Write(DescribeType(node));
        writer.Write("\",\"size\":");
        writer.Write(node.Size.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"sizeOnDisk\":");
        writer.Write(node.SizeOnDisk.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"files\":");
        writer.Write(node.FileCount.ToString(CultureInfo.InvariantCulture));
        writer.Write(",\"directories\":");
        writer.Write(node.DirCount.ToString(CultureInfo.InvariantCulture));

        if (node.LastWriteUtcTicks != 0)
        {
            writer.Write(",\"modified\":\"");
            writer.Write(node.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture));
            writer.Write('"');
        }
    }

    private static string DescribeType(FileNode node)
    {
        if (node.HasFlag(NodeFlags.FreeSpace)) return "free";
        if (node.HasFlag(NodeFlags.Unknown)) return "unknown";
        if (node.HasFlag(NodeFlags.ReparsePoint)) return "link";
        return node.IsDirectory ? "directory" : "file";
    }

    private static string Escape(string value)
    {
        // Quote only when necessary, and double any embedded quotes, per RFC 4180.
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static void WriteJsonString(TextWriter writer, string value)
    {
        writer.Write('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': writer.Write("\\\""); break;
                case '\\': writer.Write("\\\\"); break;
                case '\n': writer.Write("\\n"); break;
                case '\r': writer.Write("\\r"); break;
                case '\t': writer.Write("\\t"); break;
                default:
                    if (c < 0x20) writer.Write($"\\u{(int)c:x4}");
                    else writer.Write(c);
                    break;
            }
        }
        writer.Write('"');
    }
}
