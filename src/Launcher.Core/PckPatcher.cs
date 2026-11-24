using System.Security.Cryptography;
using System.Text;

namespace Launcher.Core;

// Experimental metadata patch; verify against real upstream payloads before device release.
public static class PckPatcher
{
    public static int Patch(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var r = new BinaryReader(f, Encoding.UTF8, true);
        if (f.Length < 104 || r.ReadUInt32() != 0x43504447) throw new InvalidDataException("Invalid PCK header.");
        var version = r.ReadUInt32();
        if (version != 3) throw new InvalidDataException($"Unsupported PCK format {version}.");
        r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
        var flags = r.ReadUInt32(); var fileBase = r.ReadUInt64(); var dir = r.ReadUInt64();
        if ((flags & 1) != 0) throw new InvalidDataException("Encrypted PCK directory unsupported.");
        if ((flags & 2) == 0) fileBase = 0;
        if (dir < 104 || dir >= (ulong)f.Length) throw new InvalidDataException("Invalid PCK directory.");
        f.Position = checked((long)dir); var count = r.ReadUInt32();
        if (count is 0 or > 1_000_000) throw new InvalidDataException("Invalid PCK entry count.");
        var changes = new List<(long Offset, long HashOffset, byte[] Data)>();
        for (uint i = 0; i < count; i++)
        {
            var n = r.ReadUInt32(); if (n is 0 or > 65536) throw new InvalidDataException("Invalid PCK path length.");
            var bytes = r.ReadBytes((int)n); if (bytes.Length != n) throw new EndOfStreamException();
            var name = Encoding.UTF8.GetString(bytes).TrimEnd('\0'); if (name.StartsWith("res://")) name = name[6..];
            var offset = checked(fileBase + r.ReadUInt64()); var size = r.ReadUInt64(); var hash = f.Position;
            if (r.ReadBytes(16).Length != 16) throw new EndOfStreamException(); var entryFlags = r.ReadUInt32(); var next = f.Position;
            if (offset > (ulong)f.Length || size > (ulong)f.Length - offset) throw new InvalidDataException("Invalid PCK entry bounds.");
            if (name is not ("project.binary" or "project.godot" or ".godot/extension_list.cfg")) continue;
            if (entryFlags != 0 || size > 64 * 1024 * 1024) throw new InvalidDataException("Unsupported PCK metadata entry.");
            f.Position = (long)offset; var data = r.ReadBytes((int)size); if (data.Length != (int)size) throw new EndOfStreamException();
            bool changed = false;
            if (name == "project.binary") foreach (var s in new[] { "SentryInit", "SentryBootstrap" }) changed |= Replace(data, "autoload/" + s, "disabled/" + s);
            else if (name == "project.godot") foreach (var s in new[] { "SentryInit=\"*res://addons/sentry/SentryInit.gd\"", "SentryBootstrap=\"*res://addons/sentry/SentryBootstrap.cs\"" }) changed |= Replace(data, s, ";" + s[1..]);
            else { const string s = "res://addons/sentry/sentry.gdextension"; changed |= Replace(data, s, new string(' ', s.Length)); }
            if (changed) changes.Add(((long)offset, hash, data)); f.Position = next;
        }
        // Parse all directory entries before mutating any bytes. Only staging copies are passed here.
        foreach (var c in changes) { f.Position = c.Offset; f.Write(c.Data); f.Position = c.HashOffset; f.Write(MD5.HashData(c.Data)); }
        f.Flush(true); return changes.Count;
    }
    static bool Replace(byte[] data, string search, string replacement)
    {
        var a = Encoding.UTF8.GetBytes(search); var b = Encoding.UTF8.GetBytes(replacement);
        if (a.Length != b.Length) throw new InvalidOperationException("Non-length-preserving patch.");
        var index = data.AsSpan().IndexOf(a); if (index < 0) return false;
        b.CopyTo(data, index); return true;
    }
}
