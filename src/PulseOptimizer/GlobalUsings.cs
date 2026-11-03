// WPF projects drop System.IO from the automatic imports (to avoid a clash with
// System.Windows.Shapes.Path). Pulse never uses Shapes in C#, so add it back here.
global using System.IO;
