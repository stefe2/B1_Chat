using System.Windows.Controls;

namespace b1_chat_console.Views;

public partial class FirmwareCardView : UserControl
{
    public FirmwareCardView() => InitializeComponent();

    private void FlashLogScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0 && sender is ScrollViewer sv) sv.ScrollToEnd();
    }
}
