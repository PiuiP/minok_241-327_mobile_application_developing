namespace PhotoGeoEditor.WinForms;

internal static class Program
{
    /// <summary>
    /// Параметры командной строки GUI:
    ///   PhotoGeoEditor.Gui.exe [папка | файл.jpg | проект.pgeo.json] [--dark] [--help]
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}
