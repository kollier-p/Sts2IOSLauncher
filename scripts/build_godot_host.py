#!/usr/bin/env python3
"""Export the pinned Godot host and embed the independently built runtime."""
import argparse, hashlib, pathlib, plistlib, re, shutil, subprocess, zipfile, sys
from integrate_extensions import embed_extensions, link_host_callback
from build_extension_fixtures import build_extension_fixtures
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
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--extensions", action="store_true")
    mode.add_argument("--extension-fixtures", action="store_true")
    args = parser.parse_args()
    WORK.mkdir(exist_ok=True)
    variant = WORK / "extension-fixture-app" if args.extension_fixtures else WORK / "licensed-app" if args.extensions else WORK
    variant.mkdir(exist_ok=True)
    release="https://github.com/godotengine/godot/releases/download/4.5.1-stable/"
    editor=fetch("godot-mono.zip",release+"Godot_v4.5.1-stable_mono_macos.universal.zip","f00ee565e9d3682584117ef8865f5ff6d8f571fbf2733075ee06b9e0953261b8")
    templates=fetch("godot-templates.tpz",release+"Godot_v4.5.1-stable_mono_export_templates.tpz","c425633061bb49f4390bdcece2b9ce68b3b0be3c71095b40644d8f6f17b1146e")
    tools=WORK/"godot";tools.mkdir(exist_ok=True)
    if not (tools/"Godot_mono.app").exists():run("ditto","-x","-k",editor,tools)
    template=tools/"ios.zip"
    if not template.exists():
        with zipfile.ZipFile(templates) as z,z.open("templates/ios.zip") as src,template.open("wb") as dst:shutil.copyfileobj(src,dst)
    project=variant/"godot-project";out=variant/"godot-export"
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
    text=re.sub(r'(OTHER_LDFLAGS = ")([^"]*)(";)',lambda m:m[1]+m[2]+' -framework Security -framework AVFoundation -framework UniformTypeIdentifiers -framework QuickLook -framework ImageIO -Wl,-export_dynamic'+m[3],text)
    pbx.write_text(text)
    shutil.copy2(ROOT/"native/HostGate.mm",src/"HostGate.mm")
    shutil.copy2(ROOT/"native/LauncherFiles.mm",src/"LauncherFiles.mm")
    shutil.copy2(ROOT/"native/LauncherWorkshop.mm",src/"LauncherWorkshop.mm")
    with dummy.open("a") as f:f.write('\n#include "HostGate.mm"\n')
    metadata=plistlib.loads(info.read_bytes())
    revision=subprocess.run(["git","-C",ROOT,"rev-parse","HEAD"],capture_output=True,text=True,check=False).stdout.strip()
    metadata.update({"godot_cmdline":["--main-pack","user://StS2.pck","--log-file","user://LauncherLogs/godot.log"],"CFBundleDisplayName":"STS2 Launcher","UIFileSharingEnabled":True,"LSSupportsOpeningDocumentsInPlace":True,"NSLocalNetworkUsageDescription":"Find local multiplayer games.","STS2GitRevision":revision})
    if args.extension_fixtures:
        metadata.update({"CFBundleDisplayName": "STS2 CI Fixture", "STS2ExtensionFixtures": True})
    info.write_bytes(plistlib.dumps(metadata))
    if args.extensions or args.extension_fixtures:
        link_host_callback(xproj)
        shutil.copy2(ROOT/"native/ExtensionSupport.mm",src/"ExtensionSupport.mm")
        with dummy.open("a") as f:
            if args.extension_fixtures:
                f.write('\n#define STS2_EXTENSION_FIXTURES 1\n')
            f.write('\n#include "ExtensionSupport.mm"\n')
    else:
        with dummy.open("a") as f:f.write('\nextern "C" __attribute__((visibility("default"),used)) int sts2_extensions_available() { return 0; }\n')
    derived=variant/"godot-derived"
    app=derived/"Build/Products/Release-iphoneos/Sts2Host.app"
    if app.exists(): shutil.rmtree(app)
    run("xcodebuild","-project",xproj,"-scheme","Sts2Host","-configuration","Release","-destination","generic/platform=iOS","-derivedDataPath",derived,"CODE_SIGNING_ALLOWED=NO","CODE_SIGNING_REQUIRED=NO","DEVELOPMENT_TEAM=","build")
    if not app.is_dir():raise SystemExit("Godot application missing")
    distribution=WORK/"runtime-distribution"
    if (app/"Managed").exists():shutil.rmtree(app/"Managed")
    shutil.copytree(distribution/"Managed",app/"Managed",dirs_exist_ok=True)
    frameworks=app/"Frameworks";frameworks.mkdir(exist_ok=True)
    shutil.copytree(distribution/"Frameworks",frameworks,dirs_exist_ok=True)
    if args.extensions:
        embed_extensions(frameworks, WORK/"extensions")
    elif args.extension_fixtures:
        fixtures = variant / "inputs"
        build_extension_fixtures(fixtures)
        embed_extensions(frameworks, fixtures, fixtures=True)
    fw=frameworks/"sts2.framework";fw.mkdir(exist_ok=True)
    shutil.copy2(WORK/"framework/sts2",fw/"sts2")
    (fw/"Info.plist").write_bytes(plistlib.dumps({"CFBundleExecutable":"sts2","CFBundleIdentifier":"org.sts2mobile.ioslauncher.runtime","CFBundlePackageType":"FMWK","CFBundleVersion":"1","CFBundleShortVersionString":"1.0","MinimumOSVersion":"16.0"}))
    mode = "--extension-fixtures" if args.extension_fixtures else "--extensions" if args.extensions else "--diagnostic"
    run(sys.executable,ROOT/"scripts/validate_native_app.py",app,mode,"--report",variant/"pre-ipa-report.json")
    if any(out.glob("*.ipa")):
        raise SystemExit("Godot's project-only export unexpectedly created an IPA")
    print("Pre-IPA build complete: "+str(app),flush=True)
if __name__=="__main__":main()
