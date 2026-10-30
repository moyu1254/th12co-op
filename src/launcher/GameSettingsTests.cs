namespace TH12Coop;

internal static partial class SelfTests
{
    private static async Task GameSettingsTests(List<string> passed, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            dispatcher.BeginInvoke(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "TH12Coop-settings-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var bytes = await GameConfiguration.Template(token);
                    var path = Path.Combine(folder, "th12.cfg");
                    await GameConfiguration.Save(path, bytes, token);
                    // Exercise real async I/O under the same STA message-pump
                    // context as the button. A constructor must never block
                    // this context waiting for an I/O continuation to run.
                    var loaded = await GameConfiguration.ReadLocal(folder, token);
                    using (var dialog = new GameSettingsForm(folder, loaded))
                    {
                        Expect(SynchronizationContext.Current is WindowsFormsSynchronizationContext, "settings runs on the UI context");
                        Expect(dialog.Display.SelectedIndex == bytes[0x1d], "settings screen loads display");
                        Expect(dialog.Bgm.Value == GameConfiguration.EffectiveVolume(bytes, true), "settings screen loads music");
                        dialog.Display.SelectedIndex = 3; dialog.Prompt.Checked = false;
                        dialog.Bgm.Value = 35; dialog.Sound.Value = 0;
                        var concurrent = (byte[])bytes.Clone(); concurrent[0x1e] = 2;
                        await GameConfiguration.Save(path, concurrent, token);
                        await dialog.SaveConfiguration(token);
                    }
                    using (var reopened = new GameSettingsForm(folder, await GameConfiguration.ReadLocal(folder, token)))
                    {
                        Expect(reopened.Display.SelectedIndex == 3 && !reopened.Prompt.Checked && reopened.Bgm.Value == 35 && reopened.Sound.Value == 0,
                            "settings changes survive save and reopening");
                        await reopened.SaveConfiguration(token);
                    }
                    var saved = await GameConfiguration.ReadLocal(folder, token);
                    Expect(saved[0x1e] == 2, "settings save merges concurrent unrelated options");
                    Expect(saved[0x1c] == 0, "settings mute survives reopening");
                    completion.SetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { Directory.Delete(folder, true); Application.ExitThread(); }
            });
            Application.Run(); // Hidden dispatcher only; no activation or visible window.
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        passed.Add("Game settings on an STA UI message pump: asynchronous load, form construction, edits, save, reopen and concurrent-field merge without UI blocking");
    }
}
