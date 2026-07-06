using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

class Program
{
    static void Main()
    {
        string json = ""{\""CustomMusicDirectory\"": \""C:\\\\Fortnite_Video_Software - C#\\\\mp3\""}"";
        try {
            var state = JsonSerializer.Deserialize<JsonObject>(json);
            Console.WriteLine(""Success: "" + state?[""CustomMusicDirectory""]?.GetValue<string>());
        } catch (Exception ex) {
            Console.WriteLine(""Error: "" + ex.Message);
        }
    }
}
