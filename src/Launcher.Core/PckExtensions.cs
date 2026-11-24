using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace Launcher.Core;

// Use the native extensions supplied by the launcher in the copy sent to Godot.
public static class PckExtensions
{
    static readonly Dictionary<string,string> Libraries=new(StringComparer.Ordinal){
        ["fmod_library_init"]="res://native/Sts2Fmod.framework",
        ["spine_godot_library_init"]="res://native/Sts2Spine.framework"
    };
    static readonly Regex EntrySymbol = new(@"(?m)^[ \t]*entry_symbol[ \t]*=[ \t]*""([A-Za-z0-9_]+)""[ \t]*\r?$");
    public static string RewriteDescriptor(string text,string symbol)
    {
        if(!Libraries.TryGetValue(symbol,out var library)) throw new InvalidDataException("Unsupported extension entry symbol: " + symbol);
        var matches=EntrySymbol.Matches(text);
        if(matches.Count!=1 || matches[0].Groups[1].Value!=symbol)
            throw new InvalidDataException("Unexpected extension entry symbol: "+symbol);
        var sections=Regex.Split(text,@"(?m)(^\s*\[[^\r\n]+\]\s*$)");
        bool libraries=false;
        for(int i=1;i<sections.Length-1;i+=2)
        {
            var name=sections[i].Trim();
            if(name=="[libraries]"){
                if(libraries)throw new InvalidDataException("Duplicate libraries section");
                libraries=true;
                // Only iOS needs rewriting. No downloaded native binary is loaded.
                sections[i+1]=Regex.Replace(sections[i+1],@"(?mi)^\s*ios(?:\.[a-z0-9_]+)*\s*=.*(?:\r?\n|$)","");
                sections[i+1]="\nios.arm64 = \""+library+"\"\n"+sections[i+1];
            }
            if(name=="[dependencies]"){
                // All iOS dependencies are linked at build time, not extracted from game files.
                sections[i+1]=Regex.Replace(sections[i+1],@"(?mi)^\s*ios(?:\.[a-z0-9_]+)*\s*=\s*\{[^}]*\}\s*","");
            }
        }
        if(!libraries)throw new InvalidDataException("Extension libraries section missing");
        return string.Concat(sections);
    }
    public static int Patch(string path,CancellationToken ct=default)
    {
        using var f=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        using var r=new BinaryReader(f,Encoding.UTF8,true);
        using var w=new BinaryWriter(f,Encoding.UTF8,true);
        if(r.ReadUInt32()!=0x43504447||r.ReadUInt32()!=3)throw new InvalidDataException("Unsupported PCK");
        f.Position=20;var flags=r.ReadUInt32();var baseOffset=r.ReadUInt64();var dir=r.ReadUInt64();
        if((flags&1)!=0||dir<104||dir>=(ulong)f.Length)throw new InvalidDataException("Invalid PCK directory");
        if((flags&2)==0)baseOffset=0;
        f.Position=(long)dir;var count=r.ReadUInt32();
        if(count==0||count>1_000_000)throw new InvalidDataException("Invalid entry count");
        using var table=new MemoryStream();using var tw=new BinaryWriter(table,Encoding.UTF8,true);
        var changes=new List<(long Metadata,byte[] Data)>();
        var found=new HashSet<string>();
        for(uint i=0;i<count;i++){
            ct.ThrowIfCancellationRequested();
            var length=r.ReadUInt32();if(length==0||length>65536)throw new InvalidDataException("Invalid path length");
            var name=r.ReadBytes((int)length);var metadata=r.ReadBytes(36);
            if(name.Length!=length||metadata.Length!=36)throw new EndOfStreamException();
            tw.Write(length);tw.Write(name);var position=table.Position;tw.Write(metadata);
            if(table.Length>128*1024*1024)throw new InvalidDataException("Directory too large");
            var normalized=Encoding.UTF8.GetString(name).TrimEnd('\0');
            if(normalized.StartsWith("res://"))normalized=normalized[6..];
            if(!normalized.EndsWith(".gdextension",StringComparison.Ordinal))continue;
            var next=f.Position;
            using var m=new BinaryReader(new MemoryStream(metadata));
            var offset=checked(baseOffset+m.ReadUInt64());var size=m.ReadUInt64();m.ReadBytes(16);var ef=m.ReadUInt32();
            if(ef!=0||size>1024*1024||offset>=dir||size>dir-offset)continue;
            f.Position=(long)offset;var data=r.ReadBytes((int)size);
            if(data.Length!=(int)size)throw new EndOfStreamException();
            f.Position=next;
            var descriptor=Encoding.UTF8.GetString(data);
            var symbol=EntrySymbol.Match(descriptor).Groups[1].Value;
            if(!Libraries.ContainsKey(symbol))continue;
            if(!found.Add(symbol))throw new InvalidDataException("Duplicate extension descriptor: "+symbol);
            changes.Add((position,Encoding.UTF8.GetBytes(RewriteDescriptor(descriptor,symbol))));
        }
        if(f.Position!=f.Length||found.Count!=Libraries.Count)throw new InvalidDataException("Required extension descriptors missing or invalid");
        // Append the updated resource bodies and rebuild the directory; never
        // overwrite original descriptor data and never change resource names.
        f.Position=(long)dir;
        foreach(var change in changes){
            ct.ThrowIfCancellationRequested();
            while(f.Position%8!=0)w.Write((byte)0);
            var offset=checked((ulong)f.Position-baseOffset);w.Write(change.Data);
            table.Position=change.Metadata;tw.Write(offset);tw.Write((ulong)change.Data.Length);tw.Write(MD5.HashData(change.Data));tw.Write(0U);
        }
        while(f.Position%8!=0)w.Write((byte)0);
        var newDir=(ulong)f.Position;w.Write(count);
        table.Position=0;table.CopyTo(f);f.SetLength(f.Position);
        f.Position=32;w.Write(newDir);f.Flush(true);
        return changes.Count;
    }
}
