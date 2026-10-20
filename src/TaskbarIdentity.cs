using System;
using System.Runtime.InteropServices;

namespace BitPerfectPlayer
{
    /// <summary>
    /// Gives SoDa Player its own taskbar identity (AppUserModelID). Without one, Windows groups the window by whatever
    /// launched it: started from a packaged app (for example a link opened from another program), the window got no taskbar
    /// button of its own, so the icon never showed. The window also carries relaunch info so a pinned button keeps the icon.
    /// </summary>
    internal static class TaskbarIdentity
    {
        public const string AppId = "SoDa.SoDaPlayer";

        [DllImport("shell32.dll", PreserveSig = true)]
        static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [DllImport("shell32.dll", PreserveSig = true)]
        static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);

        static readonly Guid FMTID_AppUserModel = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

        /// <summary>Call first thing in Main, before any window exists.</summary>
        public static void ApplyToProcess()
        {
            try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { }
        }

        /// <summary>Stamps the window with the app id plus relaunch command, name and icon (used when it is pinned).</summary>
        public static void ApplyToWindow(IntPtr hwnd)
        {
            IPropertyStore store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return;
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                Set(store, 5, AppId);                        // PKEY_AppUserModel_ID
                Set(store, 2, "\"" + exe + "\"");            // PKEY_AppUserModel_RelaunchCommand
                Set(store, 4, "SoDa Player");                // PKEY_AppUserModel_RelaunchDisplayNameResource
                Set(store, 3, exe + ",0");                   // PKEY_AppUserModel_RelaunchIconResource (the embedded can icon)
                store.Commit();
            }
            catch { }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        static void Set(IPropertyStore store, int pid, string value)
        {
            var key = new PropertyKey(FMTID_AppUserModel, pid);
            var pv = new PropVariant { VarType = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(value) };
            try { store.SetValue(ref key, ref pv); }
            finally { NativeMethods.PropVariantClear(ref pv); }
        }
    }
}
