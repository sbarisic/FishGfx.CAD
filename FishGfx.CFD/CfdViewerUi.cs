using System.Numerics;
using FishGfx.FishUI;
using FishGfx.Graphics;
using FishUI.Controls;
using FishUIRuntime = global::FishUI.FishUI;

namespace FishGfx.CFD;

internal sealed class CfdViewerUi : IDisposable
{
	private static readonly Vector2 ControlsPosition = new(10, 10);
	private static readonly Vector2 ControlsSize = new(430, 142);
	private readonly FishUIGraphicsBackend graphics;
	private readonly FishUIInputAdapter input;
	private readonly FishUIRuntime ui;

	internal CfdViewerUi(RenderWindow window, CfdResultSummary? summary, IReadOnlyList<string> fields)
	{
		graphics = new FishUIGraphicsBackend(window);
		input = new FishUIInputAdapter(window) { Enabled = true };
		global::FishUI.FishUISettings settings = new();
		ui = new FishUIRuntime(settings, graphics, input, new NullFishUIEvents(), graphics.FileSystem);
		ui.Init();
		settings.LoadTheme("data/themes/gwen.yaml");
		Panel panel = new()
		{
			Position = ControlsPosition,
			Size = ControlsSize,
			Variant = PanelVariant.Dark,
		};
		CfdResidualSample? lastResidual = summary?.Residuals.LastOrDefault();
		panel.AddChild(new Label(summary == null
			? "Published gas geometry"
			: $"{summary.Status} | dP0 {summary.PressureLossPa:F1} Pa | imbalance {summary.MassImbalanceFraction:P2} | y+ {summary.YPlusAreaWeightedMean:F2}"
				+ (lastResidual == null ? string.Empty : $" | {lastResidual.Field} r={lastResidual.InitialResidual:E1}"))
		{
			Position = new Vector2(12, 10),
			Size = new Vector2(405, 24),
		});
		string[] modes = ["Surface", "Mesh", "Slice", "Velocity"];
		for (int index = 0; index < modes.Length; ++index)
		{
			string mode = modes[index];
			Button button = new() { Position = new Vector2(12 + index * 100, 42), Size = new Vector2(92, 28), Text = mode };
			button.OnButtonPressed += (_, mouse, _) =>
			{
				if (mouse == global::FishUI.FishMouseButton.Left) ModeRequested?.Invoke(mode);
			};
			panel.AddChild(button);
		}
		for (int index = 0; index < fields.Count; ++index)
		{
			string field = fields[index];
			Button button = new() { Position = new Vector2(12 + index * 66, 82), Size = new Vector2(58, 28), Text = field };
			button.OnButtonPressed += (_, mouse, _) =>
			{
				if (mouse == global::FishUI.FishMouseButton.Left) FieldRequested?.Invoke(field);
			};
			panel.AddChild(button);
		}
		panel.AddChild(new Label("Right-drag the viewport to rotate")
		{
			Position = new Vector2(12, 116),
			Size = new Vector2(405, 18),
		});
		ui.AddControl(panel);
	}

	internal event Action<string>? ModeRequested;
	internal event Action<string>? FieldRequested;
	internal bool IsPointerOverControls(Vector2 point) =>
		point.X >= ControlsPosition.X
		&& point.X <= ControlsPosition.X + ControlsSize.X
		&& point.Y >= ControlsPosition.Y
		&& point.Y <= ControlsPosition.Y + ControlsSize.Y;
	internal void BeginFrame() => input.BeginFrame();
	internal void Update(float deltaTime, float time) => ui.TickUpdate(deltaTime, time);
	internal void Render(RenderPass pass, float deltaTime, float time)
	{
		using (graphics.UseRenderPass(pass, pass.View, pass.State)) ui.TickDraw(deltaTime, time);
	}
	public void Dispose() { input.Dispose(); graphics.Dispose(); }
}
