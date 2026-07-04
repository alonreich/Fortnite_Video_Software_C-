using System;
using System.IO;

class Program {
    static void Main() {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string myVideos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string[] probes = new[]
        {
            System.IO.Path.Combine(localAppData, ""Temp"", ""Highlights"", ""Fortnite""),
            System.IO.Path.Combine(localAppData, ""Temp"", ""Highlights""),
            System.IO.Path.Combine(localAppData, ""NVIDIA Corporation"", ""GeForce Experience"", ""Highlights""),
            System.IO.Path.Combine(myVideos, ""Highlights"", ""Fortnite""),
            System.IO.Path.Combine(myVideos, ""Fortnite""),
            System.IO.Path.Combine(myVideos, ""Highlights""),
            System.IO.Path.Combine(myDocuments, ""Highlights"")
        };
        string startPath = myVideos;
        foreach (var probe in probes) {
            if (Directory.Exists(probe)) {
                startPath = probe;
                break;
            }
        }
        Console.WriteLine(startPath);
    }
}
