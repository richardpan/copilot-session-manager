using System;
using System.Threading;
using Xunit.Sdk;

namespace CopilotSessionManager.Terminal.Wpf.Tests;

/// <summary>
/// Runs an action on a dedicated single-threaded apartment thread.
/// WPF visual construction tolerates MTA in many cases, but
/// <see cref="System.Windows.Media.Imaging.RenderTargetBitmap"/> and the
/// dispatcher infrastructure are happier on STA, so the harness uses it
/// universally.
/// </summary>
internal static class StaRunner
{
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw new XunitException("STA test body threw: " + captured.Message);
        }
    }
}
