"""Build, test, package and release an SFM mod repository (kept in sync from sfm-modkit/templates/ModRepo).

    python build/ci.py gamerefs   private reference assemblies -> .gamerefs/gamedir (needs SFM_GAMEREFS_TOKEN)
    python build/ci.py deps       RestraintHub's latest release -> lib/RestraintHub (only if the mod uses the hub)
    python build/ci.py test       run every tests/*/ project (plain console runners, exit code 1 on a failure)
    python build/ci.py build      dotnet build src/<Mod> in Release (against .gamerefs when present)
    python build/ci.py package    dist/<Mod>-<version>.zip: BepInEx/plugins/<Mod>/..., unpacked in the game folder
    python build/ci.py release    GitHub release: v<version> the first time, else pre-release v<version>-build.<run>

Runs locally too (gamerefs / deps / release need the gh CLI). Standard library only.
"""
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OWNER = "FlanderDev-NSFW"
GAMEREFS_REPO = OWNER + "/SFM-GameRefs"
GAMEREFS_TAG = "v1.1.3-be.738"
HUB_REPO = OWNER + "/SFM-RestraintHub"


def mod_name():
    mods = [d for d in os.listdir(os.path.join(ROOT, "src")) if os.path.isfile(os.path.join(ROOT, "src", d, d + ".csproj"))]
    if len(mods) != 1:
        sys.exit("expected exactly one src/<Mod>/<Mod>.csproj, found: %s" % mods)
    return mods[0]


def csproj(mod):
    with open(os.path.join(ROOT, "src", mod, mod + ".csproj"), encoding="utf-8-sig") as f:
        return f.read()


def version(mod):
    m = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", csproj(mod))
    if not m:
        sys.exit("no <Version> in src/%s/%s.csproj" % (mod, mod))
    return m.group(1)


def uses_hub(mod):
    return re.search(r"<SfmUsesRestraintHub>\s*true\s*</SfmUsesRestraintHub>", csproj(mod), re.I) is not None


def embedded_names(mod):
    return {os.path.basename(p.replace("\\", "/")) for p in re.findall(r'<EmbeddedResource\s+Include\s*=\s*"([^"]+)"', csproj(mod), re.I)}


def run(args, **kw):
    print("+ " + " ".join(args), flush=True)
    subprocess.run(args, check=True, cwd=kw.pop("cwd", ROOT), **kw)


def out(args):
    return subprocess.run(args, check=True, cwd=ROOT, capture_output=True, text=True).stdout


