// MauiProgram.cs

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Debug;
using SkiaSharp.Views.Maui.Controls.Hosting;

namespace MauiSkiaTest;

/// <summary>
/// Provides startup configuration for the .NET MAUI application.
/// </summary>
public static class MauiProgram
{
	/// <summary>
	/// Creates and configures a .NET MAUI application.
	/// </summary>
	/// <remarks>Adds debug logging only in Debug builds.</remarks>
	/// <returns>A fully built <see cref="MauiApp"/> instance.</returns>
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseSkiaSharp()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug()
			.AddFilter<DebugLoggerProvider>("MauiSkiaTest.ObjModelRenderer", LogLevel.Debug);
#endif

		Routing.RegisterRoute(nameof(TestPage), typeof(TestPage));

		return builder.Build();
	}
}
