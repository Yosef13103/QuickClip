using System.Windows;

namespace QuickClip.Views;

internal static class TextPrompt
{
    public static string? Ask(Window? owner, string prompt, string initial, string okText, bool allowEmpty = false) =>
        ModalDialog.Ask(owner, prompt, initial, okText, allowEmpty);
}
