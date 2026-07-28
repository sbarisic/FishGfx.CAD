using System.Globalization;
using System.Numerics;
using FishGfx.Graphics;

namespace FishGfx.CFD;

internal sealed partial class CfdViewerApplication
{
	private static readonly IReadOnlyDictionary<string, CfdFieldDescriptor> FieldDescriptors =
		new Dictionary<string, CfdFieldDescriptor>(StringComparer.Ordinal)
		{
			["p"] = new("Static pressure", "Pa", 1),
			["T"] = new("Temperature", "K", 2),
			["U"] = new("Velocity magnitude", "m/s", 2),
			["rho"] = new("Density", "kg/m3", 4),
			["Ma"] = new("Mach number", "dimensionless", 4),
			["yPlus"] = new("Wall y+", "dimensionless", 2),
		};
	private string pickStatus = "Left-click visible data to inspect a value.";

	private void UpdatePickingInteraction()
	{
		if (!input.WasMouseButtonPressed(MouseButton.Left)
			|| ui.IsPointerOverControls(window.MousePosition))
		{
			return;
		}

		pickedValue = mode switch
		{
			"Slice" => PickSlice(input.MousePosition),
			"Velocity" => PickVelocity(input.MousePosition),
			"Streamlines" => PickStreamline(input.MousePosition),
			_ => PickSurface(input.MousePosition),
		};
		pickStatus = pickedValue == null
			? "No visible data sample was found at that point."
			: pickedValue.PrimaryText;
		RefreshLegend();
	}

	private CfdPickedValue? PickSurface(Vector2 mouse)
	{
		if (surfaceVertices.Length == 0 || surfaceIndices.Length < 3) return null;
		PickingRay ray = camera.CreatePickingRay(mouse);
		float nearest = float.PositiveInfinity;
		int nearestA = -1;
		int nearestB = -1;
		int nearestC = -1;
		float nearestU = 0;
		float nearestV = 0;
		for (int index = 0; index + 2 < surfaceIndices.Length; index += 3)
		{
			int a = checked((int)surfaceIndices[index]);
			int b = checked((int)surfaceIndices[index + 1]);
			int c = checked((int)surfaceIndices[index + 2]);
			if (!TryIntersectTriangle(
				ray,
				surfaceVertices[a],
				surfaceVertices[b],
				surfaceVertices[c],
				out float distance,
				out float u,
				out float v)
				|| distance >= nearest)
			{
				continue;
			}
			nearest = distance;
			nearestA = a;
			nearestB = b;
			nearestC = c;
			nearestU = u;
			nearestV = v;
		}

		if (nearestA < 0) return null;
		Vector3 position = ray.GetPoint(nearest);
		string effectiveField = EffectiveField;
		CfdFieldDescriptor descriptor = FieldDescriptors[effectiveField];
		string primary;
		if (TryPointValues(walls, effectiveField, out double[] values))
		{
			double value = values[nearestA] * (1 - nearestU - nearestV)
				+ values[nearestB] * nearestU
				+ values[nearestC] * nearestV;
			primary = $"{descriptor.Name}: {FormatValue(value, descriptor)}";
		}
		else
		{
			primary = $"{descriptor.Name}: unavailable on the wall surface";
		}
		return new CfdPickedValue(position, primary, FormatPosition(position));
	}

	private CfdPickedValue? PickSlice(Vector2 mouse)
	{
		if (volume == null || slicePointIndices.Length == 0) return null;
		int nearestIndex = -1;
		float nearestScreenDistance = 12 * 12;
		float nearestDepth = float.PositiveInfinity;
		foreach (int pointIndex in slicePointIndices)
		{
			Vector3 position = Point(volume.Points[pointIndex]);
			Vector3 screen = camera.WorldToScreen(position);
			if (!IsProjectedPointVisible(screen)) continue;
			float distance = Vector2.DistanceSquared(mouse, new Vector2(screen.X, screen.Y));
			if (distance > nearestScreenDistance
				|| distance == nearestScreenDistance && screen.Z >= nearestDepth)
			{
				continue;
			}
			nearestIndex = pointIndex;
			nearestScreenDistance = distance;
			nearestDepth = screen.Z;
		}

		if (nearestIndex < 0) return null;
		Vector3 pickedPosition = Point(volume.Points[nearestIndex]);
		CfdFieldDescriptor descriptor = FieldDescriptors[field];
		string primary = TryPointValues(volume, field, out double[] values)
			? $"{descriptor.Name}: {FormatValue(values[nearestIndex], descriptor)}"
			: $"{descriptor.Name}: unavailable in the volume data";
		return new CfdPickedValue(pickedPosition, primary, FormatPosition(pickedPosition));
	}

