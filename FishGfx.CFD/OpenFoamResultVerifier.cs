namespace FishGfx.CFD;

public sealed record VerifiedOpenFoamResults(
	LegacyVtkDataSet Volume,
	IReadOnlyList<CfdBoundaryPatch> Boundaries);

public static class OpenFoamResultVerifier
{
	public static VerifiedOpenFoamResults Verify(string resultDirectory, GasPathManifest path)
	{
		string vtkRoot = Path.Combine(resultDirectory, "VTK");
		if (!Directory.Exists(vtkRoot))
		{
			throw new InvalidDataException("foamToVTK did not produce a VTK directory.");
		}
		string[] files = Directory.GetFiles(vtkRoot, "*.vtk", SearchOption.AllDirectories);
		string volumePath = files
			.Where(file => string.Equals(Path.GetDirectoryName(file), vtkRoot, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault()
			?? throw new InvalidDataException("foamToVTK did not produce the expected volume dataset.");
		LegacyVtkDataSet volume = LegacyVtkReader.Read(volumePath, true);
		RequireFields(volume, false, ["p", "T", "rho", "Ma", "yPlus"], ["U"]);

		List<CfdBoundaryPatch> boundaries = [];
		foreach (GasOpeningManifest opening in path.Openings)
		{
			string boundaryPath = FindBoundary(
				files,
				OpenFoamCaseGenerator.MeshPatchName(opening.PatchName));
			LegacyVtkDataSet data = LegacyVtkReader.Read(boundaryPath, false);
			RequireFields(data, true, ["p", "T", "rho", "Ma"], ["U"]);
			boundaries.Add(new(opening.PatchName, opening.Role, data));
		}
		string wallsPath = FindBoundary(files, OpenFoamCaseGenerator.MeshPatchName("walls"));
		LegacyVtkDataSet walls = LegacyVtkReader.Read(wallsPath, false);
		RequireFields(walls, true, ["yPlus"], []);
		boundaries.Add(new("walls", "walls", walls));
		return new(volume, boundaries);
	}

	private static string FindBoundary(IEnumerable<string> files, string patchName)
	{
		string[] matches = files.Where(file =>
			string.Equals(new DirectoryInfo(Path.GetDirectoryName(file)!).Name, patchName, StringComparison.Ordinal)
			|| Path.GetFileNameWithoutExtension(file).StartsWith(patchName + "_", StringComparison.Ordinal))
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.ToArray();
		return matches.FirstOrDefault()
			?? throw new InvalidDataException($"foamToVTK did not produce boundary dataset '{patchName}'.");
	}

	private static void RequireFields(
		LegacyVtkDataSet data,
		bool boundary,
		IEnumerable<string> scalars,
		IEnumerable<string> vectors)
	{
		foreach (string field in scalars)
		{
			if (!data.CellScalars.ContainsKey(field) && !data.PointScalars.ContainsKey(field))
				throw new InvalidDataException($"VTK dataset is missing scalar field '{field}'.");
		}
		foreach (string field in vectors)
		{
			if (!data.CellVectors.ContainsKey(field) && !data.PointVectors.ContainsKey(field))
				throw new InvalidDataException($"VTK dataset is missing vector field '{field}'.");
		}
		if (boundary && data.Cells.Length == 0)
			throw new InvalidDataException("A boundary VTK dataset contains no faces.");
	}
}
