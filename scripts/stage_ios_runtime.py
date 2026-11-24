#!/usr/bin/env python3
"""Stage the pinned iOS runtime pack, never desktop game assemblies."""
import hashlib, pathlib, urllib.request, zipfile, shutil, sys
VERSION="9.0.20"
SHA="ccb189791773d4ca77cee3dd03e28dc2a9b6231b479e1e44e28088a378692b3f"
def main():
    dest=pathlib.Path(sys.argv[1]).resolve()
    dest.mkdir(parents=True,exist_ok=True)
    archive=dest.parent / ("mono-ios-"+VERSION+".nupkg")
    if not archive.exists():
        url=f"https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.mono.ios-arm64/{VERSION}/microsoft.netcore.app.runtime.mono.ios-arm64.{VERSION}.nupkg"
        urllib.request.urlretrieve(url,archive)
    with archive.open("rb") as f:
        if hashlib.file_digest(f,"sha256").hexdigest()!=SHA:
            raise SystemExit("Runtime package checksum mismatch")
    with zipfile.ZipFile(archive) as z:
        for name in z.namelist():
            p=pathlib.PurePosixPath(name)
            if p.name in ("",".",".."): continue
            if name.startswith("runtimes/ios-arm64/lib/net9.0/") and p.suffix==".dll":
                target=dest/"Managed"/p.name
            elif name.startswith("runtimes/ios-arm64/native/") and (p.name=="System.Private.CoreLib.dll" or (p.name.startswith("libSystem.") and p.suffix==".dylib")):
                target=dest/("Managed" if p.suffix==".dll" else "Frameworks")/p.name
            else: continue
            target.parent.mkdir(parents=True,exist_ok=True)
            with z.open(name) as src,target.open("wb") as out: shutil.copyfileobj(src,out)
    if not (dest/"Managed/System.Private.CoreLib.dll").is_file(): raise SystemExit("Missing CoreLib")
if __name__=="__main__": main()
