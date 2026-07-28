using System.Diagnostics;
using System.Numerics;
using FishGfx.Cad;
using FishGfx.Game;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;

namespace FishGfx.CFD;

internal sealed class CfdViewerApplication : IDisposable
{
	private static readonly string[] Fields = ["p", "T", "U", "rho", "Ma", "yPlus"];
	private readonly RenderWindow window;
	private readonly InputManager input;
	private readonly Camera camera = new();
	private readonly Camera uiCamera = new();
	private readonly Mesh3D? surface;
	private readonly Mesh3D? wireframe;
	private readonly Mesh3D? slice;
	private RenderTarget? sceneTarget;
	private readonly List<(Vector3 Start, Vector3 End, Color Color)> arrows = [];
	private readonly CfdViewerUi ui;
	private readonly LegacyVtkDataSet? volume;
	private readonly LegacyVtkDataSet? walls;
	private readonly Vector3[] surfaceVertices = [];
	private readonly Vector3[] surfaceNormals = [];
	private readonly uint[] surfaceIndices = [];
	private bool showSurface = true;
	private bool showWireframe;
	private bool showSlice;
	private bool showArrows;
	private string field = "p";
	private int slicePointCount;
	private Vector3 orbitTarget;
	private float orbitDistance;
	private float orbitYaw;
	private float orbitPitch;
	private Vector2 previousOrbitMouse;
	private bool orbiting;
	private bool disposed;

	internal CfdViewerApplication(
		CadTessellation? tessellation,
		VerifiedOpenFoamResults? results = null,
		CfdResultSummary? summary = null)
	{
		window = new RenderWindow(1280, 800, "FishGfx.CFD — Steady Compressible Results", true);
		uiCamera.SetOrthogonal(0, 0, window.Width, window.Height);
		window.Resized += (_, args) => uiCamera.SetOrthogonal(0, 0, args.Width, args.Height);
		input = new InputManager(window);
		volume = results?.Volume;
		walls = results?.Boundaries.FirstOrDefault(item => item.Role == "walls")?.Data;
		if (walls != null)
		{
			(surfaceVertices, surfaceNormals, surfaceIndices) = BoundaryMesh(walls);
		}
		else if (tessellation != null)
		{
			surfaceVertices = tessellation.Vertices.Select(vertex =>
				new Vector3(vertex.X, vertex.Y, vertex.Z)).ToArray();
			surfaceNormals = tessellation.Vertices.Select(vertex =>
				new Vector3(vertex.NormalX, vertex.NormalY, vertex.NormalZ)).ToArray();
			surfaceIndices = tessellation.Indices;
		}
		if (surfaceVertices.Length > 0)
		{
			surface = CreateMesh(surfaceVertices, surfaceNormals, surfaceIndices, FieldColors(walls, field, surfaceVertices.Length));
			wireframe = CreateMesh(
				surfaceVertices,
				surfaceNormals,
				surfaceIndices,
				Enumerable.Repeat(new Color(225, 235, 245), surfaceVertices.Length).ToArray());
			wireframe.PolygonMode = PolygonMode.Line;
		}
		if (volume != null)
		{
			slice = window.Graphics.CreateMesh3D(BufferUsage.Dynamic);
			slice.PrimitiveType = PrimitiveType.Points;
			BuildSlice();
			BuildArrows();
		}
		FitCamera();
		ui = new CfdViewerUi(window, summary, Fields);
		ui.ModeRequested += mode =>
		{
			showSurface = mode == "Surface";
			showWireframe = mode == "Mesh";
			showSlice = mode == "Slice";
			showArrows = mode == "Velocity";
		};
		ui.FieldRequested += selected =>
		{
			field = selected;
			if (surface != null) surface.SetColors(FieldColors(walls, field, surfaceVertices.Length));
			BuildSlice();
		};
	}