	private CfdPickedValue? PickVelocity(Vector2 mouse)
	{
		if (volume == null
			|| !volume.PointVectors.TryGetValue("U", out VtkVector[]? velocities))
		{
			return null;
		}
		VelocityArrow? nearest = null;
		float nearestScreenDistance = 12 * 12;
		float nearestDepth = float.PositiveInfinity;
		foreach (VelocityArrow arrow in arrows)
		{
			Vector3 start = camera.WorldToScreen(arrow.Start);
			Vector3 end = camera.WorldToScreen(arrow.End);
			if (!IsProjectedPointVisible(start) && !IsProjectedPointVisible(end)) continue;
			Vector2 startPoint = new(start.X, start.Y);
			Vector2 endPoint = new(end.X, end.Y);
			float amount = ClosestSegmentAmount(mouse, startPoint, endPoint);
			float distance = Vector2.DistanceSquared(mouse, Vector2.Lerp(startPoint, endPoint, amount));
			float depth = start.Z + (end.Z - start.Z) * amount;
			if (distance > nearestScreenDistance
				|| distance == nearestScreenDistance && depth >= nearestDepth)
			{
				continue;
			}
			nearest = arrow;
			nearestScreenDistance = distance;
			nearestDepth = depth;
		}

		if (nearest == null) return null;
		VtkVector velocity = velocities[nearest.Value.SourcePointIndex];
		CfdFieldDescriptor descriptor = FieldDescriptors["U"];
		string primary = $"{descriptor.Name}: {FormatValue(velocity.Length, descriptor)}";
		string components = string.Create(
			CultureInfo.InvariantCulture,
			$"U = ({velocity.X:F2}, {velocity.Y:F2}, {velocity.Z:F2}) m/s");
		return new CfdPickedValue(nearest.Value.Start, primary, components, FormatPosition(nearest.Value.Start));
	}

	private CfdPickedValue? PickStreamline(Vector2 mouse)
	{
		CfdStreamlinePoint? nearest = null;
		float nearestScreenDistance = 10 * 10;
		float nearestDepth = float.PositiveInfinity;
		foreach (CfdStreamline line in streamlines)
		for (int index = 1; index < line.Points.Length; ++index)
		{
			CfdStreamlinePoint previous = line.Points[index - 1];
			CfdStreamlinePoint current = line.Points[index];
			Vector3 start = camera.WorldToScreen(previous.Position);
			Vector3 end = camera.WorldToScreen(current.Position);
			if (!IsProjectedPointVisible(start) && !IsProjectedPointVisible(end)) continue;
			Vector2 startPoint = new(start.X, start.Y);
			Vector2 endPoint = new(end.X, end.Y);
			float amount = ClosestSegmentAmount(mouse, startPoint, endPoint);
			float distance = Vector2.DistanceSquared(mouse, Vector2.Lerp(startPoint, endPoint, amount));
			float depth = start.Z + (end.Z - start.Z) * amount;
			if (distance > nearestScreenDistance
				|| distance == nearestScreenDistance && depth >= nearestDepth)
			{
				continue;
			}
			nearest = new CfdStreamlinePoint(
				Vector3.Lerp(previous.Position, current.Position, amount),
				Vector3.Lerp(previous.Velocity, current.Velocity, amount),
				previous.Speed + (current.Speed - previous.Speed) * amount);
			nearestScreenDistance = distance;
			nearestDepth = depth;
		}

		if (nearest == null) return null;
		CfdStreamlinePoint point = nearest.Value;
		CfdFieldDescriptor descriptor = FieldDescriptors["U"];
		string primary = $"{descriptor.Name}: {FormatValue(point.Speed, descriptor)}";
		string components = string.Create(
			CultureInfo.InvariantCulture,
			$"U = ({point.Velocity.X:F2}, {point.Velocity.Y:F2}, {point.Velocity.Z:F2}) m/s");
		return new CfdPickedValue(point.Position, primary, components, FormatPosition(point.Position));
	}

	private void RefreshLegend()
	{
		string effectiveField = EffectiveField;
		CfdFieldDescriptor descriptor = FieldDescriptors[effectiveField];
		LegacyVtkDataSet? data = mode is "Slice" or "Velocity" or "Streamlines" ? volume : walls;
		string modeDescription = mode switch
		{
			"Surface" => "Gas-wall surface colored by point data",
			"Mesh" => "Gas-wall surface mesh; click to sample data",
			"Slice" => $"Mid-Z volume samples at {slicePlane * 1000:F1} mm",
			"Velocity" => "Velocity vectors; arrow length follows speed",
			"Streamlines" => $"{streamlines.Count} inlet-seeded paths through U",
			_ => mode,
		};
		string scaleDescription = mode switch
		{
			"Surface" => "Color: blue low - green midpoint - red high",
			"Slice" => "Color: blue low - green midpoint - red high",
			"Mesh" => "Range applies to picked wall values",
			"Velocity" => "Range controls relative arrow length",
			"Streamlines" => "Color follows speed along each flow path",
			_ => string.Empty,
		};
		double minimum = 0;
		double maximum = 0;
		bool hasValues = TryPointValues(data, effectiveField, out double[] values);
		bool hasRange = hasValues && (mode == "Slice"
			? TryRange(values, slicePointIndices, out minimum, out maximum)
			: TryRange(values, out minimum, out maximum));
		string unavailable = data == null
			? "No CFD result data is loaded."
			: $"{descriptor.Name} is unavailable for this mode.";
		ui.SetLegend(new CfdLegendState(
			mode,
			modeDescription,
			$"{descriptor.Name} ({effectiveField}) - {descriptor.Units}",
			scaleDescription,
			hasRange ? FormatValue(minimum, descriptor) : unavailable,
			hasRange ? FormatValue((minimum + maximum) / 2, descriptor) : string.Empty,
			hasRange ? FormatValue(maximum, descriptor) : string.Empty,
			hasRange,
			hasRange && mode is "Surface" or "Slice" or "Streamlines",
			pickStatus,
			pickedValue?.SecondaryText ?? string.Empty,
			pickedValue?.TertiaryText ?? string.Empty));
	}

