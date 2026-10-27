"""Prints the WM_CLASS class of every top-level window on $DISPLAY, one per line (no X11 tools needed).

Used by smoke-linux.sh to prove the window's class equals StartupWMClass in pennyworth.desktop.
"""
import ctypes
import sys


class ClassHint(ctypes.Structure):
    _fields_ = [('res_name', ctypes.c_char_p), ('res_class', ctypes.c_char_p)]


x11 = ctypes.CDLL('libX11.so.6')
x11.XOpenDisplay.restype = ctypes.c_void_p
x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
x11.XDefaultRootWindow.restype = ctypes.c_ulong
x11.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
x11.XQueryTree.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_ulong),
                           ctypes.POINTER(ctypes.POINTER(ctypes.c_ulong)), ctypes.POINTER(ctypes.c_uint)]
x11.XGetClassHint.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ClassHint)]

display = x11.XOpenDisplay(None)
if not display:
    sys.exit('cannot open the display')
root, parent, count = ctypes.c_ulong(), ctypes.c_ulong(), ctypes.c_uint()
children = ctypes.POINTER(ctypes.c_ulong)()
x11.XQueryTree(display, x11.XDefaultRootWindow(display), ctypes.byref(root), ctypes.byref(parent), ctypes.byref(children), ctypes.byref(count))
for index in range(count.value):
    hint = ClassHint()
    if x11.XGetClassHint(display, children[index], ctypes.byref(hint)):
        print(hint.res_class.decode())
