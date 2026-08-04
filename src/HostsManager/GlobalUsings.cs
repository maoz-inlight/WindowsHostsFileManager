// Enabling UseWindowsForms for the tray NotifyIcon brings System.Windows.Forms into the
// implicit usings, which collides with WPF on several type names. This app is WPF first,
// so the ambiguous names resolve to WPF everywhere; TrayIcon.cs fully qualifies the
// handful of WinForms types it needs.

global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Clipboard = System.Windows.Clipboard;
