// WindowsDesktop SDK 的隐式 using 不含 System.IO，且 UseWindowsForms 引入的
// System.Drawing / System.Windows.Forms 会与 WPF 的同名类型冲突，这里统一消解。
global using System.IO;
global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Color = System.Windows.Media.Color;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
