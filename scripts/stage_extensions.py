#!/usr/bin/env python3
"""Fetch pinned public iOS extension inputs. No Steam game content."""
import pathlib,subprocess,hashlib,zipfile,shutil,sys
ROOT=pathlib.Path(__file__).resolve().parents[1];WORK=ROOT/".native-build"
def run(*args,cwd=None):subprocess.run([str(x) for x in args],cwd=cwd,check=True)
def checkout(path,url,rev,sparse=()):
    if not path.exists():
        path.mkdir(parents=True);run("git","init",path);run("git","remote","add","origin",url,cwd=path)
        if sparse:run("git","sparse-checkout","set",*sparse,cwd=path)
        run("git","fetch","--depth","1","origin",rev,cwd=path);run("git","checkout","--detach","FETCH_HEAD",cwd=path)
    if subprocess.check_output(["git","rev-parse","HEAD"],cwd=path,text=True).strip()!=rev:raise SystemExit("Unexpected extension revision")
def main():
    out=pathlib.Path(sys.argv[1]);WORK.mkdir(exist_ok=True)
    archive=WORK/"fmod-addons.zip"
    if not archive.exists():run("curl","-fL","--retry","4","-o",archive,"https://github.com/utopia-rise/fmod-gdextension/releases/download/6.1.0-4.5.0/addons.zip")
    with archive.open("rb") as f:
        if hashlib.file_digest(f,"sha256").hexdigest()!="8796f6cd1cb38bf7ff4da2648f8af2695244ffdb80a7ac4ab8a171ca44895713":raise SystemExit("FMOD checksum mismatch")
    with zipfile.ZipFile(archive) as z:
        for name in z.namelist():
            if not name.startswith("fmod/libs/iOS/") or name.endswith("/"):continue
            relative=pathlib.PurePosixPath(name).relative_to("fmod/libs/iOS")
            if ".." in relative.parts:raise SystemExit("Invalid archive path")
            dest=out/"fmod"/relative;dest.parent.mkdir(parents=True,exist_ok=True)
            with z.open(name) as src,dest.open("wb") as dst:shutil.copyfileobj(src,dst)
    spine=WORK/"spine-runtimes"
    checkout(spine,"https://github.com/EsotericSoftware/spine-runtimes.git","e7dc1435fa4a0083ab431f1b28e083c14a1f5c68",("spine-cpp","spine-godot"))
    checkout(spine/"spine-godot/godot-cpp","https://github.com/godotengine/godot-cpp.git","e83fd0904c13356ed1d4c3d09f8bb9132bdc6b77")
    shutil.copytree(spine/"spine-cpp/spine-cpp",spine/"spine-godot/spine_godot/spine-cpp",dirs_exist_ok=True)
    run(sys.executable,"-m","venv",WORK/"extension-venv")
    run(WORK/"extension-venv/bin/pip","install","scons==4.11.1")
    run(WORK/"extension-venv/bin/scons","platform=ios","arch=arm64","target=template_release","ios_min_version=16.0","-j8",cwd=spine/"spine-godot")
    shutil.copytree(spine/"spine-godot/bin/ios/libspine_godot.ios.template_release.framework",out/"spine/libspine_godot.ios.template_release.framework",dirs_exist_ok=True)
if __name__=="__main__":main()
