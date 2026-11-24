#!/usr/bin/env python3
"""Validate the staged app without signing it or creating an IPA."""
import pathlib,plistlib,subprocess,sys,json,hashlib
app=pathlib.Path(sys.argv[1]).resolve()
full="--extensions" in sys.argv
info=plistlib.loads((app/"Info.plist").read_bytes())
binary=app/info["CFBundleExecutable"]
framework=app/"Frameworks/sts2.framework/sts2"
required=[binary,framework,app/"Managed/Launcher.Host.dll",app/"Managed/Launcher.Core.dll",app/"Managed/Launcher.Steam.dll",app/"Managed/SteamKit2.dll",app/"Managed/System.Private.CoreLib.dll",app/"Managed/STS2JitSupport.dll",app/"Managed/Launcher.Adapters.dll"]
for p in required:
    if not p.is_file() or p.stat().st_size==0:raise SystemExit("Missing app component: "+str(p))
for p in (binary,framework):
    subprocess.run(["xcrun","lipo","-verify_arch","arm64",p],check=True)
def symbols(p):
    return subprocess.check_output(["xcrun","nm","-gU",p],text=True,errors="replace")
main=symbols(binary);runtime=symbols(framework)
for name in ("_sts2_extensions_available",):
    if name not in main:raise SystemExit("Missing host symbol: "+name)
for name in ("_sts2_host_delegate","_godotsharp_game_main_init","_sts2_initialize_game_runtime","_sts2_jit_patch","_sts2_vault_read","_sts2_vault_write","_sts2_vault_clear"):
    if name not in runtime:raise SystemExit("Missing runtime symbol: "+name)
if full:
    for name in ("_fmod_library_init","_spine_godot_library_init"):
        if name not in main:raise SystemExit("Missing native extension: "+name)
for item in app.rglob("*"):
    if item.name.lower() in {"sts2.dll","0harmony.dll","slaythespire2.pck","release_info.json","embedded.mobileprovision"}:raise SystemExit("Unexpected proprietary game or signing asset: "+str(item))
    if item.is_symlink():raise SystemExit("Unexpected symlink: "+str(item))
report={"bundle":info.get("CFBundleIdentifier"),"minimum_os":info.get("MinimumOSVersion"),"extensions":full,"required_components":len(required),"status":"compiled and staged; device untested"}
pathlib.Path(".native-build/pre-ipa-report.json").write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
