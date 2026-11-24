#!/usr/bin/env python3
"""Export the pinned Godot host and embed the independently built runtime."""
import hashlib, pathlib, plistlib, re, shutil, subprocess, zipfile, sys
ROOT=pathlib.Path(__file__).resolve().parents[1]
WORK=ROOT/".native-build"
def run(*args): subprocess.run([str(a) for a in args],check=True)
def fetch(name,url,sha):
    p=WORK/name
    if not p.exists():run("curl","-fL","--retry","4","-o",p,url)
    with p.open("rb") as f:
        if hashlib.file_digest(f,"sha256").hexdigest()!=sha:raise SystemExit(f"Checksum mismatch: {p}")
    return p
def main():
    WORK.mkdir(exist_ok=True)
    release="https://github.com/godotengine/godot/releases/download/4.5.1-stable/"
    editor=fetch("godot-mono.zip",release+"Godot_v4.5.1-stable_mono_macos.universal.zip","f00ee565e9d3682584117ef8865f5ff6d8f571fbf2733075ee06b9e0953261b8")
    templates=fetch("godot-templates.tpz",release+"Godot_v4.5.1-stable_mono_export_templates.tpz","c425633061bb49f4390bdcece2b9ce68b3b0be3c71095b40644d8f6f17b1146e")
    tools=WORK/"godot";tools.mkdir(exist_ok=True)
    if not (tools/"Godot_mono.app").exists():run("ditto","-x","-k",editor,tools)
    template=tools/"ios.zip"
    if not template.exists():
        with zipfile.ZipFile(templates) as z,z.open("templates/ios.zip") as src,template.open("wb") as dst:shutil.copyfileobj(src,dst)
    project=WORK/"godot-project";out=WORK/"godot-export"
    for p in (project,out):
        if p.exists():shutil.rmtree(p)
        p.mkdir()
    for name in ("project.godot","bootstrap.tscn","icon.svg"):shutil.copy2(ROOT/"native/host"/name,project/name)
    (project/"export_presets.cfg").write_text((ROOT/"native/host/export_presets.cfg.in").read_text().replace("@TEMPLATE@",str(template)))
    godot=tools/"Godot_mono.app/Contents/MacOS/Godot"
    run(godot,"--headless","--path",project,"--editor","--quit-after","60")
    run(godot,"--headless","--path",project,"--export-release","iOS",out/"Sts2Host.ipa")
    xproj=out/"Sts2Host.xcodeproj";pbx=xproj/"project.pbxproj"
    src=out/"Sts2Host";dummy=src/"dummy.cpp";info=src/"Sts2Host-Info.plist"
    if not all(x.exists() for x in (pbx,dummy,info)):raise SystemExit("Unexpected Godot export layout")
    text=pbx.read_text()
    old='lastKnownFileType = sourcecode.cpp.cpp; path = dummy.cpp;'
    if old not in text:raise SystemExit("Godot dummy source layout changed")
    text=text.replace(old,'lastKnownFileType = sourcecode.cpp.objcpp; path = dummy.cpp;')
    text=re.sub(r'(OTHER_LDFLAGS = ")([^"]*)(";)',lambda m:m[1]+m[2]+' -framework Security -framework AVFoundation -Wl,-export_dynamic'+m[3],text)
    pbx.write_text(text)
    shutil.copy2(ROOT/"native/HostGate.mm",src/"HostGate.mm")
    with dummy.open("a") as f:f.write('\n#include "HostGate.mm"\n')
    metadata=plistlib.loads(info.read_bytes())
    metadata.update({"godot_cmdline":["--main-pack","user://StS2.pck"],"CFBundleDisplayName":"STS2 Launcher","UIFileSharingEnabled":True,"LSSupportsOpeningDocumentsInPlace":True,"NSLocalNetworkUsageDescription":"Find local multiplayer games."})
    info.write_bytes(plistlib.dumps(metadata))
    if "--extensions" in sys.argv:
        run(sys.executable,ROOT/"scripts/integrate_extensions.py",xproj,WORK/"extensions")
        shutil.copy2(ROOT/"native/ExtensionRegistry.mm",src/"ExtensionRegistry.mm")
        with dummy.open("a") as f:f.write('\n#define STS2_NATIVE_EXTENSIONS 1\n#include "ExtensionRegistry.mm"\n')
    else:
        with dummy.open("a") as f:f.write('\nvoid sts2_register_extensions() {}\nextern "C" __attribute__((visibility("default"),used)) int sts2_extensions_available() { return 0; }\n')
    run("xcodebuild","-project",xproj,"-scheme","Sts2Host","-configuration","Release","-destination","generic/platform=iOS","-derivedDataPath",WORK/"godot-derived","CODE_SIGNING_ALLOWED=NO","CODE_SIGNING_REQUIRED=NO","DEVELOPMENT_TEAM=","build")
    app=WORK/"godot-derived/Build/Products/Release-iphoneos/Sts2Host.app"
    if not app.is_dir():raise SystemExit("Godot application missing")
    distribution=WORK/"runtime-distribution"
    if (app/"Managed").exists():shutil.rmtree(app/"Managed")
    shutil.copytree(distribution/"Managed",app/"Managed",dirs_exist_ok=True)
    frameworks=app/"Frameworks";frameworks.mkdir(exist_ok=True)
    shutil.copytree(distribution/"Frameworks",frameworks,dirs_exist_ok=True)
    fw=frameworks/"sts2.framework";fw.mkdir(exist_ok=True)
    shutil.copy2(WORK/"framework/sts2",fw/"sts2")
    (fw/"Info.plist").write_bytes(plistlib.dumps({"CFBundleExecutable":"sts2","CFBundleIdentifier":"org.sts2mobile.ioslauncher.runtime","CFBundlePackageType":"FMWK","CFBundleVersion":"1","CFBundleShortVersionString":"1.0","MinimumOSVersion":"16.0"}))
    run(sys.executable,ROOT/"scripts/validate_native_app.py",app,"--extensions" if "--extensions" in sys.argv else "--diagnostic")
    if "--package" in sys.argv:
        run(sys.executable,ROOT/"scripts/package_native_ipa.py",app)
    else:
        print("Pre-IPA build complete: "+str(app),flush=True)
if __name__=="__main__":main()
