using FishGfx.Cad;

namespace FishGfx.CFD;

public sealed record PreparedCfdPackage(
	CfdPreparedGeometry Geometry,
	CadTessellation Tessellation,
	IReadOnlyList<CfdPatchMatchDiagnostic> Diagnostics);

public static class CfdGeometryPipeline
{
	public static PreparedCfdPackage Prepare(
		LoadedGasPackage package,
		string pathId,
		string workingDirectory)
	{
		Directory.CreateDirectory(workingDirectory);
		string step = Path.Combine(workingDirectory, "geometry.step");
		string geometryDirectory = Path.Combine(workingDirectory, "geometry");
		Directory.CreateDirectory(geometryDirectory);
		string stl = Path.Combine(geometryDirectory, "gas-domain.stl");
		File.WriteAllBytes(step, package.GeometryStep);
		GasPathManifest path = package.Manifest.Paths.Single(item => item.Id == pathId);
		using CfdGasGeometry geometry = CfdGasGeometry.ImportStep(step);
		CadCfdGasPathInfo nativePath = geometry.GetPaths().Single(item => item.Id == pathId);
		if (!string.Equals(nativePath.ComponentName, path.ComponentName, StringComparison.Ordinal))
		{
			throw new InvalidDataException("The STEP gas-path component identity does not match patches.json.");
		}
		CadCfdOpeningSpec[] openings = path.Openings.Select(ToNative).ToArray();
		CadCfdGeometryPreparation preparation = geometry.PreparePath(
			pathId,
			openings,
			package.Manifest.MatchingPolicy);
		geometry.ExportMultiRegionStl(stl);
		CadTessellation tessellation = geometry.Tessellate();
		CfdPreparedGeometry prepared = new(
			stl,
			Meters(preparation.MinimumMm),
			Meters(preparation.MaximumMm),
			Meters(preparation.InteriorPointMm),
			preparation.SmallestInletHydraulicDiameterMm);
		return new(
			prepared,
			tessellation,
			preparation.Matches.Select(match => new CfdPatchMatchDiagnostic(
				match.OpeningId,
				match.SelectedCandidate,
				match.BestScore,
				double.IsPositiveInfinity(match.SecondBestScore) ? null : match.SecondBestScore,
				DecodeFailures(match.FailedToleranceMask))).ToArray());
	}

	private static CadCfdOpeningSpec ToNative(GasOpeningManifest opening)
	{
		GasOpeningFingerprint fingerprint = opening.Fingerprint;
		return new(
			opening.Id,
			opening.PatchName,
			opening.Role == "inlet" ? CadCfdOpeningRole.Inlet : CadCfdOpeningRole.Outlet,
			new CadCfdOpeningFingerprint(
				fingerprint.Area,
				Point(fingerprint.Centroid),
				Point(fingerprint.Normal),
				fingerprint.LoopCount,
				fingerprint.Perimeter,
				fingerprint.EdgeLengths,
				fingerprint.LoopSamples.Select(Point).ToArray()));
	}

	private static CfdPoint3 Meters(CadPoint3 value) => new(value.X * 0.001, value.Y * 0.001, value.Z * 0.001);
	private static CadPoint3 Point(double[] value)
	{
		if (value.Length != 3) throw new InvalidDataException("A gas manifest point must have three values.");
		return new(value[0], value[1], value[2]);
	}

	private static string[] DecodeFailures(uint mask)
	{
		List<string> result = [];
		if ((mask & 1) != 0) result.Add("area");
		if ((mask & 2) != 0) result.Add("centroid");
		if ((mask & 4) != 0) result.Add("normal");
		if ((mask & 8) != 0) result.Add("loop-count");
		if ((mask & 16) != 0) result.Add("perimeter");
		return result.ToArray();
	}
}
