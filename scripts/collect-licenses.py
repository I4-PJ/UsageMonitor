#!/usr/bin/env python3
"""Copy dependency license texts from a restored NuGet graph into a release."""

import argparse
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET


def collect(assets_path: Path, destination: Path, runtime: str) -> None:
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    target = next(value for key, value in assets["targets"].items() if key.endswith("/" + runtime))
    fallback_directory = Path(__file__).resolve().parent.parent / "ThirdPartyLicenses"
    destination.mkdir(parents=True, exist_ok=True)
    downloads = [item for framework in assets["project"]["frameworks"].values() for item in framework.get("downloadDependencies", [])]
    runtime_pack = next(item for item in downloads if item["name"].lower() == "microsoft.netcore.app.runtime." + runtime)
    runtime_version = runtime_pack["version"].strip("[]").split(",", 1)[0].strip()
    runtime_path = next(Path(folder) / runtime_pack["name"].lower() / runtime_version for folder in assets["packageFolders"] if (Path(folder) / runtime_pack["name"].lower() / runtime_version).is_dir())
    shutil.copy2(runtime_path / "LICENSE.TXT", destination.parent / "DOTNET-LICENSE.txt")
    shutil.copy2(runtime_path / "THIRD-PARTY-NOTICES.TXT", destination.parent / "DOTNET-THIRD-PARTY-NOTICES.txt")
    manifest = ["# Packaged dependency licenses", "", f".NET runtime {runtime_version}: see DOTNET-LICENSE.txt and DOTNET-THIRD-PARTY-NOTICES.txt in the publish directory.", ""]
    count = 0

    for identity, entry in sorted(target.items()):
        if entry.get("type") != "package" or not any(entry.get(key) for key in ("runtime", "native", "resources", "runtimeTargets")):
            continue
        package_id, version = identity.split("/", 1)
        library = assets["libraries"][identity]
        package = next(Path(folder) / library["path"] for folder in assets["packageFolders"] if (Path(folder) / library["path"]).is_dir())
        package_destination = destination / f"{package_id}-{version}"
        if not package_destination.resolve().is_relative_to(destination.resolve()):
            raise ValueError(f"Invalid package identity: {identity}")
        package_destination.mkdir(exist_ok=True)
        notices = [name for name in library["files"] if Path(name).name.lower().startswith(("license", "licence", "copying", "notice", "third-party", "thirdparty")) and Path(name).suffix.lower() in ("", ".txt", ".md", ".rst")]
        for name in notices:
            source = package / name
            if not source.resolve().is_relative_to(package.resolve()):
                raise ValueError(f"Invalid notice path for {identity}")
            output = package_destination / name
            output.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, output)

        if not any(Path(name).name.lower().startswith(("license", "licence", "copying")) for name in notices):
            if (package_id == "Avalonia" or package_id.startswith("Avalonia.")) and version == "11.1.0":
                fallback = "Avalonia.LICENSE.txt"
            elif (package_id, version) == ("MicroCom.Runtime", "0.11.0"):
                fallback = "MicroCom.LICENSE.txt"
            elif (package_id, version) == ("Tmds.DBus.Protocol", "0.16.0"):
                fallback = "Tmds.DBus.LICENSE.txt"
            else:
                raise ValueError(f"No license text found for {identity}; add an upstream notice before publishing.")
            shutil.copy2(fallback_directory / fallback, package_destination / fallback)

        if package_id == "Avalonia.Fonts.Inter":
            shutil.copy2(fallback_directory / "Inter.OFL.txt", package_destination / "Inter.OFL.txt")

        nuspec = next(package.glob("*.nuspec"))
        license_element = next((element for element in ET.parse(nuspec).getroot().iter() if element.tag.split("}")[-1] == "license"), None)
        label = license_element.text if license_element is not None else "See bundled notices"
        manifest.append(f"- {package_id} {version}: {label}")
        count += 1

    (destination / "README.md").write_text("\n".join(manifest) + "\n", encoding="utf-8")
    print(f"Included license notices for {count} runtime dependencies.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--runtime", required=True)
    args = parser.parse_args()
    collect(args.assets, args.destination, args.runtime)
