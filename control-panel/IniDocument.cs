using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace YMacType.Settings
{
internal sealed class IniDocument
{
    private readonly List<string> _lines;

    private IniDocument(IEnumerable<string> lines)
    {
        _lines = lines.ToList();
    }

    public static IniDocument Load(string path)
    {
        return new IniDocument(File.Exists(path)
            ? File.ReadAllLines(path)
            : Array.Empty<string>());
    }

    public string Get(string section, string key, string fallback = "")
    {
        var bounds = FindSection(section);
        if (bounds.start < 0)
            return fallback;
        for (var index = bounds.start + 1; index < bounds.end; ++index)
        {
            var line = _lines[index].Trim();
            if (line.StartsWith(";") || line.StartsWith("#"))
                continue;
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            if (line.Substring(0, separator).Trim().Equals(
                    key, StringComparison.OrdinalIgnoreCase))
                return line.Substring(separator + 1).Trim();
        }
        return fallback;
    }

    public void Set(string section, string key, string value)
    {
        var bounds = FindSection(section);
        if (bounds.start < 0)
        {
            if (_lines.Count > 0 && _lines[_lines.Count - 1].Length != 0)
                _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        for (var index = bounds.start + 1; index < bounds.end; ++index)
        {
            var line = _lines[index].Trim();
            var separator = line.IndexOf('=');
            if (separator > 0 &&
                line.Substring(0, separator).Trim().Equals(
                    key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[index] = $"{key}={value}";
                return;
            }
        }
        _lines.Insert(bounds.end, $"{key}={value}");
    }

    public void Remove(string section, string key)
    {
        var bounds = FindSection(section);
        if (bounds.start < 0)
            return;
        for (var index = bounds.end - 1; index > bounds.start; --index)
        {
            var line = _lines[index].Trim();
            var separator = line.IndexOf('=');
            if (separator > 0 &&
                line.Substring(0, separator).Trim().Equals(
                    key, StringComparison.OrdinalIgnoreCase))
                _lines.RemoveAt(index);
        }
    }

    public IReadOnlyList<string> GetEntries(string section)
    {
        var bounds = FindSection(section);
        if (bounds.start < 0)
            return Array.Empty<string>();
        return _lines
            .Skip(bounds.start + 1)
            .Take(bounds.end - bounds.start - 1)
            .Select(line => line.Trim())
            .Where(line =>
                line.Length > 0 &&
                !line.StartsWith(";") &&
                !line.StartsWith("#") &&
                line.IndexOf('=') < 0)
            .ToList();
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllLines(
            temporary,
            _lines,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(path))
            File.Replace(temporary, path, null);
        else
            File.Move(temporary, path);
    }

    private (int start, int end) FindSection(string section)
    {
        var header = $"[{section}]";
        var start = _lines.FindIndex(line =>
            line.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
            return (-1, -1);
        var end = _lines.FindIndex(start + 1, line =>
            line.TrimStart().StartsWith("[") &&
            line.TrimEnd().EndsWith("]"));
        return (start, end < 0 ? _lines.Count : end);
    }
}
}
