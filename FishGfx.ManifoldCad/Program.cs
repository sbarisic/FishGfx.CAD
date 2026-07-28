namespace FishGfx.ManifoldCad;

internal static class Program
{
	[STAThread]
	private static int Main(string[] args)
	{
		using ApplicationLog log = ApplicationLog.Start();
		log.Info($"Arguments: {string.Join(' ', args.Select(QuoteArgument))}");
		log.Info($"Log file: {log.Path}");
		try
		{
			if (args.Length == 3
				&& string.Equals(args[0], "--export-cfd", StringComparison.OrdinalIgnoreCase))
			{
				HeadlessCfdExport.Export(args[1], args[2]);
				return 0;
			}
			using ManifoldCadApplication application = new(args);
			application.Run();
			return 0;
		}
		catch (Exception exception)
		{
			log.Exception("Fatal application failure.", exception);
			Console.Error.WriteLine(exception);
			return 1;
		}
	}

	private static string QuoteArgument(string value)
	{
		return value.Contains(' ') ? $"\"{value}\"" : value;
	}
}
