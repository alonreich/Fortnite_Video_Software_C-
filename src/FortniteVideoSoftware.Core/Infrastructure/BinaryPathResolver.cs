using System;
using System.IO;

namespace FortniteVideoSoftware.Core.Infrastructure;

public static class BinaryPathResolver
{
    public static string Resolve(string filename, params string[] fallbackDirs)
    {
        string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        
        foreach (string dir in fallbackDirs)
        {
            string path1 = Path.Combine(baseDir, dir, filename);
            if (File.Exists(path1)) return path1;
            
            string path2 = Path.Combine(baseDir, "..", "..", "..", "..", "..", dir, filename);
            if (File.Exists(path2)) return path2;
        }
        
        return filename;
    }
}