	internal void Run()
	{
		Stopwatch timing = Stopwatch.StartNew();
		bool automatic = string.Equals(
			Environment.GetEnvironmentVariable("FISHGFX_CFD_AUTO"),
			"1",
			StringComparison.Ordinal);
		bool automaticComplete = false;
		while (!window.IsCloseRequested && !automaticComplete)
		{
			input.BeginFrame();
			ui.BeginFrame();
			window.PollEvents();
			UpdateCameraInteraction();
			ui.Update(1f / 60f, (float)timing.Elapsed.TotalSeconds);
			EnsureSceneTarget();
			using RenderFrame frame = window.Graphics.BeginFrame();
			using (RenderPass pass = frame.BeginPass(sceneTarget!, new RenderPassDescriptor
			{
				View = new RenderView(camera),
				State = RenderState.Default with
				{
					CullMode = CullMode.None,
					DepthTestEnabled = true,
					DepthWriteEnabled = true,
				},
				ColorLoadAction = RenderLoadAction.Clear,
				DepthLoadAction = RenderLoadAction.Clear,
				StencilLoadAction = RenderLoadAction.Clear,
				ClearColor = new Color(16, 20, 26),
			}))
			{
				if (showSurface && surface is not null) pass.DrawMesh(surface);
				if (showWireframe && wireframe is not null) pass.DrawMesh(wireframe);
				if (showSlice && slice is not null) pass.DrawMesh(slice);
				if (showArrows)
				{
					foreach ((Vector3 start, Vector3 end, Color color) in arrows)
						pass.DrawLine(new Vertex3(start, color), new Vertex3(end, color), 1.5f);
				}
			}
			using (RenderPass uiPass = frame.BeginPass(window.Graphics.Backbuffer, new RenderPassDescriptor
			{
				View = new RenderView(uiCamera),
				State = RenderState.Default with
				{
					CullMode = CullMode.None,
					DepthTestEnabled = false,
					DepthWriteEnabled = false,
				},
				ColorLoadAction = RenderLoadAction.Clear,
				DepthLoadAction = RenderLoadAction.Clear,
				StencilLoadAction = RenderLoadAction.Clear,
				ClearColor = new Color(16, 20, 26),
			}))
			{
				uiPass.DrawTexturedRectangle(
					0,
					0,
					window.Width,
					window.Height,
					0,
					1,
					1,
					0,
					Color.White,
					sceneTarget!.ColorAttachments[0]);
				ui.Render(uiPass, 1f / 60f, (float)timing.Elapsed.TotalSeconds);
			}
			frame.Present();
			if (automatic && timing.Elapsed.TotalSeconds > 1.5)
			{
				string screenshotPath = Path.GetFullPath(
					Environment.GetEnvironmentVariable("FISHGFX_CFD_AUTO_SCREENSHOT")
						?? Path.Combine(Path.GetTempPath(), "fishgfx-cfd-auto.png"));
				Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
				CaptureScreenshot(screenshotPath);
				Console.WriteLine(
					$"FISHGFX_CFD_AUTO_OK field={field} surfaceVertices={surfaceVertices.Length} "
						+ $"slicePoints={slicePointCount} arrows={arrows.Count} screenshot={screenshotPath}");
				automaticComplete = true;
			}
		}
	}

	private Mesh3D CreateMesh(Vector3[] vertices, Vector3[] normals, uint[] indices, Color[] colors)
	{
		Mesh3D result = window.Graphics.CreateMesh3D(BufferUsage.Dynamic);
		result.SetVertices(vertices);
		result.SetNormals(normals);
		result.SetColors(colors);
		result.SetElements(indices);
		return result;
	}

	private void EnsureSceneTarget()
	{
		if (sceneTarget != null
			&& sceneTarget.Width == window.Width
			&& sceneTarget.Height == window.Height)
		{
			return;
		}
		sceneTarget?.Dispose();
		sceneTarget = window.Graphics.CreateRenderTarget(
			new RenderTargetDescriptor(window.Width, window.Height));
	}

