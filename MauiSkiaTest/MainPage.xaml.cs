// MainPage.xaml.cs

namespace MauiSkiaTest;

/// <summary>
/// Represents the main application page.
/// </summary>
public partial class MainPage : ContentPage
{
	int count = 0;

	/// <summary>
	/// Initializes a new instance of the <see cref="MainPage"/> class.
	/// </summary>
	public MainPage()
	{
		InitializeComponent();
	}

	async void OnNavigateClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { CommandParameter: string assetName } || string.IsNullOrWhiteSpace(assetName))
		{
			throw new InvalidOperationException("The navigation button must specify an asset filename.");
		}

		await Shell.Current.GoToAsync(nameof(TestPage), new Dictionary<string, object>
		{
			["assetName"] = assetName
		});
	}
}