	private string EffectiveField => mode is "Velocity" or "Streamlines" ? "U" : this.field;

	private string FormatPosition(Vector3 position)
	{
		float scale = volume == null ? 1 : 1000;
		return string.Create(
			CultureInfo.InvariantCulture,
			$"Position: {position.X * scale:F1}, {position.Y * scale:F1}, {position.Z * scale:F1} mm");
	}

	private static string FormatValue(double value, CfdFieldDescriptor descriptor)
	{
		string formatted = value.ToString($"F{descriptor.DecimalPlaces}", CultureInfo.InvariantCulture);
		return descriptor.Units == "dimensionless" ? formatted : $"{formatted} {descriptor.Units}";
	}

	private static bool TryPointValues(LegacyVtkDataSet? data, string name, out double[] values)
	{
		if (data == null)
		{
			values = [];
			return false;
		}
		if (name == "U" && data.PointVectors.TryGetValue(name, out VtkVector[]? vectors))
		{
			values = vectors.Select(item => item.Length).ToArray();
			return true;
		}
		if (data.PointScalars.TryGetValue(name, out double[]? scalars))
		{
			values = scalars;
			return true;
		}
		values = [];
		return false;
	}

	private static bool TryRange(double[] values, out double minimum, out double maximum)
	{
		return TryRange(values, Enumerable.Range(0, values.Length), out minimum, out maximum);
	}

	private static bool TryRange(
		double[] values,
		IEnumerable<int> indices,
		out double minimum,
		out double maximum)
	{
		minimum = double.PositiveInfinity;
		maximum = double.NegativeInfinity;
		foreach (int index in indices)
		{
			double value = values[index];
			if (!double.IsFinite(value)) continue;
			minimum = Math.Min(minimum, value);
			maximum = Math.Max(maximum, value);
		}
		return double.IsFinite(minimum) && double.IsFinite(maximum);
	}

	internal static bool TryIntersectTriangle(
		PickingRay ray,
		Vector3 a,
		Vector3 b,
		Vector3 c,
		out float distance,
		out float u,
		out float v)
	{
		Vector3 edge1 = b - a;
		Vector3 edge2 = c - a;
		Vector3 p = Vector3.Cross(ray.Direction, edge2);
		float determinant = Vector3.Dot(edge1, p);
		if (MathF.Abs(determinant) < 1e-7f)
		{
			distance = u = v = 0;
			return false;
		}
		float inverse = 1 / determinant;
		Vector3 t = ray.Origin - a;
		u = Vector3.Dot(t, p) * inverse;
		if (u < 0 || u > 1)
		{
			distance = v = 0;
			return false;
		}
		Vector3 q = Vector3.Cross(t, edge1);
		v = Vector3.Dot(ray.Direction, q) * inverse;
		distance = Vector3.Dot(edge2, q) * inverse;
		return v >= 0 && u + v <= 1 && distance >= 0;
	}

	internal static float ClosestSegmentAmount(Vector2 point, Vector2 start, Vector2 end)
	{
		Vector2 segment = end - start;
		float lengthSquared = segment.LengthSquared();
		return lengthSquared <= 1e-8f
			? 0
			: Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0, 1);
	}

	private static bool IsProjectedPointVisible(Vector3 point) =>
		float.IsFinite(point.X)
		&& float.IsFinite(point.Y)
		&& float.IsFinite(point.Z)
		&& point.Z is >= 0 and <= 1;

	private readonly record struct CfdFieldDescriptor(string Name, string Units, int DecimalPlaces);
	private readonly record struct VelocityArrow(
		Vector3 Start,
		Vector3 End,
		Color Color,
		int SourcePointIndex);
	private sealed record CfdPickedValue(
		Vector3 Position,
		string PrimaryText,
		string SecondaryText,
		string TertiaryText = "");
}

internal sealed record CfdLegendState(
	string Mode,
	string ModeDescription,
	string FieldDescription,
	string ScaleDescription,
	string Minimum,
	string Midpoint,
	string Maximum,
	bool HasRange,
	bool ShowColorScale,
	string PickText,
	string PickDetail,
	string PickDetail2);
