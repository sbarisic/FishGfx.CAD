using System.Numerics;

namespace FishGfx.CFD;

internal readonly record struct CfdBoundaryHit(
	Vector3 Position,
	Vector3 Normal,
	float SegmentParameter,
	string PatchName,
	string Role);

internal readonly record struct CfdStreamlineSeed(
	Vector3 Position,
	Vector3 InwardDirection,
	string PatchName);

internal sealed class CfdBoundaryBvh
{
	private const int LeafSize = 8;
	private readonly Triangle[] triangles;
	private readonly Node root;
	private readonly Vector3 boundsMinimum;
	private readonly Vector3 boundsMaximum;

	internal CfdBoundaryBvh(IReadOnlyList<CfdBoundaryPatch> patches, float spatialCellSize)
	{
		List<Triangle> source = [];
		foreach (CfdBoundaryPatch patch in patches.Where(value => value.Role is "walls" or "inlet" or "outlet"))
		{
			foreach (VtkCell cell in patch.Data.Cells)
			{
				if (cell.PointIndices.Length < 3) continue;
				Vector3 first = Point(patch.Data.Points[cell.PointIndices[0]]);
				for (int index = 1; index + 1 < cell.PointIndices.Length; ++index)
				{
					Vector3 b = Point(patch.Data.Points[cell.PointIndices[index]]);
					Vector3 c = Point(patch.Data.Points[cell.PointIndices[index + 1]]);
					if (Vector3.Cross(b - first, c - first).LengthSquared() <= 1e-20f) continue;
					source.Add(new(first, b, c, patch.Name, patch.Role));
				}
			}
		}
		if (source.Count == 0) throw new InvalidDataException("The CFD boundary contains no triangles.");
		triangles = source.ToArray();
		int[] indices = Enumerable.Range(0, triangles.Length).ToArray();
		root = Build(indices);
		boundsMinimum = root.Minimum;
		boundsMaximum = root.Maximum;
		Epsilon = Math.Clamp(spatialCellSize * 1e-4f, 1e-7f, 1e-5f);
	}

	internal float Epsilon { get; }

	internal bool IsInside(Vector3 point)
	{
		Vector3 direction = Vector3.Normalize(new Vector3(1, 0.371f, 0.193f));
		float length = Vector3.Distance(boundsMinimum, boundsMaximum) * 3 + 1;
		Vector3 end = point + direction * length;
		List<float> hits = [];
		CollectIntersections(root, point, end, hits);
		hits.Sort();
		int unique = 0;
		float previous = float.NegativeInfinity;
		foreach (float hit in hits)
		{
			if (hit * length <= Epsilon || MathF.Abs(hit - previous) * length <= Epsilon * 2) continue;
			++unique;
			previous = hit;
		}
		return (unique & 1) != 0;
	}

