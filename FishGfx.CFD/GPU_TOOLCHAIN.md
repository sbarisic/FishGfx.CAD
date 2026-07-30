# External AMD GPU solver contract

FishGfx.CFD does not install or build the AMD solver stack. The operator must provide a
Foundation OpenFOAM 14-compatible PETSc adapter and set the Windows environment variable
`FISHGFX_CFD_GPU_ENV` to the absolute WSL path of its activation script.

The activation script must source Foundation OpenFOAM 14, configure ROCm, PETSc and Hypre,
put `fishgfx-cfd-petsc-smoke` on `PATH`, make `libpetscFoam.so` discoverable, and export
`FISHGFX_CFD_GPU_MANIFEST` with the absolute path of the manifest below. The activation
script must be non-interactive and safe to source repeatedly.

```json
{
  "schema": "fishgfx.cfd-gpu-toolchain",
  "version": 1,
  "distribution": "Foundation",
  "openFoamVersion": "14",
  "wmOptions": "linux64GccDPInt32Opt",
  "openFoamEnvironmentScriptPath": "/opt/openfoam14/etc/bashrc",
  "openFoamEnvironmentScriptSha256": "<64 lowercase hex digits>",
  "rocmVersion": "7.2",
  "hipVersion": "7.2",
  "gpuName": "AMD Radeon RX 9070 XT",
  "gpuPciAddress": "<WSL PCI address>",
  "gpuArchitectures": ["gfx1201"],
  "petscGitCommit": "<pinned PETSc main commit>",
  "petscConfigurationSha256": "<64 lowercase hex digits>",
  "petscScalarType": "real",
  "petscPrecision": "double",
  "petscIndexBits": 32,
  "hypreVersion": "<version>",
  "hypreConfiguration": "HIP enabled; <canonical configure options>",
  "adapterGitCommit": "<Foundation 14 adapter commit>",
  "adapterPortVersion": "foundation14-port-v1",
  "adapterAbi": "foundation-openfoam14-linux64GccDPInt32Opt-v1",
  "adapterLibraryPath": "/absolute/path/libpetscFoam.so",
  "adapterSha256": "<64 lowercase hex digits>"
}
```

`fishgfx-cfd-petsc-smoke --device N --json` must load the adapter, solve a finite
double-precision 32-bit-index sparse system through PETSc and Hypre on the selected HIP
device, and write one JSON object to stdout:

```json
{
  "schema": "fishgfx.cfd-gpu-smoke",
  "adapterLoaded": true,
  "petscHipActive": true,
  "hypreHipActive": true,
  "deviceIndex": 0,
  "deviceName": "AMD Radeon RX 9070 XT",
  "devicePciAddress": "<WSL PCI address>",
  "deviceArchitecture": "gfx1201",
  "iterations": 5,
  "initialResidual": 1.0,
  "finalResidual": 1e-10
}
```

The command must return nonzero when it cannot prove device execution. FishGfx.CFD also
checks the activation script, base OpenFOAM script, adapter and manifest hashes. A failed
check blocks `prepare`, `run`, `run-view` and `benchmark-compute` for AMD-GPU cases. There
is no automatic CPU fallback.

Run the validation with:

```text
dotnet run --project FishGfx.CFD/FishGfx.CFD.csproj -c Release -- gpu-doctor
```
