using System.Globalization;

namespace FishGfx.CFD;

public readonly record struct VtkVector(double X, double Y, double Z)
{
	public static VtkVector operator -(VtkVector left, VtkVector right) =>
		new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
	public static VtkVector Cross(VtkVector left, VtkVector right) => new(
		left.Y * right.Z - left.Z * right.Y,
		left.Z * right.X - left.X * right.Z,
		left.X * right.Y - left.Y * right.X);
	public double Dot(VtkVector other) => X * other.X + Y * other.Y + Z * other.Z;
	public double Length => Math.Sqrt(Dot(this));
}

public sealed record VtkCell(int Type, int[] PointIndices);

public sealed class LegacyVtkDataSet
{
	public required VtkVector[] Points { get; init; }
	public required VtkCell[] Cells { get; init; }
	public Dictionary<string, double[]> PointScalars { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, VtkVector[]> PointVectors { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, double[]> CellScalars { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, VtkVector[]> CellVectors { get; } = new(StringComparer.Ordinal);
}

public static class LegacyVtkReader
{
	private static readonly HashSet<int> SupportedVolumeTypes = [10, 12, 13, 14];
	private static readonly HashSet<int> SupportedSurfaceTypes = [5, 7, 9];

	public static LegacyVtkDataSet Read(string path, bool volumeData)
	{
		string[] lines = File.ReadAllLines(path);
		if (lines.Length < 4 || !lines[0].StartsWith("# vtk DataFile", StringComparison.OrdinalIgnoreCase)
			|| !lines[2].Equals("ASCII", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Only legacy ASCII VTK datasets are supported.");
		}
		TokenReader reader = new(lines.Skip(3).SelectMany(line => line.Split(
			(char[]?)null,
			StringSplitOptions.RemoveEmptyEntries)));
		string dataset = reader.Expect("DATASET").Next();
		if (dataset is not ("UNSTRUCTURED_GRID" or "POLYDATA"))
		{
			throw new InvalidDataException($"Unsupported VTK dataset type '{dataset}'.");
		}
		reader.Expect("POINTS");
		int pointCount = reader.Int();
		reader.Next();
		VtkVector[] points = new VtkVector[pointCount];
		for (int index = 0; index < pointCount; ++index)
		{
			points[index] = new(reader.Double(), reader.Double(), reader.Double());
		}

		List<VtkCell> cells = [];
		if (dataset == "UNSTRUCTURED_GRID")
		{
			reader.Expect("CELLS");
			int cellCount = reader.Int();
			reader.Int();
			int[][] indices = ReadConnectivity(reader, cellCount);
			reader.Expect("CELL_TYPES");
			int typeCount = reader.Int();
			if (typeCount != cellCount) throw new InvalidDataException("VTK cell type count mismatch.");
			for (int index = 0; index < cellCount; ++index)
			{
				int type = reader.Int();
				if (volumeData && !SupportedVolumeTypes.Contains(type)
					|| !volumeData && !SupportedSurfaceTypes.Contains(type))
				{
					throw new NotSupportedException(
						type == 42 ? "VTK_POLYHEDRON is not supported." : $"VTK cell type {type} is not supported.");
				}
				cells.Add(new(type, indices[index]));
			}
		}
		else
		{
			reader.Expect("POLYGONS");
			int cellCount = reader.Int();
			reader.Int();
			int[][] indices = ReadConnectivity(reader, cellCount);
			foreach (int[] cell in indices)
			{
				int type = cell.Length switch { 3 => 5, 4 => 9, _ => 7 };
				cells.Add(new(type, cell));
			}
		}

		LegacyVtkDataSet result = new() { Points = points, Cells = cells.ToArray() };
		while (reader.HasMore)
		{
			string association = reader.Next();
			if (association is not ("POINT_DATA" or "CELL_DATA"))
			{
				throw new InvalidDataException($"Unexpected VTK token '{association}'.");
			}
			int count = reader.Int();
			Dictionary<string, double[]> scalars = association == "POINT_DATA"
				? result.PointScalars : result.CellScalars;
			Dictionary<string, VtkVector[]> vectors = association == "POINT_DATA"
				? result.PointVectors : result.CellVectors;
			while (reader.HasMore && reader.Peek() is not ("POINT_DATA" or "CELL_DATA"))
			{
				string kind = reader.Next();
				if (kind == "SCALARS")
				{
					string name = reader.Next();
					reader.Next();
					int components = reader.Peek() == "LOOKUP_TABLE" ? 1 : reader.Int();
					reader.Expect("LOOKUP_TABLE");
					reader.Next();
					if (components != 1) throw new NotSupportedException("Multi-component VTK SCALARS are unsupported.");
					double[] values = new double[count];
					for (int index = 0; index < count; ++index) values[index] = reader.Double();
					scalars[name] = values;
				}
				else if (kind == "VECTORS")
				{
					string name = reader.Next();
					reader.Next();
					VtkVector[] values = new VtkVector[count];
					for (int index = 0; index < count; ++index)
					{
						values[index] = new(reader.Double(), reader.Double(), reader.Double());
					}
					vectors[name] = values;
				}
				else if (kind == "FIELD")
				{
					reader.Next();
					int arrayCount = reader.Int();
					for (int array = 0; array < arrayCount; ++array)
					{
						string name = reader.Next();
						int components = reader.Int();
						int tuples = reader.Int();
						reader.Next();
						if (tuples != count || components is not (1 or 3))
							throw new NotSupportedException("Unsupported legacy VTK FIELD array shape.");
						if (components == 1)
						{
							double[] values = new double[count];
							for (int index = 0; index < count; ++index) values[index] = reader.Double();
							scalars[name] = values;
						}
						else
						{
							VtkVector[] values = new VtkVector[count];
							for (int index = 0; index < count; ++index)
								values[index] = new(reader.Double(), reader.Double(), reader.Double());
							vectors[name] = values;
						}
					}
				}
				else
				{
					throw new NotSupportedException($"VTK field construct '{kind}' is not supported.");
				}
			}
		}
		return result;
	}

	private static int[][] ReadConnectivity(TokenReader reader, int count)
	{
		int[][] result = new int[count][];
		for (int index = 0; index < count; ++index)
		{
			int size = reader.Int();
			result[index] = new int[size];
			for (int point = 0; point < size; ++point) result[index][point] = reader.Int();
		}
		return result;
	}

	private sealed class TokenReader
	{
		private readonly string[] values;
		private int index;
		internal TokenReader(IEnumerable<string> values) => this.values = values.ToArray();
		internal bool HasMore => index < values.Length;
		internal string Peek() => HasMore ? values[index] : string.Empty;
		internal string Next() => HasMore ? values[index++] : throw new EndOfStreamException();
		internal TokenReader Expect(string expected)
		{
			string actual = Next();
			if (actual != expected) throw new InvalidDataException($"Expected VTK token '{expected}', found '{actual}'.");
			return this;
		}
		internal int Int() => int.Parse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture);
		internal double Double()
		{
			double result = double.Parse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture);
			if (!double.IsFinite(result)) throw new InvalidDataException("VTK contains a non-finite value.");
			return result;
		}
	}
}
