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
    private readonly Encoding _encoding;

    // The core's INI parser only recognises UTF-16 LE (with BOM) and UTF-8 with
    // BOM. Anything else is decoded with the system ANSI code page, which turns
    // Chinese font names into mojibake. New files are therefore always written
    // as UTF-8 with BOM, and UTF-16 files keep their encoding.
    private static readonly Encoding Utf8WithBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private IniDocument(IEnumerable<string> lines, Encoding encoding)
    {
        _lines = lines.ToList();
        _encoding = encoding;
    }

    private static readonly Encoding Utf16LeWithBom =
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

    /// <param name="preferUtf16">
    /// Write the file as UTF-16 LE. Used for MacType.ini, which legacy
    /// components read through the Win32 profile API (UTF-16 or ANSI only).
    /// </param>
    public static IniDocument Load(string path, bool preferUtf16 = false)
    {
        var target = preferUtf16 ? Utf16LeWithBom : Utf8WithBom;
        if (!File.Exists(path))
            return new IniDocument(Array.Empty<string>(), target);
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        string text;
        try
        {
            text = encoding.GetString(
                bytes, preambleLength, bytes.Length - preambleLength);
        }
        catch (DecoderFallbackException)
        {
            // Not valid UTF-8: a legacy file in the system ANSI code page,
            // which is exactly how the core reads BOM-less files.
            text = Encoding.Default.GetString(bytes);
        }
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        // UTF-16 files keep UTF-16 (LE: big-endian is not read by the core).
        return new IniDocument(
            lines,
            encoding is UnicodeEncoding ? Utf16LeWithBom : target);
    }

    /// <summary>
    /// True when the core would decode this file correctly: it has a UTF-8 or
    /// UTF-16 BOM, or it only contains ASCII.
    /// </summary>
    public static bool HasSafeEncoding(string path)
    {
        if (!File.Exists(path))
            return true;
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        if (encoding is UnicodeEncoding && bytes[0] == 0xFE)
            return false;   // UTF-16 BE is not understood by the core
        return preambleLength > 0 || bytes.All(value => value < 0x80);
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            preambleLength = 3;
            return Utf8WithBom;
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }
        preambleLength = 0;
        // BOM-less files written by older builds of this panel are UTF-8;
        // decoding is strict so ANSI files fall back in Load.
        return new UTF8Encoding(false, throwOnInvalidBytes: true);
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

    public void AddEntry(string section, string entry)
    {
        if (GetEntries(section).Any(item =>
                item.Equals(entry, StringComparison.OrdinalIgnoreCase)))
            return;
        EnsureSection(section);
        var bounds = FindSection(section);
        _lines.Insert(bounds.start + 1, entry);
    }

    public void RemoveEntry(string section, string entry)
    {
        var bounds = FindSection(section);
        if (bounds.start < 0)
            return;
        for (var index = bounds.end - 1; index > bounds.start; --index)
        {
            if (_lines[index].Trim().Equals(
                    entry,
                    StringComparison.OrdinalIgnoreCase))
                _lines.RemoveAt(index);
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(
            temporary,
            string.Join("\r\n", _lines) + "\r\n",
            _encoding);
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

    private void EnsureSection(string section)
    {
        if (FindSection(section).start >= 0)
            return;
        if (_lines.Count > 0 && _lines[_lines.Count - 1].Length != 0)
            _lines.Add("");
        _lines.Add($"[{section}]");
    }
}
}
