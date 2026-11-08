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
	/// Creates the .NET MAUI application instance.
	/// </summary>
	/// <returns>A configured <see cref="MauiApp"/> instance for the application.</returns>
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
