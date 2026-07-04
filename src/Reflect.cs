using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFile("C:\\Fortnite_Video_Software - C#\\src\\FortniteVideoSoftware.App\\bin\\Debug\\net9.0-windows\\win-x64\\Vortice.Direct3D11.dll");
        var t = asm.GetType("Vortice.Direct3D11.ID3D11DeviceContext");
        foreach (var m in t.GetMethods()) {
            if (m.Name == "GetData") {
                Console.WriteLine(m);
            }
        }
    }
}
