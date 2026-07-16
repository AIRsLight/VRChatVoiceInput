namespace VRChatVoiceInput.App;

internal interface ISettingsWindow
{
    Task<bool> CloseAfterSavingAsync();

    void ShowUnhandledError(Exception exception);
}