	private void FitCamera()
	{
		Vector3[] points = surfaceVertices.Length > 0
			? surfaceVertices
			: volume?.Points.Select(Point).ToArray() ?? [Vector3.Zero, Vector3.One];
		Vector3 minimum = new(points.Min(item => item.X), points.Min(item => item.Y), points.Min(item => item.Z));
		Vector3 maximum = new(points.Max(item => item.X), points.Max(item => item.Y), points.Max(item => item.Z));
		Vector3 center = (minimum + maximum) / 2;
		float distance = Math.Max((maximum - minimum).Length() * 1.4f, 0.01f);
		Vector3 direction = Vector3.Normalize(new Vector3(1, 0.65f, 1));
		orbitTarget = center;
		orbitDistance = distance;
		orbitYaw = MathF.Atan2(direction.X, direction.Z) * 180 / MathF.PI;
		orbitPitch = MathF.Asin(direction.Y) * 180 / MathF.PI;
		ConfigureCamera();
	}

	private void UpdateCameraInteraction()
	{
		Vector2 mouse = window.MousePosition;
		if (input.WasMouseButtonPressed(MouseButton.Right))
		{
			orbiting = !ui.IsPointerOverControls(mouse);
			previousOrbitMouse = mouse;
		}

		if (orbiting && input.IsMouseButtonDown(MouseButton.Right))
		{
			Vector2 delta = mouse - previousOrbitMouse;
			orbitYaw -= delta.X * 0.35f;
			orbitPitch = Math.Clamp(orbitPitch - delta.Y * 0.35f, -89, 89);
		}

		if (input.WasMouseButtonReleased(MouseButton.Right)) orbiting = false;
		previousOrbitMouse = mouse;
		ConfigureCamera();
	}

	private void ConfigureCamera()
	{
		float yaw = orbitYaw * MathF.PI / 180;
		float pitch = orbitPitch * MathF.PI / 180;
		Vector3 direction = new(
			MathF.Sin(yaw) * MathF.Cos(pitch),
			MathF.Sin(pitch),
			MathF.Cos(yaw) * MathF.Cos(pitch));
		camera.Position = orbitTarget + direction * orbitDistance;
		camera.CameraUpNormal = Vector3.UnitY;
		camera.LookAt(orbitTarget);
		camera.SetPerspective(
			Math.Max(window.Width, 1),
			Math.Max(window.Height, 1),
			MathF.PI / 3,
			orbitDistance / 1000,
			orbitDistance * 20);
	}

	private void BuildSlice()
	{
		if (volume == null || slice == null) return;
		float minimum = (float)volume.Points.Min(item => item.Z);
		float maximum = (float)volume.Points.Max(item => item.Z);
		float plane = (minimum + maximum) / 2;
		float thickness = Math.Max((maximum - minimum) * 0.006f, 1e-6f);
		int stride = Math.Max(1, volume.Points.Length / 30000);
		List<int> selected = [];
		for (int index = 0; index < volume.Points.Length; index += stride)
			if (Math.Abs(volume.Points[index].Z - plane) <= thickness) selected.Add(index);
		Vector3[] points = selected.Select(index => Point(volume.Points[index])).ToArray();
		slicePointCount = points.Length;
		slice.SetVertices(points);
		slice.SetColors(SelectedPointColors(volume, field, selected));
		slice.SetElements([]);
	}

	private void BuildArrows()
	{
		if (volume == null || !volume.PointVectors.TryGetValue("U", out VtkVector[]? velocity)) return;
		double maximum = velocity.Max(item => item.Length);
		if (!(maximum > 0)) return;
		Vector3 extent = new(
			(float)(volume.Points.Max(item => item.X) - volume.Points.Min(item => item.X)),
			(float)(volume.Points.Max(item => item.Y) - volume.Points.Min(item => item.Y)),
			(float)(volume.Points.Max(item => item.Z) - volume.Points.Min(item => item.Z)));
		float scale = extent.Length() * 0.04f / (float)maximum;
		int stride = Math.Max(1, volume.Points.Length / 600);
		for (int index = 0; index < volume.Points.Length; index += stride)
		{
			Vector3 start = Point(volume.Points[index]);
			Vector3 vector = Point(velocity[index]) * scale;
			arrows.Add((start, start + vector, new Color(245, 200, 80)));
		}
	}

