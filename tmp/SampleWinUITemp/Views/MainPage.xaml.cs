namespace SampleWinUITemp.Views
{
    /// <summary>
    /// A simple page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public partial class MainPage : Page
    {
        int count = 0;

        public MainPage()
        {
            this.InitializeComponent();
        }

        private void OnCountClicked(object sender, RoutedEventArgs e)
            => txtCount.Text = $"Current count: {count++}";
    }
}
