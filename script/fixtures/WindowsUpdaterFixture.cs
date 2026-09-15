using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

internal static class UpdateChecks
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CanExit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RunInstaller([MarshalAs(UnmanagedType.LPWStr)] string path);
    public static int Main(string[] args) { return Native(args[0], args[1], bool.Parse(args[2])); }
    public static int Native(string feed, string publicKey, bool tampered)
    {
        var registry = @"Software\YakLang\YTrayUpdaterFixture-" + Guid.NewGuid().ToString("N");
        var done = new ManualResetEventSlim(); bool accepted = false, failed = false;
        Callback error = () => { failed = true; done.Set(); };
        Callback shutdown = () => { };
        CanExit canExit = () => 1;
        RunInstaller installer = path => { accepted = File.Exists(path); done.Set(); return 1; };
        try
        {
            win_sparkle_set_registry_path(registry);
            win_sparkle_set_app_details("YakLang test fixture", "Update test", "1.0.0");
            win_sparkle_set_appcast_url(feed);
            if (win_sparkle_set_eddsa_public_key(publicKey) != 1) throw new Exception("Test update key rejected");
            win_sparkle_set_automatic_check_for_updates(0);
            win_sparkle_set_error_callback(error); win_sparkle_set_shutdown_request_callback(shutdown);
            win_sparkle_set_can_shutdown_callback(canExit); win_sparkle_set_user_run_installer_callback(installer);
            win_sparkle_init(); win_sparkle_check_update_with_ui_and_install();
            if (!done.Wait(TimeSpan.FromSeconds(45))) throw new Exception("Native updater timed out");
            if (tampered ? (!failed || accepted) : (!accepted || failed)) throw new Exception("Unexpected native signature result");
            Console.WriteLine(tampered ? "WinSparkle rejected altered payload; installer never ran" : "WinSparkle verified download and handed off installer; test intercepted execution");
            return 0;
        }
        catch (Exception errorResult) { Console.Error.WriteLine(errorResult); return 1; }
        finally
        {
            win_sparkle_cleanup(); GC.KeepAlive(new Delegate[] { error, shutdown, canExit, installer });
            Registry.CurrentUser.DeleteSubKeyTree(registry, false); done.Dispose();
        }
    }
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void win_sparkle_init();
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void win_sparkle_cleanup();
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)] private static extern void win_sparkle_set_app_details(string company,string app,string version);
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi)] private static extern void win_sparkle_set_registry_path(string path);
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi)] private static extern void win_sparkle_set_appcast_url(string url);
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi)] private static extern int win_sparkle_set_eddsa_public_key(string key);
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void win_sparkle_set_automatic_check_for_updates(int value);
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void win_sparkle_check_update_with_ui_and_install();
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void win_sparkle_set_error_callback(Callback callback);
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void win_sparkle_set_shutdown_request_callback(Callback callback);
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void win_sparkle_set_can_shutdown_callback(CanExit callback);
    [DllImport("WinSparkle.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void win_sparkle_set_user_run_installer_callback(RunInstaller callback);
}