	internal bool TryIntersectSegment(
		Vector3 start,
		Vector3 end,
		out CfdBoundaryHit hit,
		string? ignorePatchNearStart = null)
	{
		float best = float.PositiveInfinity;
		int bestTriangle = -1;
		FindNearest(root, start, end, ignorePatchNearStart, ref best, ref bestTriangle);
		if (bestTriangle < 0)
		{
			hit = default;
			return false;
		}
		Triangle triangle = triangles[bestTriangle];
		hit = new(
			Vector3.Lerp(start, end, best),
			Vector3.Normalize(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A)),
			best,
			triangle.PatchName,
			triangle.Role);
		return true;
	}

	private Node Build(int[] indices)
	{
		(Vector3 minimum, Vector3 maximum) = Bounds(indices);
		if (indices.Length <= LeafSize) return new(minimum, maximum, indices, null, null);
		Vector3 extent = maximum - minimum;
		int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
		Array.Sort(indices, (left, right) => Component(triangles[left].Center, axis)
			.CompareTo(Component(triangles[right].Center, axis)));
		int middle = indices.Length / 2;
		return new(
			minimum,
			maximum,
			[],
			Build(indices[..middle]),
			Build(indices[middle..]));
	}

	private (Vector3 Minimum, Vector3 Maximum) Bounds(IReadOnlyList<int> indices)
	{
		Vector3 minimum = new(float.PositiveInfinity);
		Vector3 maximum = new(float.NegativeInfinity);
		foreach (int index in indices)
		{
			minimum = Vector3.Min(minimum, triangles[index].Minimum);
			maximum = Vector3.Max(maximum, triangles[index].Maximum);
		}
		return (minimum, maximum);
	}

	private void FindNearest(
		Node node,
		Vector3 start,
		Vector3 end,
		string? ignorePatchNearStart,
		ref float best,
		ref int bestTriangle)
	{
		if (!IntersectsBox(start, end, node.Minimum, node.Maximum)) return;
		if (node.Left != null)
		{
			FindNearest(node.Left, start, end, ignorePatchNearStart, ref best, ref bestTriangle);
			FindNearest(node.Right!, start, end, ignorePatchNearStart, ref best, ref bestTriangle);
			return;
		}
		float length = Vector3.Distance(start, end);
		foreach (int index in node.Indices)
		{
			Triangle triangle = triangles[index];
			if (!IntersectTriangle(start, end, triangle, out float amount) || amount >= best) continue;
			if (ignorePatchNearStart != null
				&& triangle.PatchName == ignorePatchNearStart
				&& amount * length <= Epsilon * 2) continue;
			if (amount * length <= Epsilon) continue;
			best = amount;
			bestTriangle = index;
		}
	}

	private void CollectIntersections(Node node, Vector3 start, Vector3 end, List<float> hits)
	{
		if (!IntersectsBox(start, end, node.Minimum, node.Maximum)) return;
		if (node.Left != null)
		{
			CollectIntersections(node.Left, start, end, hits);
			CollectIntersections(node.Right!, start, end, hits);
			return;
		}
		foreach (int index in node.Indices)
			if (IntersectTriangle(start, end, triangles[index], out float amount)) hits.Add(amount);
	}

	private static bool IntersectTriangle(Vector3 start, Vector3 end, Triangle triangle, out float amount)
	{
		Vector3 direction = end - start;
		Vector3 edge1 = triangle.B - triangle.A;
		Vector3 edge2 = triangle.C - triangle.A;
		Vector3 p = Vector3.Cross(direction, edge2);
		float determinant = Vector3.Dot(edge1, p);
		if (MathF.Abs(determinant) < 1e-12f)
		{
			amount = 0;
			return false;
		}
		float inverse = 1 / determinant;
		Vector3 t = start - triangle.A;
		float u = Vector3.Dot(t, p) * inverse;
		if (u < 0 || u > 1)
		{
			amount = 0;
			return false;
		}
		Vector3 q = Vector3.Cross(t, edge1);
		float v = Vector3.Dot(direction, q) * inverse;
		amount = Vector3.Dot(edge2, q) * inverse;
		return v >= 0 && u + v <= 1 && amount >= 0 && amount <= 1;
	}

	private static bool IntersectsBox(Vector3 start, Vector3 end, Vector3 minimum, Vector3 maximum)
	{
		Vector3 direction = end - start;
		float low = 0;
		float high = 1;
		for (int axis = 0; axis < 3; ++axis)
		{
			float origin = Component(start, axis);
			float delta = Component(direction, axis);
			float min = Component(minimum, axis);
			float max = Component(maximum, axis);
			if (MathF.Abs(delta) < 1e-20f)
			{
				if (origin < min || origin > max) return false;
				continue;
			}
			float a = (min - origin) / delta;
			float b = (max - origin) / delta;
			if (a > b) (a, b) = (b, a);
			low = MathF.Max(low, a);
			high = MathF.Min(high, b);
			if (low > high) return false;
		}
		return true;
	}

	private static float Component(Vector3 value, int axis) => axis switch
	{
		0 => value.X,
		1 => value.Y,
		_ => value.Z,
	};

	private static Vector3 Point(VtkVector point) => new((float)point.X, (float)point.Y, (float)point.Z);

	private sealed record Node(
		Vector3 Minimum,
		Vector3 Maximum,
		int[] Indices,
		Node? Left,
		Node? Right);

	private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, string PatchName, string Role)
	{
		internal Vector3 Minimum => Vector3.Min(A, Vector3.Min(B, C));
		internal Vector3 Maximum => Vector3.Max(A, Vector3.Max(B, C));
		internal Vector3 Center => (A + B + C) / 3;
	}
}
