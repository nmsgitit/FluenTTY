// The WindowsDesktop SDK leaves System.IO out of the implicit usings (Path clashes with
// System.Windows.Shapes.Path). Nothing here imports System.Windows.Shapes, so bring it back.
global using System.IO;
