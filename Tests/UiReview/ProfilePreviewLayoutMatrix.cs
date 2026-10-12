using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VatscaUpdateChecker;

internal static partial class LayoutMatrix
{
    private static int CheckProfilePreviewLayouts()
    {
        const string masked = "ES_TEST.prf\nName: Synthetic Controller\nPassword: ********\nServer: AUTOMATIC";
        const string revealed = "ES_TEST.prf\nName: Synthetic Controller\nPassword: SYNTHETIC-NOT-A-PASSWORD\nServer: AUTOMATIC";
        const string summary = "What changes\nApply the entered name, CID, rating and password to the Swedish EuroScope profiles. The server becomes AUTOMATIC.\n\nWhat stays\nOther settings and existing Hoppie codes stay in place.\n\nRecovery\nThis action does not create a recovery copy. Copy custom profiles before applying.\n\nBefore you continue\nPasswords are written into profile files. This is a read-only preview; return to the profile form to save and apply. All values in this test are synthetic.";
        var previousTextScale = UiFixture.TextScale;
        int layouts = 0;
        try
        {
            foreach (var textScale in new[] { 1d, 1.25d, 1.5d })
            foreach (var minimum in new[] { false, true })
            {
                UiFixture.SetTextScale(textScale);
                int revealCalls = 0;
                var window = new ProfilePreviewWindow(summary, masked, () =>
                {
                    revealCalls++;
                    return revealed;
                });
                if (minimum)
                {
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                }
                try
                {
                    var details = Required<Expander>(window, "ProfileDetails");
                    var preview = Required<TextBox>(window, "PreviewDetailsText");
                    var toggle = Required<Button>(window, "ToggleCredentialsButton");
                    var back = Required<Button>(window, "BackButton");
                    LayoutValidation.CheckWindowContent(window);
                    CheckFooter(window);
                    Require(!details.IsExpanded && preview.Text == masked && revealCalls == 0,
                        "Opening the profile preview must leave credentials masked and must not request clear text.");
                    Require(preview.IsReadOnly && back.IsCancel && toggle.IsTabStop && back.IsTabStop,
                        "The profile preview must remain read-only, keyboard reachable and cancellable.");
                    Require(Math.Abs(toggle.FontSize - 12 * textScale) < .01 && Math.Abs(back.FontSize - 12 * textScale) < .01,
                        "Profile preview actions must use the selected text size.");

                    details.IsExpanded = true;
                    LayoutValidation.CheckWindowContent(window);
                    CheckFooter(window);
                    Require(preview.Text == masked && revealCalls == 0,
                        "Expanding file changes must not reveal or request credentials.");

                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    LayoutValidation.CheckWindowContent(window);
                    CheckFooter(window);
                    Require(revealCalls == 1 && preview.Text == revealed && details.IsExpanded &&
                        Equals(toggle.Content, "Hide credentials"),
                        "Only an explicit Show credentials action should reveal the prepared values.");

                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    LayoutValidation.CheckWindowContent(window);
                    CheckFooter(window);
                    Require(revealCalls == 1 && preview.Text == masked && Equals(toggle.Content, "Show credentials"),
                        "Hiding credentials must restore the masked text without generating another preview.");

                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(revealCalls == 1 && preview.Text == revealed,
                        "Repeated reveal actions must reuse the same prepared preview within this window.");
                    toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(preview.Text == masked, "Credentials must be maskable after repeated reveal actions.");
                    layouts++;
                }
                catch (Exception error)
                {
                    throw new InvalidDataException($"Profile preview, {(UiFixture.Dark ? "dark" : "light")}, {textScale:P0} text, {(minimum ? "minimum" : "default")}: {error.Message}", error);
                }
                finally { window.Close(); }
            }
            return layouts;
        }
        finally { UiFixture.SetTextScale(previousTextScale); }
    }
}
