// AppDelegate.cs

using Foundation;

namespace MauiSkiaTest;

/// <summary>
/// Represents the iOS application delegate for the .NET MAUI app.
/// </summary>
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	/// <summary>
	/// Creates and configures the .NET MAUI application instance.
	/// </summary>
	/// <returns>A configured <see cref="MauiApp"/> instance.</returns>
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
