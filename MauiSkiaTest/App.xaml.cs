// App.xaml.cs

namespace MauiSkiaTest;

/// <summary>
/// Represents the .NET MAUI application entry point.
/// </summary>
public partial class App : Application
{
	/// <summary>
	/// Initializes a new instance of the <see cref="App"/> class.
	/// </summary>
	public App()
	{
		InitializeComponent();
	}

	/// <summary>
	/// Creates the main application window with an AppShell root page.
	/// </summary>
	/// <param name="activationState">The activation state used to restore the window, or null when no activation data is available.</param>
	/// <returns>A new Window instance initialized with an AppShell.</returns>
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}
