// PlatformInvokes.cs created by tobias at 13:30 PM 2/15/2009
// DllImports from libX11 for PoliteRegister and future implementations

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IronAHK.Rusty.Linux
{
    abstract class PlatformInvokes
    {
        [DllImport("libX11")]
        protected static extern IntPtr XOpenDisplay(IntPtr From);

        [DllImport("libX11")]
        protected static extern void XCloseDisplay(IntPtr Dpy);

        [DllImport("libX11")]
        protected static extern int XDefaultRootWindow(IntPtr Display);

        [DllImport("libX11")]
        protected static extern IntPtr XSelectInput(IntPtr Display, int Window, EventMasks EventMask);
        
        [DllImport("libX11")]
        protected static extern int XQueryTree(IntPtr display, int w, out int root_return, out int parent_return, 
                                             out IntPtr children_return,  out int nchildren_return);
            
        [DllImport("libX11")]
        protected static extern void XNextEvent(IntPtr Display, ref XEvent Event);
        
        [DllImport("libX11")]
        protected static extern int XLookupString(ref XEvent Key, StringBuilder Buffer, int Count, IntPtr KeySym, IntPtr Useless);
        
        [DllImport("libX11")]
        protected static extern XErrorHandler XSetErrorHandler(XErrorHandler Handler);
        
        [DllImport("libX11")]
        protected static extern uint XStringToKeysym(string Convert);
        
        [DllImport("libXtst.so.6")]
        protected extern static void XTestFakeKeyEvent(IntPtr Display, uint KeyCode, bool isPress, ulong delay);
        
        [DllImport("libX11")]
        protected extern static uint XKeysymToKeycode(IntPtr Display, uint Keysym);
    }
}