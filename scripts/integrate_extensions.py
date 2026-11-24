#!/usr/bin/env python3
"""Link device extension archives into exported Godot Xcode target.
Requires explicit license acknowledgement before distributing an IPA.
"""
import pathlib,plistlib,re,shutil,subprocess,sys
def integrate(project,extensions):
    project=pathlib.Path(project);extensions=pathlib.Path(extensions)
    target=project.parent/"NativeExtensions";target.mkdir(exist_ok=True)
    archives=[]
    for name in ("libfmod_iphoneos.a","libfmodstudio_iphoneos.a"):
        matches=list(extensions.rglob(name))
        if len(matches)!=1:raise SystemExit("Missing or ambiguous "+name)
        dest=target/name;shutil.copy2(matches[0],dest);archives.append(dest)
    for name in ("libGodotFmod","libspine_godot"):
        candidates=[p for p in extensions.rglob("*") if p.is_file() and p.name.startswith(name) and (p.suffix==".a" or p.parent.suffix==".framework")]
        device=[]
        for p in candidates:
            if "simulator" in str(p).lower():continue
            if subprocess.run(["xcrun","lipo","-verify_arch","arm64",p],capture_output=True).returncode==0:device.append(p)
        if len(device)!=1:raise SystemExit("Missing or ambiguous device binary "+name)
        p=device[0]
        if subprocess.run(["file",p],capture_output=True,text=True).stdout.find("archive")<0:raise SystemExit("Expected static extension archive: "+str(p))
        dest=target/(name+".a");shutil.copy2(p,dest);archives.append(dest)
    pbx=project/"project.pbxproj";text=pbx.read_text()
    flags=" ".join("-Wl,-force_load,$(SRCROOT)/NativeExtensions/"+p.name for p in archives)
    text,n=re.subn(r'(OTHER_LDFLAGS = ")([^"]*)(";)',lambda m:m[1]+m[2]+" "+flags+" -framework AudioToolbox -framework AVFoundation -framework CoreAudio -framework CoreMedia"+m[3],text)
    if not n:raise SystemExit("Unrecognized Xcode linker settings")
    pbx.write_text(text)
if __name__=="__main__":integrate(sys.argv[1],sys.argv[2])