def extract(zip_path, prefix, dest):
    """Extract the members of zip_path under prefix/ into dest (prefix stripped)."""
    with zipfile.ZipFile(zip_path) as z:
        for info in z.infolist():
            name = info.filename.replace("\\", "/")
            if info.is_dir() or not name.startswith(prefix + "/"):
                continue
            target = os.path.join(dest, *name[len(prefix) + 1:].split("/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with z.open(info) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)


def cmd_gamerefs():
    """The private reference-only assemblies (no game code) the mod compiles against in CI."""
    dest = os.path.join(ROOT, ".gamerefs")
    shutil.rmtree(dest, ignore_errors=True)
    env = dict(os.environ)
    token = env.get("SFM_GAMEREFS_TOKEN")
    if token:
        env["GH_TOKEN"] = token
    elif os.environ.get("GITHUB_ACTIONS"):
        sys.exit("SFM_GAMEREFS_TOKEN is not set: add the repository secret (read access to %s)." % GAMEREFS_REPO)
    run(["gh", "release", "download", GAMEREFS_TAG, "--repo", GAMEREFS_REPO, "--pattern", "*.nupkg", "--dir", dest], env=env)
    nupkg = glob.glob(os.path.join(dest, "*.nupkg"))[0]
    extract(nupkg, "gamedir", os.path.join(dest, "gamedir"))
    os.remove(nupkg)
    print("game references in " + os.path.join(dest, "gamedir"))


def cmd_deps():
    mod = mod_name()
    if not uses_hub(mod):
        print(mod + " does not use RestraintHub")
        return
    tmp = os.path.join(ROOT, "lib", "RestraintHub", "download")
    shutil.rmtree(os.path.join(ROOT, "lib", "RestraintHub"), ignore_errors=True)
    # No tag: the latest (non-pre-)release, the version players install from F3M.
    run(["gh", "release", "download", "--repo", HUB_REPO, "--pattern", "RestraintHub-*.zip", "--dir", tmp])
    extract(glob.glob(os.path.join(tmp, "*.zip"))[0], "BepInEx/plugins/RestraintHub", os.path.join(ROOT, "lib", "RestraintHub"))
    shutil.rmtree(tmp)
    print("RestraintHub: " + ", ".join(sorted(os.listdir(os.path.join(ROOT, "lib", "RestraintHub")))))


def cmd_test():
    projects = sorted(glob.glob(os.path.join(ROOT, "tests", "*", "*.csproj")))
    if not projects:
        print("no tests")
    for p in projects:
        run(["dotnet", "run", "--project", p, "-c", "Release"])


def game_dir_args():
    refs = os.path.join(ROOT, ".gamerefs", "gamedir")
    return ["-p:GameDir=" + refs] if os.path.isdir(refs) else []


def cmd_build():
    mod = mod_name()
    run(["dotnet", "build", os.path.join("src", mod, mod + ".csproj"), "-c", "Release", "--nologo"] + game_dir_args())


def zip_name(mod, ver):
    tag = release_tag(ver) if os.environ.get("GITHUB_ACTIONS") else "v" + ver
    return "%s-%s.zip" % (mod, tag[1:])


def cmd_package():
    mod = mod_name()
    ver = version(mod)
    bin_dir = os.path.join(ROOT, "src", mod, "bin", "Release", "net6.0")
    files = {mod + ".dll": os.path.join(bin_dir, mod + ".dll")}
    if os.path.isfile(os.path.join(bin_dir, mod + ".xml")):
        files[mod + ".xml"] = os.path.join(bin_dir, mod + ".xml")  # API docs for modders (RestraintHub)
    assets = os.path.join(ROOT, "src", mod, "assets")
    embedded = embedded_names(mod)
    for path in glob.glob(os.path.join(assets, "**", "*"), recursive=True):
        name = os.path.basename(path)
        if os.path.isfile(path) and name != ".gitkeep" and name not in embedded:
            files[os.path.relpath(path, assets).replace("\\", "/")] = path
    files["README.md"] = os.path.join(ROOT, "README.md")
    files["LICENSE.txt"] = os.path.join(ROOT, "LICENSE")
    if os.path.isfile(os.path.join(ROOT, "LICENSE-MODELS")):
        files["LICENSE-MODELS.txt"] = os.path.join(ROOT, "LICENSE-MODELS")
    bad = [n for n in files if re.match(r"^(GameAssembly|UnityPlayer|Assembly-CSharp|Il2Cpp|BepInEx|0Harmony|MonoMod)", os.path.basename(n))]
    if bad:
        sys.exit("refusing to package game / loader binaries: %s" % bad)
    dist = os.path.join(ROOT, "dist")
    os.makedirs(dist, exist_ok=True)
    zip_path = os.path.join(dist, zip_name(mod, ver))
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for rel, src in sorted(files.items()):
            if not os.path.isfile(src):
                sys.exit("missing for the package: " + src)
            z.write(src, "BepInEx/plugins/%s/%s" % (mod, rel))
    print(zip_path)
    with zipfile.ZipFile(zip_path) as z:
        for info in z.infolist():
            print("  %9d %s" % (info.file_size, info.filename))


def releases():
    return json.loads(out(["gh", "release", "list", "--limit", "200", "--json", "tagName,isPrerelease"]))


def release_tag(ver):
    """v<version> if that release does not exist yet, else the pre-release v<version>-build.<run number>."""
    tags = {r["tagName"] for r in releases()}
    if "v" + ver not in tags:
        return "v" + ver
    return "v%s-build.%s" % (ver, os.environ.get("GITHUB_RUN_NUMBER", "0"))


def cmd_release():
    mod = mod_name()
    ver = version(mod)
    tag = release_tag(ver)
    pre = tag != "v" + ver
    zips = glob.glob(os.path.join(ROOT, "dist", "*.zip"))
    if len(zips) != 1:
        sys.exit("expected one zip in dist/, found %s (run package first)" % zips)
    sha = os.environ.get("GITHUB_SHA") or out(["git", "rev-parse", "HEAD"]).strip()
    subject = out(["git", "log", "-1", "--format=%s", sha]).strip()
    needs = "RestraintHub 1.0.0 or newer, " if uses_hub(mod) else ""
    notes = (("Development build of %s (not the released version)." % ver if pre else "%s %s." % (mod, ver))
             + "\n\nCommit %s: %s\n\nInstall: unpack the zip in the game folder (the one with SecretFlasherManaka.exe). "
               "Needs BepInEx 6 IL2CPP, %sSecret Flasher Manaka v1.1.3. Single-player / offline only." % (sha[:7], subject, needs))
    args = ["gh", "release", "create", tag, zips[0], "--target", sha, "--title", "%s %s" % (mod, tag[1:]), "--notes", notes]
    run(args + (["--prerelease"] if pre else ["--latest"]))
    # Only the newest development build is kept.
    for r in releases():
        if r["isPrerelease"] and r["tagName"] != tag:
            run(["gh", "release", "delete", r["tagName"], "--yes", "--cleanup-tag"])


if __name__ == "__main__":
    commands = {"gamerefs": cmd_gamerefs, "deps": cmd_deps, "test": cmd_test, "build": cmd_build, "package": cmd_package, "release": cmd_release}
    if len(sys.argv) != 2 or sys.argv[1] not in commands:
        sys.exit(__doc__)
    commands[sys.argv[1]]()