	private static (Vector3[] Vertices, Vector3[] Normals, uint[] Indices) BoundaryMesh(LegacyVtkDataSet data)
	{
		Vector3[] vertices = data.Points.Select(Point).ToArray();
		Vector3[] normals = new Vector3[vertices.Length];
		List<uint> indices = [];
		foreach (VtkCell cell in data.Cells)
		{
			for (int index = 1; index + 1 < cell.PointIndices.Length; ++index)
			{
				int a = cell.PointIndices[0];
				int b = cell.PointIndices[index];
				int c = cell.PointIndices[index + 1];
				indices.Add((uint)a); indices.Add((uint)b); indices.Add((uint)c);
				Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
				normals[a] += normal; normals[b] += normal; normals[c] += normal;
			}
		}
		for (int index = 0; index < normals.Length; ++index)
			normals[index] = normals[index].LengthSquared() > 0 ? Vector3.Normalize(normals[index]) : Vector3.UnitZ;
		return (vertices, normals, indices.ToArray());
	}

	private static Color[] FieldColors(LegacyVtkDataSet? data, string name, int count)
	{
		if (data == null) return Enumerable.Repeat(new Color(70, 165, 220), count).ToArray();
		double[] values = PointValues(data, name);
		return Colors(values, count);
	}

	private static Color[] SelectedPointColors(LegacyVtkDataSet data, string name, List<int> selected)
	{
		double[] all = PointValues(data, name);
		return Colors(selected.Select(index => all[index]).ToArray(), selected.Count);
	}

	private static double[] PointValues(LegacyVtkDataSet data, string name)
	{
		if (name == "U" && data.PointVectors.TryGetValue(name, out VtkVector[]? vectors))
			return vectors.Select(item => item.Length).ToArray();
		if (data.PointScalars.TryGetValue(name, out double[]? values)) return values;
		return new double[data.Points.Length];
	}

	private static Color[] Colors(double[] values, int count)
	{
		if (values.Length == 0) return [];
		double minimum = values.Where(double.IsFinite).DefaultIfEmpty(0).Min();
		double maximum = values.Where(double.IsFinite).DefaultIfEmpty(1).Max();
		double range = Math.Max(maximum - minimum, double.Epsilon);
		return Enumerable.Range(0, count).Select(index =>
		{
			double t = Math.Clamp((values[index] - minimum) / range, 0, 1);
			return new Color(
				(byte)(30 + 225 * t),
				(byte)(80 + 140 * (1 - Math.Abs(2 * t - 1))),
				(byte)(240 - 210 * t));
		}).ToArray();
	}

	private static Vector3 Point(VtkVector value) => new((float)value.X, (float)value.Y, (float)value.Z);

	private unsafe void CaptureScreenshot(string path)
	{
		window.ReadPixels();
		using System.Drawing.Bitmap bitmap = new(
			window.Width,
			window.Height,
			System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		System.Drawing.Rectangle rectangle = new(0, 0, bitmap.Width, bitmap.Height);
		System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
			rectangle,
			System.Drawing.Imaging.ImageLockMode.WriteOnly,
			System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		try
		{
			for (int y = 0; y < bitmap.Height; ++y)
			for (int x = 0; x < bitmap.Width; ++x)
			{
				Color color = window.GetPixel(x, y);
				byte* pixel = (byte*)data.Scan0 + y * data.Stride + x * 4;
				pixel[0] = color.B;
				pixel[1] = color.G;
				pixel[2] = color.R;
				pixel[3] = color.A;
			}
		}
		finally
		{
			bitmap.UnlockBits(data);
		}
		bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		ui.Dispose();
		surface?.Dispose();
		wireframe?.Dispose();
		slice?.Dispose();
		sceneTarget?.Dispose();
		input.Dispose();
		window.Graphics.CollectGarbage();
		window.Dispose();
	}
}
