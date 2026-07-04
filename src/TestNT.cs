using System;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;

class P {
    static void Main() {
        var res = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out _);
        if (res.Failure) { Console.WriteLine("Device creation failed"); return; }
        
        var desc = new Texture2DDescription {
            Width = 100, Height = 100, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.SharedNTHandle
        };
        try {
            var tex = device.CreateTexture2D(desc);
            Console.WriteLine("Success! NTHandle works WITHOUT KeyedMutex!");
        } catch (Exception ex) {
            Console.WriteLine("Failed: " + ex.Message);
        }
    }
}
