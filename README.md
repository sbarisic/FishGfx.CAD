# FishGfx.CAD

FishGfx.CAD is a Windows x64/.NET 10 parametric CAD application for designing exhaust runners and collector systems. FishGfx and FishUI provide the viewport and desktop interface, while Open CASCADE owns exact STEP/XCAF geometry, B-rep construction, validation, tessellation, and export.

## Repository layout

- `FishGfx.CadKernel` — managed CAD contracts, document worker, persistence, diagnostics, and native ownership.
- `FishGfx.CadKernel.Native` — C++17/Open CASCADE implementation and native tests.
- `FishGfx.Im3d` — managed im3d interaction and FishGfx rendering adapter.
- `FishGfx.Im3d.Native` — pinned C++17 im3d bridge and native tests.
- `FishGfx.ManifoldCad` — desktop application, runner graph, collector model, viewport, and editor.
- `FishGfx.ManifoldCad.Tests` — managed, persistence, relationship, and graphical acceptance tests.
- `external/FishGfx` — pinned renderer repository submodule, including its FishUI submodule.

## Prerequisites

- Windows x64.
- Visual Studio 2022 with Desktop development with C++ and CMake tools.
- .NET 10 SDK.
- Git with submodule support.

Open CASCADE is restored from the manifest-pinned vcpkg baseline into ignored local build directories. It does not need to be installed globally.

## Clone and build

Clone recursively, then run the bootstrap script from PowerShell:

```powershell
git clone --recursive https://github.com/sbarisic/FishGfx.CAD.git
Set-Location FishGfx.CAD
.\tools\bootstrap-manifold-cad.ps1 -Configuration Release
```

For an existing checkout:

```powershell
git submodule update --init --recursive
.\tools\bootstrap-manifold-cad.ps1 -Configuration Release
```

The script bootstraps the ignored `.tools/vcpkg` checkout, builds and runs CTest for both native projects, runs the managed test suite, and builds `FishGfx.CAD.sln` for x64 Release.

## Usage

Run the application after building:

```powershell
dotnet run --project .\FishGfx.ManifoldCad\FishGfx.ManifoldCad.csproj -c Release
```

The application imports exact STEP parts, creates named mates from supported profiles, builds parametric runner graphs, and combines multiple runners into collector systems. Viewport meshes are display data only; saved and exported geometry remains exact Open CASCADE B-rep geometry.

Geometry edits update lightweight previews and mark exact geometry stale. Use **Rebuild Exact** before STEP export. **Save Project** writes a current `.fgcad` archive; **Save Draft** preserves editable graph state with the previous exact result marked stale.

Automated graphical acceptance can be run with:

```powershell
dotnet run --project .\FishGfx.ManifoldCad\FishGfx.ManifoldCad.csproj -c Release -- --auto
```

## Project files and persistence

`.fgcad` files are versioned ZIP archives containing the project manifest, runner and collector graph data, view state, and an XCAF document with exact geometry and placements. Original STEP paths are metadata; reopening a saved project does not depend on the source STEP files.

Generated `.fgcad` archives, native build trees, local vcpkg content, logs, screenshots, and binaries are intentionally ignored.

## Licensing

This repository is licensed under the MIT License. Runtime distributions also include the applicable third-party notices:

- Open CASCADE Technology is dynamically linked under LGPL-2.1 with the OCCT exception. See `FishGfx.CadKernel.Native/THIRD_PARTY_NOTICES.md`.
- im3d is vendored at the pinned revision recorded in `FishGfx.Im3d.Native/thirdparty/im3d/PROVENANCE.md`; its MIT license is retained beside the source.
- FishGfx and FishUI licensing is retained in the `external/FishGfx` submodule.
