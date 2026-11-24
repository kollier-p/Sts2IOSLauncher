#!/usr/bin/env python3
import pathlib,plistlib,subprocess,sys,zipfile
app=pathlib.Path(sys.argv[1]).resolve()
info=plistlib.loads((app/"Info.plist").read_bytes())
if info.get("STS2ExtensionFixtures"):
    raise SystemExit("The extension test fixture cannot be packaged.")
subprocess.run(["xcrun","lipo","-verify_arch","arm64",app/info["CFBundleExecutable"]],check=True)
subprocess.run(["xcrun","lipo","-verify_arch","arm64",app/"Frameworks/sts2.framework/sts2"],check=True)
for item in app.rglob("*"):
    if item.name.lower() in {"sts2.dll","0harmony.dll","slaythespire2.pck","release_info.json","embedded.mobileprovision"}:raise SystemExit(f"Unexpected game or signing asset: {item}")
    if item.is_symlink():raise SystemExit(f"Unexpected symlink: {item}")
out=pathlib.Path("dist/STS2IOSLauncher-native-experimental.ipa")
out.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(out,"w",compression=zipfile.ZIP_DEFLATED,compresslevel=3) as z:
    for p in app.rglob("*"):
        if p.is_file() and "_CodeSignature" not in p.parts:z.write(p,"Payload/"+app.name+"/"+p.relative_to(app).as_posix())
with zipfile.ZipFile(out) as z:
    if z.testzip():raise SystemExit("Invalid IPA archive")
print(out)
