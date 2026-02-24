using BetterGenshinImpact.ViewModel.Pages;
using System.Windows.Input;

namespace BetterGenshinImpact.View.Pages;

public partial class AiChatPage
{
    public AiChatViewModel ViewModel { get; }

    public AiChatPage(AiChatViewModel viewModel)
    {
        DataContext = ViewModel = viewModel;
        InitializeComponent();
    }

    private void InputTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            return;
        }

        if (!ViewModel.SendMessageCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        ViewModel.SendMessageCommand.Execute(null);
    }
}
