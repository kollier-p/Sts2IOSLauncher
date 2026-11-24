using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
namespace Launcher.Core;

public static class PckScripts
{
    // Only mutate a disposable launch copy, never the verified installed pack.
    public static int Add(string pack,string assembly,CancellationToken ct=default)
    {
        var paths=Regex.Matches(Encoding.ASCII.GetString(File.ReadAllBytes(assembly)),@"res://[A-Za-z0-9_/.]+\.cs")
            .Select(m=>m.Value).Where(p=>!p.Contains("..")).Distinct(StringComparer.Ordinal).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
        if(paths.Length==0)throw new InvalidDataException("No managed script paths found.");
        return AddPaths(pack,paths,ct);
    }
    public static int AddPaths(string pack,IEnumerable<string> paths,CancellationToken ct=default)
    {
        using var f=new FileStream(pack,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        using var r=new BinaryReader(f,Encoding.UTF8,true);
        using var w=new BinaryWriter(f,Encoding.UTF8,true);
        if(r.ReadUInt32()!=0x43504447||r.ReadUInt32()!=3)throw new InvalidDataException("Unsupported pack");
        f.Position=20;var flags=r.ReadUInt32();var fileBase=r.ReadUInt64();var dir=r.ReadUInt64();
        if((flags&1)!=0||dir<104||dir>=(ulong)f.Length)throw new InvalidDataException("Unsupported directory");
        f.Position=(long)dir;var count=r.ReadUInt32();
        if(count==0||count>1_000_000)throw new InvalidDataException("Invalid count");
        var existing=new HashSet<string>(StringComparer.Ordinal);
        using var old=new MemoryStream();
        for(uint i=0;i<count;i++){
            ct.ThrowIfCancellationRequested();
            var n=r.ReadUInt32();if(n==0||n>65536)throw new InvalidDataException("Invalid path");
            var p=r.ReadBytes((int)n);var metadata=r.ReadBytes(36);
            if(p.Length!=n||metadata.Length!=36)throw new EndOfStreamException();
            existing.Add(Encoding.UTF8.GetString(p).TrimEnd('\0').Replace("res://",""));
            using var ow=new BinaryWriter(old,Encoding.UTF8,true);ow.Write(n);ow.Write(p);ow.Write(metadata);
            if(old.Length>128*1024*1024)throw new InvalidDataException("Directory too large");
        }
        if(f.Position!=f.Length)throw new InvalidDataException("Directory must be final section");
        var wanted=paths.Distinct(StringComparer.Ordinal).Where(p=>p.StartsWith("res://")&&p.EndsWith(".cs")&&!p.Contains("..")&&!existing.Contains(p[6..])).ToArray();
        if(wanted.Length==0)return 0;
        if(wanted.Length>100000)throw new InvalidDataException("Too many scripts");
        f.Position=(long)dir;
        using var entries=new MemoryStream();using var ew=new BinaryWriter(entries,Encoding.UTF8,true);
        byte[] newline=[10];var md5=MD5.HashData(newline);
        foreach(var p in wanted){
            ct.ThrowIfCancellationRequested();
            while(f.Position%8!=0)w.Write((byte)0);
            var offset=(ulong)f.Position;w.Write(newline);
            var bytes=Encoding.UTF8.GetBytes(p);var padded=(bytes.Length+3)&~3;
            ew.Write((uint)padded);ew.Write(bytes);ew.Write(new byte[padded-bytes.Length]);
            ew.Write((flags&2)!=0?checked(offset-fileBase):offset);ew.Write(1UL);ew.Write(md5);ew.Write(0U);
        }
        while(f.Position%8!=0)w.Write((byte)0);
        var newDir=(ulong)f.Position;w.Write(checked(count+(uint)wanted.Length));
        old.Position=0;old.CopyTo(f);entries.Position=0;entries.CopyTo(f);
        f.SetLength(f.Position);f.Position=32;w.Write(newDir);f.Flush(true);
        return wanted.Length;
    }
}
