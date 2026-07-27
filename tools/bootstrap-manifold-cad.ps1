param(
	[ValidateSet("Debug", "Release")]
	[string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repository = Split-Path -Parent $PSScriptRoot
$vcpkgRoot = Join-Path $repository ".tools\vcpkg"
$cadNativeRoot = Join-Path $repository "FishGfx.CadKernel.Native"
$im3dNativeRoot = Join-Path $repository "FishGfx.Im3d.Native"

git -C $repository submodule update --init --recursive
if ($LASTEXITCODE -ne 0)
{
	throw "Failed to initialize the FishGfx and FishUI submodules."
}

if (-not (Test-Path (Join-Path $vcpkgRoot "vcpkg.exe")))
{
	New-Item -ItemType Directory -Path (Split-Path -Parent $vcpkgRoot) -Force | Out-Null

	if (-not (Test-Path (Join-Path $vcpkgRoot ".git")))
	{
		git clone --depth 1 https://github.com/microsoft/vcpkg.git $vcpkgRoot
		if ($LASTEXITCODE -ne 0)
		{
			throw "Failed to clone vcpkg."
		}
	}

	& (Join-Path $vcpkgRoot "bootstrap-vcpkg.bat") -disableMetrics
	if ($LASTEXITCODE -ne 0)
	{
		throw "Failed to bootstrap vcpkg."
	}
}

$manifest = Get-Content (Join-Path $cadNativeRoot "vcpkg.json") -Raw | ConvertFrom-Json
$baseline = $manifest.'builtin-baseline'

git -C $vcpkgRoot cat-file -e "$baseline^{commit}" 2>$null
if ($LASTEXITCODE -ne 0)
{
	git -C $vcpkgRoot fetch origin $baseline --depth 1
	if ($LASTEXITCODE -ne 0)
	{
		throw "Failed to fetch the pinned vcpkg baseline $baseline."
	}
}

function Invoke-NativeBuild
{
	param(
		[Parameter(Mandatory)]
		[string]$SourceRoot,
		[Parameter(Mandatory)]
		[string]$DisplayName
	)

	Push-Location $SourceRoot
	try
	{
		cmake --preset windows-x64
		if ($LASTEXITCODE -ne 0)
		{
			throw "Failed to configure $DisplayName."
		}

		cmake --build --preset ("windows-x64-" + $Configuration.ToLowerInvariant()) --parallel
		if ($LASTEXITCODE -ne 0)
		{
			throw "Failed to build $DisplayName."
		}

		ctest --preset ("windows-x64-" + $Configuration.ToLowerInvariant())
		if ($LASTEXITCODE -ne 0)
		{
			throw "$DisplayName tests failed."
		}
	}
	finally
	{
		Pop-Location
	}
}

Invoke-NativeBuild -SourceRoot $cadNativeRoot -DisplayName "the native CAD kernel"
Invoke-NativeBuild -SourceRoot $im3dNativeRoot -DisplayName "the native im3d bridge"

dotnet test (Join-Path $repository "FishGfx.ManifoldCad.Tests\FishGfx.ManifoldCad.Tests.csproj") `
	-c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0)
{
	throw "Managed CAD tests failed."
}

dotnet build (Join-Path $repository "FishGfx.CAD.sln") -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0)
{
	throw "FishGfx.CAD solution build failed."
}
