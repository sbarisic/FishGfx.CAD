using FishGfx.CFD;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class LegacyVtkTests
{
	[Fact]
	public void ReadsSupportedTetrahedronWithFields()
	{
		string path = Write("""
			# vtk DataFile Version 2.0
			fixture
			ASCII
			DATASET UNSTRUCTURED_GRID
			POINTS 4 float
			0 0 0 1 0 0 0 1 0 0 0 1
			CELLS 1 5
			4 0 1 2 3
			CELL_TYPES 1
			10
			CELL_DATA 1
			SCALARS p float 1
			LOOKUP_TABLE default
			101325
			VECTORS U float
			1 2 3
			""");
		try
		{
			LegacyVtkDataSet data = LegacyVtkReader.Read(path, true);
			Assert.Single(data.Cells);
			Assert.Equal(101325, data.CellScalars["p"][0]);
			Assert.Equal(new VtkVector(1, 2, 3), data.CellVectors["U"][0]);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void RejectsPolyhedronInsteadOfSkippingIt()
	{
		string path = Write("""
			# vtk DataFile Version 2.0
			fixture
			ASCII
			DATASET UNSTRUCTURED_GRID
			POINTS 4 float
			0 0 0 1 0 0 0 1 0 0 0 1
			CELLS 1 5
			4 0 1 2 3
			CELL_TYPES 1
			42
			""");
		try
		{
			NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
				LegacyVtkReader.Read(path, true));
			Assert.Contains("VTK_POLYHEDRON", error.Message);
		}
		finally { File.Delete(path); }
	}

	private static string Write(string content)
	{
		string path = Path.Combine(Path.GetTempPath(), $"fishgfx-vtk-{Guid.NewGuid():N}.vtk");
		File.WriteAllText(path, content.Replace("\r\n", "\n"));
		return path;
	}
}
